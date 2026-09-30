import { ApiClientError, type AuthApi } from "../api/auth-api";
import {
  runWithRequestScope,
  runWithSignal,
} from "../api/operation-runtime";
import type { AuthStorage } from "./secure-auth-storage";
import type { AuthCredentials, AuthSession } from "./auth-session-types";

export interface AuthSessionCoordinator {
  restore(signal?: AbortSignal): Promise<AuthSession | null>;
  signIn(credentials: AuthCredentials, signal?: AbortSignal): Promise<AuthSession>;
  signOut(session: AuthSession | null, signal?: AbortSignal): Promise<void>;
  synchronize(session: AuthSession, signal?: AbortSignal): Promise<AuthSession>;
}

export interface AuthSessionControllerDependencies {
  readonly api: AuthApi;
  readonly createInstallationId?: () => string;
  readonly now?: () => number;
  readonly storage: AuthStorage;
}

export class DefaultAuthSessionCoordinator implements AuthSessionCoordinator {
  private readonly api: AuthApi;
  private readonly createInstallationId: () => string;
  private readonly now: () => number;
  private readonly storage: AuthStorage;
  private operationVersion = 0;
  private storageOperation: Promise<void> = Promise.resolve();

  public constructor(dependencies: AuthSessionControllerDependencies) {
    this.api = dependencies.api;
    this.createInstallationId = dependencies.createInstallationId ?? createInstallationId;
    this.now = dependencies.now ?? (() => Date.now());
    this.storage = dependencies.storage;
  }

  public async restore(signal?: AbortSignal): Promise<AuthSession | null> {
    const operationVersion = this.beginOperation();
    const storedSession = await runWithSignal(
      () => this.storage.loadSession(),
      signal,
    );
    this.throwIfSuperseded(operationVersion, signal);

    if (!storedSession) {
      return null;
    }

    if (isExpired(storedSession.refreshTokenExpiresAt, this.now())) {
      await this.clearSessionIfCurrent(operationVersion, signal);
      return null;
    }

    if (isExpired(storedSession.accessTokenExpiresAt, this.now())) {
      return this.refreshSession(storedSession, operationVersion, signal);
    }

    return storedSession;
  }

  public async signIn(credentials: AuthCredentials, signal?: AbortSignal): Promise<AuthSession> {
    const operationVersion = this.beginOperation();
    const installationId = await this.getOrCreateInstallationId(signal);
    this.throwIfSuperseded(operationVersion, signal);
    const tokens = await runWithSignal(
      () => this.api.login(
        {
          email: credentials.email,
          password: credentials.password,
        },
        installationId,
        signal,
      ),
      signal,
    );
    this.throwIfSuperseded(operationVersion, signal);
    const actor = await runWithSignal(
      () => this.api.getCurrentActor(tokens.accessToken, signal),
      signal,
    );
    this.throwIfSuperseded(operationVersion, signal);

    const session: AuthSession = {
      ...tokens,
      actor,
      installationId,
    };

    await this.saveSessionIfCurrent(session, operationVersion, signal);
    return session;
  }

  public async signOut(session: AuthSession | null, signal?: AbortSignal): Promise<void> {
    this.beginOperation();
    const remoteSession = session
      ? {
          installationId: session.installationId,
          refreshToken: session.refreshToken,
        }
      : null;
    const localDeletion = runWithRequestScope(
      (localSignal) =>
        this.runStorageOperation(
          () => this.storage.clearSession(),
          localSignal,
        ),
      signal,
    );
    const remoteRevocation = remoteSession
      ? runWithRequestScope(
          (remoteSignal) =>
            this.api.logoutSession(
              remoteSession.refreshToken,
              remoteSession.installationId,
              remoteSignal,
            ),
          signal,
        ).catch(() => undefined)
      : Promise.resolve();
    const [localResult] = await Promise.allSettled([
      localDeletion,
      remoteRevocation,
    ]);

    if (localResult.status === "rejected") {
      throw localResult.reason;
    }
  }

  public async synchronize(session: AuthSession, signal?: AbortSignal): Promise<AuthSession> {
    this.throwIfSignalAborted(signal);
    const operationVersion = this.beginOperation();
    return this.synchronizeSession(session, operationVersion, signal);
  }

  private async synchronizeSession(
    session: AuthSession,
    operationVersion: number,
    signal?: AbortSignal,
  ): Promise<AuthSession> {
    if (isExpired(session.refreshTokenExpiresAt, this.now())) {
      await this.clearSessionIfCurrent(operationVersion, signal);
      throw createExpiredSessionError();
    }

    if (isExpired(session.accessTokenExpiresAt, this.now())) {
      return this.refreshSession(session, operationVersion, signal);
    }

    try {
      const actor = await runWithSignal(
        () => this.api.getCurrentActor(session.accessToken, signal),
        signal,
      );
      this.throwIfSuperseded(operationVersion, signal);

      if (isSameActor(session.actor, actor)) {
        return session;
      }

      const updatedSession: AuthSession = {
        ...session,
        actor,
      };

      await this.saveSessionIfCurrent(updatedSession, operationVersion, signal);
      return updatedSession;
    } catch (error) {
      if (isAbortError(error) || signal?.aborted) {
        throw createSupersededError();
      }

      if (error instanceof ApiClientError && error.status === 401) {
        return this.refreshSession(session, operationVersion, signal);
      }

      return session;
    }
  }

  private async getOrCreateInstallationId(
    signal?: AbortSignal,
  ): Promise<string> {
    const existingInstallationId = await runWithSignal(
      () => this.storage.loadInstallationId(),
      signal,
    );

    if (existingInstallationId) {
      return existingInstallationId;
    }

    const installationId = this.createInstallationId();
    await runWithSignal(
      () => this.storage.saveInstallationId(installationId),
      signal,
    );
    return installationId;
  }

  private async refreshSession(
    session: AuthSession,
    operationVersion: number,
    signal?: AbortSignal,
  ): Promise<AuthSession> {
    let rotatedSession: AuthSession | null = null;

    try {
      const rotatedTokens = await runWithSignal(
        () => this.api.refreshSession(
          session.refreshToken,
          session.installationId,
          signal,
        ),
        signal,
      );
      this.throwIfSuperseded(operationVersion, signal);

      rotatedSession = {
        ...rotatedTokens,
        actor: session.actor,
        installationId: session.installationId,
      };

      await this.saveSessionIfCurrent(rotatedSession, operationVersion, signal);
    } catch (error) {
      if (isAbortError(error) || signal?.aborted) {
        throw createSupersededError();
      }

      if (!isAbortError(error)) {
        await this.clearSessionIfCurrent(operationVersion, signal);
      }

      throw error;
    }

    if (!rotatedSession) {
      throw new Error("Rotated session was not created after a successful refresh.");
    }

    try {
      const actor = await runWithSignal(
        () => this.api.getCurrentActor(rotatedSession.accessToken, signal),
        signal,
      );
      this.throwIfSuperseded(operationVersion, signal);

      if (isSameActor(rotatedSession.actor, actor)) {
        return rotatedSession;
      }

      const updatedSession: AuthSession = {
        ...rotatedSession,
        actor,
      };

      await this.saveSessionIfCurrent(updatedSession, operationVersion, signal);
      return updatedSession;
    } catch (error) {
      if (error instanceof ApiClientError && error.status === 401) {
        await this.clearSessionIfCurrent(operationVersion, signal);
        throw error;
      }

      if (isTimeoutAbort(error, signal)) {
        return rotatedSession;
      }

      if (isAbortError(error) || signal?.aborted) {
        throw createSupersededError();
      }

      return rotatedSession;
    }
  }

  private beginOperation(): number {
    this.operationVersion += 1;
    return this.operationVersion;
  }

  private async saveSessionIfCurrent(
    session: AuthSession,
    operationVersion: number,
    signal?: AbortSignal,
  ): Promise<void> {
    await this.runStorageOperation(async () => {
      this.throwIfSuperseded(operationVersion, signal);
      await this.storage.saveSession(session);
      this.throwIfSuperseded(operationVersion, signal);
    }, signal);
  }

  private async clearSessionIfCurrent(
    operationVersion: number,
    signal?: AbortSignal,
  ): Promise<void> {
    await this.runStorageOperation(async () => {
      this.throwIfSuperseded(operationVersion, signal);
      await this.storage.clearSession();
      this.throwIfSuperseded(operationVersion, signal);
    }, signal);
  }

  private async runStorageOperation<T>(
    operation: () => Promise<T>,
    signal?: AbortSignal,
  ): Promise<T> {
    const execute = (): Promise<T> => {
      this.throwIfSignalAborted(signal);
      return operation();
    };
    const result = this.storageOperation.then(execute, execute);
    this.storageOperation = result.then(
      () => undefined,
      () => undefined,
    );
    return runWithSignal(() => result, signal);
  }

  private throwIfSuperseded(
    operationVersion: number,
    signal?: AbortSignal,
  ): void {
    if (signal?.aborted || operationVersion !== this.operationVersion) {
      throw createSupersededError();
    }
  }

  private throwIfSignalAborted(signal?: AbortSignal): void {
    if (signal?.aborted) {
      throw createSupersededError();
    }
  }
}

export function createInstallationId(): string {
  const cryptoLike = globalThis.crypto as
    | {
        getRandomValues?: (values: Uint8Array) => Uint8Array;
        randomUUID?: () => string;
      }
    | undefined;

  if (typeof cryptoLike?.randomUUID === "function") {
    return cryptoLike.randomUUID();
  }

  const randomBytes = new Uint8Array(16);

  if (typeof cryptoLike?.getRandomValues === "function") {
    cryptoLike.getRandomValues(randomBytes);
  } else {
    for (let index = 0; index < randomBytes.length; index += 1) {
      randomBytes[index] = Math.floor(Math.random() * 256);
    }
  }

  randomBytes[6] = (randomBytes[6]! & 0x0f) | 0x40;
  randomBytes[8] = (randomBytes[8]! & 0x3f) | 0x80;

  const hex = Array.from(randomBytes, (value) => value.toString(16).padStart(2, "0")).join("");

  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export function isAbortError(error: unknown): boolean {
  return error instanceof Error && error.name === "AbortError";
}

function isExpired(isoTimestamp: string, now: number): boolean {
  const expiresAt = Date.parse(isoTimestamp);
  return Number.isNaN(expiresAt) || expiresAt <= now;
}

function isSameActor(left: AuthSession["actor"], right: AuthSession["actor"]): boolean {
  return (
    left.membership.id === right.membership.id &&
    left.membership.displayName === right.membership.displayName &&
    left.membership.eligibleAsNamedParticipant ===
      right.membership.eligibleAsNamedParticipant &&
    left.organization.id === right.organization.id &&
    left.organization.name === right.organization.name &&
    left.organization.timeZone === right.organization.timeZone &&
    left.roles.length === right.roles.length &&
    left.roles.every((role, index) => right.roles[index] === role)
  );
}

function isTimeoutAbort(error: unknown, signal?: AbortSignal): boolean {
  return (
    isTimeoutError(error) ||
    (signal?.aborted === true && isTimeoutError(signal.reason))
  );
}

function isTimeoutError(error: unknown): boolean {
  return error instanceof Error && error.name === "TimeoutError";
}

function createExpiredSessionError(): ApiClientError {
  return new ApiClientError({
    status: 401,
    message: "The authentication session has expired.",
  });
}

function createSupersededError(): Error {
  const error = new Error("The authentication operation was superseded.");
  error.name = "AbortError";
  return error;
}
