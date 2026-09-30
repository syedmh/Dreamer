import { beforeEach, describe, expect, it, jest } from "@jest/globals";

import * as secureStoreModule from "../../../src/core/security/expoSecureStore";
import {
  SecureStorePendingSignupStorage,
  type PendingSignupCommand,
} from "../../../src/core/security/pending-signup-storage";

jest.mock("../../../src/core/security/expoSecureStore", () => ({
  WHEN_UNLOCKED_THIS_DEVICE_ONLY: 6,
  deleteItemAsync: jest.fn(),
  getItemAsync: jest.fn(),
  setItemAsync: jest.fn(),
}));

const mockSecureStore = secureStoreModule as jest.Mocked<
  typeof secureStoreModule
>;

const command: PendingSignupCommand = {
  body: {
    kind: "household",
    label: "Household 2",
    memberParticipantIds: ["22222222-2222-4222-8222-222222222222"],
    unnamedParticipantCount: 1,
  },
  category: "serving",
  createdAt: "2026-08-17T10:00:00.000Z",
  helpNeedId: "need-1",
  idempotencyKey: "11111111-1111-4111-8111-111111111111",
  organizationId: "organization-1",
  primaryMembershipId: "membership-1",
  serviceDateId: "date-1",
  version: 1,
};

describe("SecureStorePendingSignupStorage", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("uses one device-only SecureStore slot", async () => {
    const storage = new SecureStorePendingSignupStorage();

    await expect(storage.admit(command)).resolves.toMatchObject({
      type: "admitted",
    });

    expect(mockSecureStore.setItemAsync).toHaveBeenCalledWith(
      "tabruk.signup.pending.v1",
      JSON.stringify(command),
      { keychainAccessible: 6 },
    );
  });

  it("purges malformed and cross-identity commands", async () => {
    const storage = new SecureStorePendingSignupStorage();
    mockSecureStore.getItemAsync
      .mockResolvedValueOnce("{invalid")
      .mockResolvedValueOnce(JSON.stringify(command));

    await expect(storage.load()).resolves.toBeNull();
    await expect(
      storage.loadForIdentity("other-organization", "membership-1"),
    ).resolves.toBeNull();

    expect(mockSecureStore.deleteItemAsync).toHaveBeenCalledTimes(2);
  });

  it("restores only the exact organization and primary membership", async () => {
    const storage = new SecureStorePendingSignupStorage();
    mockSecureStore.getItemAsync.mockResolvedValueOnce(JSON.stringify(command));

    await expect(
      storage.loadForIdentity("organization-1", "membership-1"),
    ).resolves.toEqual({ command, epoch: 0 });
  });

  it("atomically admits only one command for concurrent callers", async () => {
    const storage = new SecureStorePendingSignupStorage();
    let stored: string | null = null;
    mockSecureStore.getItemAsync.mockImplementation(async () => stored);
    mockSecureStore.setItemAsync.mockImplementation(async (_key, value) => {
      stored = value;
    });

    const second = {
      ...command,
      helpNeedId: "need-2",
      idempotencyKey: "22222222-2222-4222-8222-222222222222",
    };
    const admissions = await Promise.all([
      storage.admit(command),
      storage.admit(second),
    ]);

    expect(admissions.map((admission) => admission.type).sort()).toEqual([
      "admitted",
      "existing",
    ]);
    expect(admissions[1]?.slot.command).toEqual(command);
    expect(mockSecureStore.setItemAsync).toHaveBeenCalledTimes(1);
  });

  it("invalidates a delayed admission before purge deletes the slot", async () => {
    const storage = new SecureStorePendingSignupStorage();
    const delayedLoad = createDeferred<string | null>();
    mockSecureStore.getItemAsync.mockReturnValueOnce(delayedLoad.promise);
    const admission = storage.admit(command);
    await Promise.resolve();
    const purging = storage.purge();
    delayedLoad.resolve(null);

    await expect(admission).rejects.toMatchObject({ name: "AbortError" });
    await expect(purging).resolves.toBeUndefined();
    expect(mockSecureStore.setItemAsync).not.toHaveBeenCalled();
    expect(mockSecureStore.deleteItemAsync).toHaveBeenCalledTimes(1);
  });

  it("prevents conditional writes and clears from crossing a purge epoch", async () => {
    const storage = new SecureStorePendingSignupStorage();
    let stored: string | null = null;
    mockSecureStore.getItemAsync.mockImplementation(async () => stored);
    mockSecureStore.setItemAsync.mockImplementation(async (_key, value) => {
      stored = value;
    });
    mockSecureStore.deleteItemAsync.mockImplementation(async () => {
      stored = null;
    });
    const admitted = await storage.admit(command);

    await storage.purge();

    await expect(
      storage.replaceIfCurrent(admitted.slot, {
        ...command,
        nextAttemptAt: "2026-08-17T10:00:02.000Z",
      }),
    ).resolves.toBeNull();
    await expect(
      storage.clearIfCurrent(admitted.slot),
    ).resolves.toBe(false);
    expect(stored).toBeNull();
  });

  it("fails closed instead of returning a same-identity command after deletion fails", async () => {
    const storage = new SecureStorePendingSignupStorage();
    mockSecureStore.getItemAsync.mockResolvedValue(JSON.stringify(command));
    mockSecureStore.deleteItemAsync.mockRejectedValue(
      new Error("secure deletion failed"),
    );

    await expect(storage.purge()).rejects.toThrow("secure deletion failed");
    await expect(
      storage.loadForIdentity("organization-1", "membership-1"),
    ).rejects.toThrow("secure deletion failed");

    expect(mockSecureStore.getItemAsync).not.toHaveBeenCalled();
  });
});

function createDeferred<T>(): {
  readonly promise: Promise<T>;
  readonly resolve: (value: T | PromiseLike<T>) => void;
} {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}
