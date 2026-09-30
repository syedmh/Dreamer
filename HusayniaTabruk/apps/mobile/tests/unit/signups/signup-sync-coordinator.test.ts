import { describe, expect, it, jest } from "@jest/globals";

import {
  ApiClientError,
  type ProblemDetails,
  type SignupResponse,
} from "../../../src/core/api/auth-api";
import { createRequestScope } from "../../../src/core/api/operation-runtime";
import type {
  PendingSignupAdmission,
  PendingSignupCommand,
  PendingSignupSlot,
  PendingSignupStorage,
} from "../../../src/core/security/pending-signup-storage";
import {
  SignupSyncCoordinator,
  type NewSignupCommand,
} from "../../../src/features/signups/signup-sync-coordinator";

const input: NewSignupCommand = {
  body: {
    kind: "individual",
    label: null,
    memberParticipantIds: [],
    unnamedParticipantCount: 0,
  },
  category: "serving",
  helpNeedId: "need-1",
  organizationId: "organization-1",
  primaryMembershipId: "membership-1",
  serviceDateId: "date-1",
};

const signup: SignupResponse = {
  category: "serving",
  helpNeedId: "need-1",
  id: "signup-1",
  kind: "individual",
  label: null,
  lastTransitionAt: null,
  memberParticipants: [],
  primaryContact: {
    displayName: "Primary Member",
    membershipId: "membership-1",
  },
  serviceDateId: "date-1",
  signupVersion: 1,
  status: "pending",
  submittedAt: "2026-08-17T10:00:00.000Z",
  totalParticipantCount: 1,
  unnamedParticipantCount: 0,
  version: 1,
  waitlistOrder: null,
};

describe("SignupSyncCoordinator", () => {
  it("admits only one command when two submitters race", async () => {
    const fixture = createStorage();
    const keys = [
      "11111111-1111-4111-8111-111111111111",
      "22222222-2222-4222-8222-222222222222",
    ];
    const send = jest.fn(async () => signup);
    const coordinator = new SignupSyncCoordinator({
      createIdempotencyKey: () => keys.shift()!,
      now: () => Date.parse("2026-08-17T10:00:00.000Z"),
      reconcile: async () => null,
      send,
      storage: fixture.storage,
    });

    const submissions = Promise.all([
      coordinator.submit(input),
      coordinator.submit({
        ...input,
        helpNeedId: "need-2",
      }),
    ]);
    const results = await submissions;

    expect(send).toHaveBeenCalledTimes(1);
    expect(fixture.storage.admit).toHaveBeenCalledTimes(2);
    expect(results.map((result) => result.type).sort()).toEqual([
      "confirmed",
      "pendingSync",
    ]);
  });

  it("keeps a shared retry authoritative when one caller aborts", async () => {
    const fixture = createStorage();
    fixture.current = createCommand();
    const sendResult = createDeferred<SignupResponse>();
    const send = jest
      .fn<(command: PendingSignupCommand, signal?: AbortSignal) => Promise<SignupResponse>>()
      .mockReturnValue(sendResult.promise);
    const firstCoordinator = createCoordinator(fixture.storage, send);
    const secondCoordinator = createCoordinator(fixture.storage, send);
    const firstCaller = new AbortController();

    const firstRetry = firstCoordinator.retry(
      input.organizationId,
      input.primaryMembershipId,
      firstCaller.signal,
    );
    await Promise.resolve();
    await Promise.resolve();
    const secondRetry = secondCoordinator.retry(
      input.organizationId,
      input.primaryMembershipId,
    );
    const outcomesPromise = Promise.allSettled([firstRetry, secondRetry]);
    await Promise.resolve();
    await Promise.resolve();

    expect(send).toHaveBeenCalledTimes(1);

    firstCaller.abort();
    sendResult.resolve(signup);
    const outcomes = await outcomesPromise;

    expect(outcomes[0]).toMatchObject({
      reason: expect.objectContaining({ name: "AbortError" }),
      status: "rejected",
    });
    expect(outcomes[1]).toEqual({
      status: "fulfilled",
      value: { signup, type: "confirmed" },
    });
    expect(send).toHaveBeenCalledTimes(1);
    expect(fixture.storage.clearIfCurrent).toHaveBeenCalledTimes(1);
    expect(fixture.current).toBeNull();
  });

  it("does not persist or send when logout aborts a delayed admission load", async () => {
    const fixture = createStorage();
    const delayedAdmission = createDeferred<PendingSignupAdmission>();
    fixture.storage.admit.mockReturnValueOnce(delayedAdmission.promise);
    const send = jest.fn(async () => signup);
    const coordinator = createCoordinator(fixture.storage, send);
    const controller = new AbortController();

    const submission = coordinator.submit(input, controller.signal);
    await Promise.resolve();
    controller.abort(new Error("session cleared"));
    delayedAdmission.resolve({
      slot: { command: createCommand(), epoch: fixture.epoch },
      type: "admitted",
    });

    await expect(submission).rejects.toMatchObject({ name: "AbortError" });
    expect(fixture.storage.replaceIfCurrent).not.toHaveBeenCalled();
    expect(send).not.toHaveBeenCalled();
    expect(fixture.current).toBeNull();
  });

  it("persists before send, clears on 201, and blocks a second command", async () => {
    const fixture = createStorage();
    const send = jest.fn(async (command: PendingSignupCommand) => {
      expect(fixture.current).toEqual(command);
      return signup;
    });
    const coordinator = createCoordinator(fixture.storage, send);

    await expect(coordinator.submit(input)).resolves.toEqual({
      signup,
      type: "confirmed",
    });
    expect(fixture.storage.admit).toHaveBeenCalledTimes(1);
    expect(fixture.storage.clearIfCurrent).toHaveBeenCalledTimes(1);

    fixture.current = createCommand();
    await expect(coordinator.submit(input)).resolves.toMatchObject({
      type: "pendingSync",
    });
    expect(send).toHaveBeenCalledTimes(1);
  });

  it("does not send when durable persistence fails", async () => {
    const fixture = createStorage();
    fixture.storage.admit.mockRejectedValueOnce(new Error("secure store failed"));
    const send = jest.fn(async () => signup);
    const coordinator = createCoordinator(fixture.storage, send);

    await expect(coordinator.submit(input)).rejects.toThrow(
      "secure store failed",
    );
    expect(send).not.toHaveBeenCalled();
  });

  it("retains and replays the exact body and idempotency key after an unknown outcome", async () => {
    const fixture = createStorage();
    const sent: PendingSignupCommand[] = [];
    const send = jest
      .fn(async (command: PendingSignupCommand) => {
        sent.push(command);
        if (sent.length === 1) {
          throw new TypeError("network unavailable");
        }
        return signup;
      });
    const coordinator = createCoordinator(fixture.storage, send);

    const first = await coordinator.submit(input);
    expect(first.type).toBe("pendingSync");
    await coordinator.retry(
      input.organizationId,
      input.primaryMembershipId,
      undefined,
      true,
    );

    expect(sent).toHaveLength(2);
    expect(sent[1]?.idempotencyKey).toBe(sent[0]?.idempotencyKey);
    expect(sent[1]?.body).toEqual(sent[0]?.body);
  });

  it.each([
    [412, "stale_version"],
    [429, "rate_limited"],
    [503, "dependency_unavailable"],
    [409, "idempotency_in_progress"],
  ])("retains transient %s %s outcomes", async (status, code) => {
    const fixture = createStorage();
    const send = jest.fn(async () => {
      throw problemError(status, code, status === 429 ? 17 : undefined);
    });

    const coordinator = createCoordinator(fixture.storage, send);

    const result = await coordinator.submit(input);

    expect(result).toMatchObject({ type: "pendingSync" });
    expect(fixture.current?.nextAttemptAt).toBeDefined();
    if (status === 429) {
      expect(fixture.current?.nextAttemptAt).toBe(
        "2026-08-17T10:00:17.000Z",
      );
    }
  });

  it("retains Pending sync when the 10-second request scope times out", async () => {
    const fixture = createStorage();
    const controller = new AbortController();
    const timeout = new Error("The request timed out.");
    timeout.name = "TimeoutError";
    const coordinator = createCoordinator(fixture.storage, async () => {
      controller.abort(timeout);
      throw new Error("network request aborted");
    });

    await expect(
      coordinator.submit(input, {
        operationSignal: new AbortController().signal,
        requestSignal: controller.signal,
      }),
    ).resolves.toMatchObject({ type: "pendingSync" });
    expect(fixture.current).not.toBeNull();
  });

  it("does not re-save after the real timeout when logout wins before late rejection", async () => {
    jest.useFakeTimers();
    try {
      const fixture = createStorage();
      const lateSend = createDeferred<SignupResponse>();
      const send = jest.fn(async () => lateSend.promise);
      const coordinator = createCoordinator(fixture.storage, send);
      const operationController = new AbortController();
      const scope = createRequestScope(operationController.signal);

      const submission = coordinator.submit(input, {
        operationSignal: operationController.signal,
        requestSignal: scope.signal,
      });
      await Promise.resolve();
      await Promise.resolve();
      expect(send).toHaveBeenCalledTimes(1);

      await jest.advanceTimersByTimeAsync(10_000);
      operationController.abort(new Error("session cleared"));
      await fixture.storage.purge();
      lateSend.reject(new Error("late network rejection"));

      await expect(submission).rejects.toMatchObject({ name: "AbortError" });
      expect(fixture.current).toBeNull();
      expect(fixture.storage.replaceIfCurrent).not.toHaveBeenCalled();
      scope.dispose();
    } finally {
      jest.useRealTimers();
    }
  });

  it("reconciles duplicates without manufacturing new-submission success", async () => {
    const fixture = createStorage();
    const coordinator = new SignupSyncCoordinator({
      createIdempotencyKey: () => "11111111-1111-4111-8111-111111111111",
      now: () => Date.parse("2026-08-17T10:00:00.000Z"),
      reconcile: async () => signup,
      send: async () => {
        throw problemError(409, "signup_duplicate");
      },
      storage: fixture.storage,
    });

    await expect(coordinator.submit(input)).resolves.toEqual({
      signup,
      type: "duplicate",
    });
    expect(fixture.storage.clearIfCurrent).toHaveBeenCalled();
  });

  it("clears category-closed and other permanent 4xx only after reconciliation loads", async () => {
    const fixture = createStorage();
    const reconcile = jest.fn(async () => null);
    const coordinator = new SignupSyncCoordinator({
      createIdempotencyKey: () => "11111111-1111-4111-8111-111111111111",
      now: () => Date.parse("2026-08-17T10:00:00.000Z"),
      reconcile,
      send: async () => {
        throw problemError(409, "category_closed");
      },
      storage: fixture.storage,
    });

    await expect(coordinator.submit(input)).resolves.toMatchObject({
      type: "permanentError",
    });
    expect(reconcile).toHaveBeenCalled();
    expect(fixture.storage.clearIfCurrent).toHaveBeenCalled();
  });

  it("lets 401 escape for the authenticated runner to sign out", async () => {
    const fixture = createStorage();
    const unauthorized = problemError(401, "unauthorized");
    const coordinator = createCoordinator(fixture.storage, async () => {
      throw unauthorized;
    });

    await expect(coordinator.submit(input)).rejects.toBe(unauthorized);
  });

  it("stops replay at 23 hours and requires a new explicit command", async () => {
    const fixture = createStorage();
    fixture.current = {
      ...createCommand(),
      createdAt: "2026-08-16T11:00:00.000Z",
    };
    const send = jest.fn(async () => signup);
    const reconcile = jest.fn(async () => null);
    const coordinator = new SignupSyncCoordinator({
      now: () => Date.parse("2026-08-17T10:00:00.000Z"),
      reconcile,
      send,
      storage: fixture.storage,
    });

    await expect(
      coordinator.retry(input.organizationId, input.primaryMembershipId),
    ).resolves.toMatchObject({ type: "expired" });
    expect(send).not.toHaveBeenCalled();
    expect(reconcile).toHaveBeenCalled();
    expect(fixture.storage.clearIfCurrent).toHaveBeenCalled();
  });

  it("expires at exactly 23 hours before a future next attempt", async () => {
    const fixture = createStorage();
    fixture.current = {
      ...createCommand(),
      createdAt: "2026-08-16T11:00:00.000Z",
      nextAttemptAt: "2026-08-17T10:05:00.000Z",
    };
    const send = jest.fn(async () => signup);
    const reconcile = jest.fn(async () => null);
    const coordinator = new SignupSyncCoordinator({
      now: () => Date.parse("2026-08-17T10:00:00.000Z"),
      reconcile,
      send,
      storage: fixture.storage,
    });

    await expect(
      coordinator.retry(input.organizationId, input.primaryMembershipId),
    ).resolves.toEqual({
      message:
        "The pending signup is too old to replay. Review the current date and submit again.",
      type: "expired",
    });
    expect(reconcile).toHaveBeenCalledTimes(1);
    expect(send).not.toHaveBeenCalled();
    expect(fixture.storage.clearIfCurrent).toHaveBeenCalledTimes(1);
    expect(fixture.current).toBeNull();
  });

  it("expires at 23 hours after a long Retry-After without sending again", async () => {
    const fixture = createStorage();
    let now = Date.parse("2026-08-17T09:59:00.000Z");
    fixture.current = {
      ...createCommand(),
      createdAt: "2026-08-16T11:00:00.000Z",
    };
    const send = jest.fn(async () => {
      throw problemError(429, "rate_limited", 24 * 60 * 60);
    });
    const reconcile = jest.fn(async () => null);
    const coordinator = new SignupSyncCoordinator({
      now: () => now,
      reconcile,
      send,
      storage: fixture.storage,
    });

    await expect(
      coordinator.retry(
        input.organizationId,
        input.primaryMembershipId,
        undefined,
        true,
      ),
    ).resolves.toMatchObject({ type: "pendingSync" });
    expect(fixture.current?.nextAttemptAt).toBe(
      "2026-08-18T09:59:00.000Z",
    );

    now = Date.parse("2026-08-17T10:00:00.000Z");
    send.mockClear();

    await expect(
      coordinator.retry(input.organizationId, input.primaryMembershipId),
    ).resolves.toMatchObject({ type: "expired" });
    expect(send).not.toHaveBeenCalled();
    expect(reconcile).toHaveBeenCalledTimes(2);
    expect(fixture.storage.clearIfCurrent).toHaveBeenCalledTimes(1);
    expect(fixture.current).toBeNull();
  });

  it("does not restore a purged command after stale completion", async () => {
    const fixture = createStorage();
    let resolveSend!: (value: SignupResponse) => void;
    const sendPromise = new Promise<SignupResponse>((resolve) => {
      resolveSend = resolve;
    });
    const coordinator = createCoordinator(
      fixture.storage,
      async () => sendPromise,
    );
    const controller = new AbortController();

    const operation = coordinator.submit(input, controller.signal);
    await Promise.resolve();
    controller.abort();
    fixture.current = null;
    resolveSend(signup);

    await expect(operation).rejects.toMatchObject({ name: "AbortError" });
    expect(fixture.current).toBeNull();
    expect(fixture.storage.admit).toHaveBeenCalledTimes(1);
    expect(fixture.storage.replaceIfCurrent).not.toHaveBeenCalled();
  });
});

function createCoordinator(
  storage: jest.Mocked<PendingSignupStorage>,
  send: (command: PendingSignupCommand) => Promise<SignupResponse>,
): SignupSyncCoordinator {
  return new SignupSyncCoordinator({
    createIdempotencyKey: () => "11111111-1111-4111-8111-111111111111",
    now: () => Date.parse("2026-08-17T10:00:00.000Z"),
    reconcile: async () => null,
    send,
    storage,
  });
}

function createStorage(): {
  current: PendingSignupCommand | null;
  epoch: number;
  storage: jest.Mocked<PendingSignupStorage>;
} {
  const fixture: {
    current: PendingSignupCommand | null;
    epoch: number;
    storage: jest.Mocked<PendingSignupStorage>;
  } = {
    current: null,
    epoch: 0,
    storage: undefined as unknown as jest.Mocked<PendingSignupStorage>,
  };

  fixture.storage = {
    admit: jest.fn(async (command, signal) => {
      throwIfAborted(signal);
      if (fixture.current) {
        return {
          slot: { command: fixture.current, epoch: fixture.epoch },
          type: "existing",
        };
      }
      fixture.current = command;
      return {
        slot: { command, epoch: fixture.epoch },
        type: "admitted",
      };
    }),
    clearIfCurrent: jest.fn(async (slot, signal) => {
      throwIfAborted(signal);
      if (!isCurrentSlot(fixture, slot)) {
        return false;
      }
      fixture.current = null;
      return true;
    }),
    load: jest.fn(async (signal) => {
      throwIfAborted(signal);
      return fixture.current
        ? { command: fixture.current, epoch: fixture.epoch }
        : null;
    }),
    loadForIdentity: jest.fn(async (organizationId, membershipId, signal) => {
      throwIfAborted(signal);
      if (
        fixture.current?.organizationId === organizationId &&
        fixture.current.primaryMembershipId === membershipId
      ) {
        return { command: fixture.current, epoch: fixture.epoch };
      }
      return null;
    }),
    purge: jest.fn(async () => {
      fixture.epoch += 1;
      fixture.current = null;
    }),
    replaceIfCurrent: jest.fn(async (slot, command, signal) => {
      throwIfAborted(signal);
      if (!isCurrentSlot(fixture, slot)) {
        return null;
      }
      fixture.current = command;
      return { command, epoch: fixture.epoch };
    }),
  };

  return fixture;
}

function isCurrentSlot(
  fixture: {
    readonly current: PendingSignupCommand | null;
    readonly epoch: number;
  },
  slot: PendingSignupSlot,
): boolean {
  return (
    fixture.epoch === slot.epoch &&
    fixture.current?.idempotencyKey === slot.command.idempotencyKey
  );
}

function throwIfAborted(signal?: AbortSignal): void {
  if (!signal?.aborted) {
    return;
  }

  const error = new Error("superseded");
  error.name = "AbortError";
  throw error;
}

function createCommand(): PendingSignupCommand {
  return {
    ...input,
    createdAt: "2026-08-17T10:00:00.000Z",
    idempotencyKey: "11111111-1111-4111-8111-111111111111",
    version: 1,
  };
}

function problemError(
  status: number,
  code: string,
  retryAfterSeconds?: number,
): ApiClientError {
  const problem: ProblemDetails = {
    code,
    detail: code,
    status,
    title: "Problem",
    traceId: "trace-id",
    type: `https://httpstatuses.com/${status}`,
  };

  return new ApiClientError({
    problem,
    retryAfterSeconds,
    status,
  });
}

function createDeferred<T>(): {
  readonly promise: Promise<T>;
  readonly reject: (reason?: unknown) => void;
  readonly resolve: (value: T | PromiseLike<T>) => void;
} {
  let reject!: (reason?: unknown) => void;
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    reject = rejectPromise;
    resolve = resolvePromise;
  });

  return { promise, reject, resolve };
}
