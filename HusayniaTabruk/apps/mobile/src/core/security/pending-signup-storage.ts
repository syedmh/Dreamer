import type {
  SubmitSignupRequest,
  SignupResponse,
} from "../api/generated/api-contract-client";
import {
  WHEN_UNLOCKED_THIS_DEVICE_ONLY,
  deleteItemAsync,
  getItemAsync,
  setItemAsync,
  type SecureStoreOptions,
} from "./expoSecureStore";

const pendingSignupKey = "tabruk.signup.pending.v1";

const secureStoreOptions: SecureStoreOptions = {
  keychainAccessible: WHEN_UNLOCKED_THIS_DEVICE_ONLY,
};

export type HelpCategory = SignupResponse["category"];

export interface PendingSignupCommand {
  readonly version: 1;
  readonly idempotencyKey: string;
  readonly organizationId: string;
  readonly primaryMembershipId: string;
  readonly serviceDateId: string;
  readonly helpNeedId: string;
  readonly category: HelpCategory;
  readonly body: SubmitSignupRequest;
  readonly createdAt: string;
  readonly nextAttemptAt?: string;
}

export interface PendingSignupSlot {
  readonly command: PendingSignupCommand;
  readonly epoch: number;
}

export interface PendingSignupAdmission {
  readonly slot: PendingSignupSlot;
  readonly type: "admitted" | "existing";
}

export interface PendingSignupStorage {
  admit(
    command: PendingSignupCommand,
    signal?: AbortSignal,
  ): Promise<PendingSignupAdmission>;
  clearIfCurrent(
    slot: PendingSignupSlot,
    signal?: AbortSignal,
  ): Promise<boolean>;
  load(signal?: AbortSignal): Promise<PendingSignupSlot | null>;
  loadForIdentity(
    organizationId: string,
    primaryMembershipId: string,
    signal?: AbortSignal,
  ): Promise<PendingSignupSlot | null>;
  purge(): Promise<void>;
  replaceIfCurrent(
    slot: PendingSignupSlot,
    command: PendingSignupCommand,
    signal?: AbortSignal,
  ): Promise<PendingSignupSlot | null>;
}

export class SecureStorePendingSignupStorage implements PendingSignupStorage {
  private deletionRequired = false;
  private epoch = 0;
  private storageOperation: Promise<void> = Promise.resolve();

  public async admit(
    command: PendingSignupCommand,
    signal?: AbortSignal,
  ): Promise<PendingSignupAdmission> {
    assertValidCommand(command);
    throwIfOperationAborted(signal);
    const operationEpoch = this.epoch;

    return this.runStorageOperation(async () => {
      this.assertCurrentEpoch(operationEpoch);
      throwIfOperationAborted(signal);
      const existing = await this.loadStoredCommand();
      this.assertCurrentEpoch(operationEpoch);
      throwIfOperationAborted(signal);

      if (existing && isSameIdentity(existing, command)) {
        return {
          slot: { command: existing, epoch: operationEpoch },
          type: "existing",
        };
      }

      let admittedEpoch = operationEpoch;
      if (existing) {
        this.epoch += 1;
        admittedEpoch = this.epoch;
        await this.deleteStoredCommand();
        throwIfOperationAborted(signal);
      }

      await setItemAsync(
        pendingSignupKey,
        JSON.stringify(command),
        secureStoreOptions,
      );
      this.assertCurrentEpoch(admittedEpoch);
      throwIfOperationAborted(signal);
      return {
        slot: { command, epoch: admittedEpoch },
        type: "admitted",
      };
    });
  }

  public async clearIfCurrent(
    slot: PendingSignupSlot,
    signal?: AbortSignal,
  ): Promise<boolean> {
    throwIfOperationAborted(signal);
    return this.runStorageOperation(async () => {
      if (slot.epoch !== this.epoch) {
        return false;
      }

      throwIfOperationAborted(signal);
      const stored = await this.loadStoredCommand();
      if (
        slot.epoch !== this.epoch ||
        !stored ||
        !isSameCommand(stored, slot.command)
      ) {
        return false;
      }

      throwIfOperationAborted(signal);
      await this.deleteStoredCommand();
      this.assertCurrentEpoch(slot.epoch);
      throwIfOperationAborted(signal);
      return true;
    });
  }

  public async load(
    signal?: AbortSignal,
  ): Promise<PendingSignupSlot | null> {
    throwIfOperationAborted(signal);
    const operationEpoch = this.epoch;
    return this.runStorageOperation(async () => {
      this.assertCurrentEpoch(operationEpoch);
      const command = await this.loadStoredCommand();
      this.assertCurrentEpoch(operationEpoch);
      throwIfOperationAborted(signal);
      return command ? { command, epoch: operationEpoch } : null;
    });
  }

  public async loadForIdentity(
    organizationId: string,
    primaryMembershipId: string,
    signal?: AbortSignal,
  ): Promise<PendingSignupSlot | null> {
    throwIfOperationAborted(signal);
    const operationEpoch = this.epoch;
    return this.runStorageOperation(async () => {
      this.assertCurrentEpoch(operationEpoch);
      const command = await this.loadStoredCommand();
      this.assertCurrentEpoch(operationEpoch);
      throwIfOperationAborted(signal);
      if (!command) {
        return null;
      }

      if (
        command.organizationId !== organizationId ||
        command.primaryMembershipId !== primaryMembershipId
      ) {
        this.epoch += 1;
        await this.deleteStoredCommand();
        return null;
      }

      return { command, epoch: operationEpoch };
    });
  }

  public purge(): Promise<void> {
    this.epoch += 1;
    this.deletionRequired = true;
    return this.runStorageOperation(() => this.deleteStoredCommand());
  }

  public async replaceIfCurrent(
    slot: PendingSignupSlot,
    command: PendingSignupCommand,
    signal?: AbortSignal,
  ): Promise<PendingSignupSlot | null> {
    assertValidCommand(command);
    throwIfOperationAborted(signal);
    return this.runStorageOperation(async () => {
      if (slot.epoch !== this.epoch) {
        return null;
      }

      const stored = await this.loadStoredCommand();
      if (
        slot.epoch !== this.epoch ||
        !stored ||
        !isSameCommand(stored, slot.command)
      ) {
        return null;
      }

      throwIfOperationAborted(signal);
      await setItemAsync(
        pendingSignupKey,
        JSON.stringify(command),
        secureStoreOptions,
      );
      this.assertCurrentEpoch(slot.epoch);
      throwIfOperationAborted(signal);
      return { command, epoch: slot.epoch };
    });
  }

  private async runStorageOperation<T>(
    operation: () => Promise<T>,
  ): Promise<T> {
    const result = this.storageOperation.then(operation, operation);
    this.storageOperation = result.then(
      () => undefined,
      () => undefined,
    );
    return result;
  }

  private async loadStoredCommand(): Promise<PendingSignupCommand | null> {
    if (this.deletionRequired) {
      await this.deleteStoredCommand();
      return null;
    }

    const raw = await getItemAsync(pendingSignupKey, secureStoreOptions);
    if (!raw) {
      return null;
    }

    try {
      const parsed = JSON.parse(raw) as unknown;
      if (isPendingSignupCommand(parsed)) {
        return parsed;
      }
    } catch {
      // Corrupt command data is purged without logging private signup content.
    }

    await this.deleteStoredCommand();
    return null;
  }

  private async deleteStoredCommand(): Promise<void> {
    this.deletionRequired = true;
    await deleteItemAsync(pendingSignupKey, secureStoreOptions);
    this.deletionRequired = false;
  }

  private assertCurrentEpoch(expectedEpoch: number): void {
    if (this.epoch === expectedEpoch) {
      return;
    }

    const error = new Error("The pending signup operation was superseded.");
    error.name = "AbortError";
    throw error;
  }
}

let defaultPendingSignupStorage: PendingSignupStorage | null = null;

export function getPendingSignupStorage(): PendingSignupStorage {
  if (!defaultPendingSignupStorage) {
    defaultPendingSignupStorage = new SecureStorePendingSignupStorage();
  }

  return defaultPendingSignupStorage;
}

function isPendingSignupCommand(value: unknown): value is PendingSignupCommand {
  if (!isRecord(value)) {
    return false;
  }

  return (
    value.version === 1 &&
    isNonEmptyString(value.idempotencyKey) &&
    isNonEmptyString(value.organizationId) &&
    isNonEmptyString(value.primaryMembershipId) &&
    isNonEmptyString(value.serviceDateId) &&
    isNonEmptyString(value.helpNeedId) &&
    isHelpCategory(value.category) &&
    isSubmitSignupRequest(value.body) &&
    isIsoTimestamp(value.createdAt) &&
    (value.nextAttemptAt === undefined || isIsoTimestamp(value.nextAttemptAt))
  );
}

function assertValidCommand(command: PendingSignupCommand): void {
  if (!isPendingSignupCommand(command)) {
    throw new Error("The pending signup command is invalid.");
  }
}

function isSameCommand(
  left: PendingSignupCommand,
  right: PendingSignupCommand,
): boolean {
  return (
    left.idempotencyKey === right.idempotencyKey &&
    isSameIdentity(left, right)
  );
}

function isSameIdentity(
  left: PendingSignupCommand,
  right: PendingSignupCommand,
): boolean {
  return (
    left.organizationId === right.organizationId &&
    left.primaryMembershipId === right.primaryMembershipId
  );
}

function throwIfOperationAborted(signal?: AbortSignal): void {
  if (!signal?.aborted) {
    return;
  }

  const error = new Error("The pending signup operation was superseded.");
  error.name = "AbortError";
  throw error;
}

function isSubmitSignupRequest(value: unknown): value is SubmitSignupRequest {
  if (!isRecord(value)) {
    return false;
  }

  return (
    (value.kind === "individual" ||
      value.kind === "household" ||
      value.kind === "team") &&
    (value.label === undefined ||
      value.label === null ||
      typeof value.label === "string") &&
    Array.isArray(value.memberParticipantIds) &&
    value.memberParticipantIds.every(isNonEmptyString) &&
    Number.isInteger(value.unnamedParticipantCount)
  );
}

function isHelpCategory(value: unknown): value is HelpCategory {
  return (
    value === "foodPreparation" ||
    value === "serving" ||
    value === "cleanup"
  );
}

function isIsoTimestamp(value: unknown): value is string {
  return isNonEmptyString(value) && !Number.isNaN(Date.parse(value));
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.length > 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}
