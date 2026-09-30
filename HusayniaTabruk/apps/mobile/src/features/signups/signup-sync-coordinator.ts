import {
  ApiClientError,
  type SignupResponse,
  type SubmitSignupRequest,
} from "../../core/api/auth-api";
import {
  createRequestScope,
  runWithSignal,
} from "../../core/api/operation-runtime";
import { createInstallationId } from "../../core/security/auth-session-controller";
import type {
  HelpCategory,
  PendingSignupCommand,
  PendingSignupSlot,
  PendingSignupStorage,
} from "../../core/security/pending-signup-storage";

const retryDelaysSeconds = [2, 5, 15, 30, 60] as const;
const stopReplayAfterMilliseconds = 23 * 60 * 60 * 1000;

export interface NewSignupCommand {
  readonly organizationId: string;
  readonly primaryMembershipId: string;
  readonly serviceDateId: string;
  readonly helpNeedId: string;
  readonly category: HelpCategory;
  readonly body: SubmitSignupRequest;
}

export type SignupSyncResult =
  | {
      readonly command: PendingSignupCommand;
      readonly message: string;
      readonly type: "pendingSync";
    }
  | {
      readonly signup: SignupResponse;
      readonly type: "confirmed";
    }
  | {
      readonly signup: SignupResponse;
      readonly type: "duplicate";
    }
  | {
      readonly message: string;
      readonly type: "permanentError";
    }
  | {
      readonly message: string;
      readonly type: "expired";
    };

const retryFlights = new WeakMap<
  PendingSignupStorage,
  Map<string, Promise<SignupSyncResult | null>>
>();

export interface SignupSyncCoordinatorDependencies {
  readonly createIdempotencyKey?: () => string;
  readonly now?: () => number;
  readonly reconcile: (
    command: PendingSignupCommand,
    signal?: AbortSignal,
  ) => Promise<SignupResponse | null>;
  readonly send: (
    command: PendingSignupCommand,
    signal?: AbortSignal,
  ) => Promise<SignupResponse>;
  readonly storage: PendingSignupStorage;
}

export interface SignupSyncSignals {
  readonly operationSignal?: AbortSignal;
  readonly requestSignal?: AbortSignal;
}

export class SignupSyncCoordinator {
  private readonly attempts = new Map<string, number>();
  private readonly createIdempotencyKey: () => string;
  private readonly now: () => number;
  private readonly reconcile: SignupSyncCoordinatorDependencies["reconcile"];
  private readonly send: SignupSyncCoordinatorDependencies["send"];
  private readonly storage: PendingSignupStorage;

  public constructor(dependencies: SignupSyncCoordinatorDependencies) {
    this.createIdempotencyKey =
      dependencies.createIdempotencyKey ?? createInstallationId;
    this.now = dependencies.now ?? (() => Date.now());
    this.reconcile = dependencies.reconcile;
    this.send = dependencies.send;
    this.storage = dependencies.storage;
  }

  public async submit(
    input: NewSignupCommand,
    signalsInput?: SignupSyncSignals | AbortSignal,
  ): Promise<SignupSyncResult> {
    const signals = normalizeSignals(signalsInput);
    throwIfOperationSuperseded(signals.operationSignal);
    const command: PendingSignupCommand = {
      version: 1,
      idempotencyKey: this.createIdempotencyKey(),
      organizationId: input.organizationId,
      primaryMembershipId: input.primaryMembershipId,
      serviceDateId: input.serviceDateId,
      helpNeedId: input.helpNeedId,
      category: input.category,
      body: input.body,
      createdAt: new Date(this.now()).toISOString(),
    };
    const admission = await this.storage.admit(
      command,
      signals.operationSignal,
    );
    throwIfOperationSuperseded(signals.operationSignal);

    if (admission.type === "existing") {
      return {
        command: admission.slot.command,
        message: "Another signup is Pending sync.",
        type: "pendingSync",
      };
    }

    return this.attempt(admission.slot, signals);
  }

  public async retry(
    organizationId: string,
    primaryMembershipId: string,
    signalsInput?: SignupSyncSignals | AbortSignal,
    force = false,
  ): Promise<SignupSyncResult | null> {
    const signals = normalizeSignals(signalsInput);
    throwIfOperationSuperseded(signals.operationSignal);
    const slot = await this.storage.loadForIdentity(
      organizationId,
      primaryMembershipId,
      signals.operationSignal,
    );
    throwIfOperationSuperseded(signals.operationSignal);
    if (!slot) {
      return null;
    }

    const flight = getOrCreateRetryFlight(
      this.storage,
      `${slot.epoch}:${slot.command.idempotencyKey}:${force ? "force" : "scheduled"}`,
      async () => {
        const scope = createRequestScope();
        try {
          return await this.retryLoadedSlot(
            slot,
            { requestSignal: scope.signal },
            force,
          );
        } finally {
          scope.dispose();
        }
      },
    );

    return runWithSignal(
      () => flight,
      signals.operationSignal,
    );
  }

  private async retryLoadedSlot(
    slot: PendingSignupSlot,
    signals: SignupSyncSignals,
    force: boolean,
  ): Promise<SignupSyncResult> {
    const reconciliation = await this.tryReconcileWithStatus(slot, signals);
    if (reconciliation.loaded && reconciliation.signup) {
      await this.clearSlot(slot, signals.operationSignal);
      this.attempts.delete(slot.command.idempotencyKey);
      return { signup: reconciliation.signup, type: "duplicate" };
    }

    if (hasReachedReplayCutoff(slot.command, this.now())) {
      return this.completeExpiredReconciliation(
        slot,
        reconciliation,
        signals,
      );
    }

    if (
      !force &&
      slot.command.nextAttemptAt &&
      Date.parse(slot.command.nextAttemptAt) > this.now()
    ) {
      return {
        command: slot.command,
        message: "Signup is Pending sync.",
        type: "pendingSync",
      };
    }

    return this.attempt(slot, signals);
  }

  private async attempt(
    slot: PendingSignupSlot,
    signals: SignupSyncSignals,
  ): Promise<SignupSyncResult> {
    const command = slot.command;
    throwIfOperationSuperseded(signals.operationSignal);
    if (hasReachedReplayCutoff(command, this.now())) {
      return this.reconcileExpired(slot, signals);
    }

    let signup: SignupResponse;
    try {
      signup = await this.send(command, signals.requestSignal);
    } catch (error) {
      throwIfOperationSuperseded(signals.operationSignal);
      if (isTimeoutAbort(signals.requestSignal)) {
        return this.retainForRetry(
          slot,
          "Signup is Pending sync.",
          undefined,
          signals.operationSignal,
        );
      }
      throwIfRequestSuperseded(signals.requestSignal);

      if (!(error instanceof ApiClientError)) {
        return this.retainForRetry(
          slot,
          "Signup is Pending sync.",
          undefined,
          signals.operationSignal,
        );
      }

      const code = error.problem?.code;
      if (error.status === 401) {
        throw error;
      }

      if (
        error.status === 429 ||
        error.status === 503 ||
        error.status === 412 ||
        code === "idempotency_in_progress" ||
        error.status >= 200 && error.status < 300
      ) {
        return this.retainForRetry(
          slot,
          "Signup is Pending sync.",
          error.retryAfterSeconds,
          signals.operationSignal,
        );
      }

      if (error.status === 409 && code === "signup_duplicate") {
        const existing = await this.tryReconcile(slot, signals);
        if (existing) {
          await this.clearSlot(slot, signals.operationSignal);
          this.attempts.delete(command.idempotencyKey);
          return { signup: existing, type: "duplicate" };
        }

        return this.retainForRetry(
          slot,
          "Signup is Pending sync while the existing request is loaded.",
          undefined,
          signals.operationSignal,
        );
      }

      if (error.status >= 400 && error.status < 500) {
        const reconciliation = await this.tryReconcileWithStatus(slot, signals);
        if (!reconciliation.loaded) {
          return this.retainForRetry(
            slot,
            "Signup is Pending sync while the current state is loaded.",
            undefined,
            signals.operationSignal,
          );
        }

        await this.clearSlot(slot, signals.operationSignal);
        this.attempts.delete(command.idempotencyKey);
        return {
          message:
            code === "category_closed"
              ? "This help request is no longer available. Review the current date before submitting again."
              : "The pending signup could not be submitted. Review the current date before submitting again.",
          type: "permanentError",
        };
      }

      return this.retainForRetry(
        slot,
        "Signup is Pending sync.",
        undefined,
        signals.operationSignal,
      );
    }

    throwIfOperationSuperseded(signals.operationSignal);
    if (isTimeoutAbort(signals.requestSignal)) {
      return this.retainForRetry(
        slot,
        "Signup is Pending sync.",
        undefined,
        signals.operationSignal,
      );
    }
    throwIfRequestSuperseded(signals.requestSignal);

    if (
      signup.status !== "pending" ||
      !isSignupForPendingCommand(signup, command)
    ) {
      return this.retainForRetry(
        slot,
        "Signup is Pending sync while its authoritative state is checked.",
        undefined,
        signals.operationSignal,
      );
    }

    await this.clearSlot(slot, signals.operationSignal);
    this.attempts.delete(command.idempotencyKey);
    return { signup, type: "confirmed" };
  }

  private async reconcileExpired(
    slot: PendingSignupSlot,
    signals: SignupSyncSignals,
  ): Promise<SignupSyncResult> {
    const reconciliation = await this.tryReconcileWithStatus(slot, signals);
    return this.completeExpiredReconciliation(
      slot,
      reconciliation,
      signals,
    );
  }

  private async completeExpiredReconciliation(
    slot: PendingSignupSlot,
    reconciliation: {
      readonly loaded: boolean;
      readonly signup: SignupResponse | null;
    },
    signals: SignupSyncSignals,
  ): Promise<SignupSyncResult> {
    const command = slot.command;
    if (!reconciliation.loaded) {
      return this.retainForRetry(
        slot,
        "Signup is Pending sync while the current state is loaded.",
        undefined,
        signals.operationSignal,
      );
    }

    await this.clearSlot(slot, signals.operationSignal);
    this.attempts.delete(command.idempotencyKey);
    if (reconciliation.signup) {
      return { signup: reconciliation.signup, type: "duplicate" };
    }

    return {
      message:
        "The pending signup is too old to replay. Review the current date and submit again.",
      type: "expired",
    };
  }

  private async tryReconcile(
    slot: PendingSignupSlot,
    signals: SignupSyncSignals,
  ): Promise<SignupResponse | null> {
    const result = await this.tryReconcileWithStatus(slot, signals);
    return result.signup;
  }

  private async tryReconcileWithStatus(
    slot: PendingSignupSlot,
    signals: SignupSyncSignals,
  ): Promise<{
    readonly loaded: boolean;
    readonly signup: SignupResponse | null;
  }> {
    try {
      const signup = await this.reconcile(
        slot.command,
        signals.requestSignal,
      );
      throwIfOperationSuperseded(signals.operationSignal);
      if (isTimeoutAbort(signals.requestSignal)) {
        return { loaded: false, signup: null };
      }
      throwIfRequestSuperseded(signals.requestSignal);
      return {
        loaded: true,
        signup:
          signup && isSignupForPendingCommand(signup, slot.command)
            ? signup
            : null,
      };
    } catch {
      throwIfOperationSuperseded(signals.operationSignal);
      if (!isTimeoutAbort(signals.requestSignal)) {
        throwIfRequestSuperseded(signals.requestSignal);
      }
      return { loaded: false, signup: null };
    }
  }

  private async retainForRetry(
    slot: PendingSignupSlot,
    message: string,
    retryAfterSeconds?: number,
    operationSignal?: AbortSignal,
  ): Promise<SignupSyncResult> {
    throwIfOperationSuperseded(operationSignal);
    const command = slot.command;
    const attempt = this.attempts.get(command.idempotencyKey) ?? 0;
    this.attempts.set(command.idempotencyKey, attempt + 1);
    const fallbackDelay =
      retryDelaysSeconds[Math.min(attempt, retryDelaysSeconds.length - 1)]!;
    const delaySeconds = retryAfterSeconds ?? fallbackDelay;
    const updated: PendingSignupCommand = {
      ...command,
      nextAttemptAt: new Date(this.now() + delaySeconds * 1000).toISOString(),
    };

    try {
      throwIfOperationSuperseded(operationSignal);
      const updatedSlot = await this.storage.replaceIfCurrent(
        slot,
        updated,
        operationSignal,
      );
      if (!updatedSlot) {
        throw createAbortError();
      }
      return {
        command: updatedSlot.command,
        message,
        type: "pendingSync",
      };
    } catch (error) {
      if (error instanceof Error && error.name === "AbortError") {
        throw error;
      }
      throwIfOperationSuperseded(operationSignal);
      return { command, message, type: "pendingSync" };
    }
  }

  private async clearSlot(
    slot: PendingSignupSlot,
    operationSignal?: AbortSignal,
  ): Promise<void> {
    throwIfOperationSuperseded(operationSignal);
    const cleared = await this.storage.clearIfCurrent(
      slot,
      operationSignal,
    );
    if (!cleared) {
      throw createAbortError();
    }
  }
}

export function isSignupForPendingCommand(
  signup: SignupResponse,
  command: PendingSignupCommand,
): boolean {
  return (
    signup.helpNeedId === command.helpNeedId &&
    signup.serviceDateId === command.serviceDateId &&
    signup.primaryContact.membershipId === command.primaryMembershipId
  );
}

function normalizeSignals(
  input?: SignupSyncSignals | AbortSignal,
): SignupSyncSignals {
  if (!input) {
    return {};
  }

  if ("aborted" in input) {
    return {
      operationSignal: input,
      requestSignal: input,
    };
  }

  return input;
}

function throwIfOperationSuperseded(signal?: AbortSignal): void {
  if (!signal?.aborted) {
    return;
  }

  throw createAbortError();
}

function throwIfRequestSuperseded(signal?: AbortSignal): void {
  if (!signal?.aborted || isTimeoutAbort(signal)) {
    return;
  }

  throw createAbortError();
}

function isTimeoutAbort(signal?: AbortSignal): boolean {
  return (
    signal?.aborted === true &&
    signal.reason instanceof Error &&
    signal.reason.name === "TimeoutError"
  );
}

function createAbortError(): Error {
  const error = new Error("The signup operation was superseded.");
  error.name = "AbortError";
  return error;
}

function getOrCreateRetryFlight(
  storage: PendingSignupStorage,
  key: string,
  operation: () => Promise<SignupSyncResult | null>,
): Promise<SignupSyncResult | null> {
  let storageFlights = retryFlights.get(storage);
  if (!storageFlights) {
    storageFlights = new Map();
    retryFlights.set(storage, storageFlights);
  }

  const existing = storageFlights.get(key);
  if (existing) {
    return existing;
  }

  const flight = operation();
  storageFlights.set(key, flight);
  const clear = (): void => {
    if (storageFlights?.get(key) === flight) {
      storageFlights.delete(key);
    }
  };
  void flight.then(clear, clear);
  return flight;
}

function hasReachedReplayCutoff(
  command: PendingSignupCommand,
  now: number,
): boolean {
  return now - Date.parse(command.createdAt) >= stopReplayAfterMilliseconds;
}
