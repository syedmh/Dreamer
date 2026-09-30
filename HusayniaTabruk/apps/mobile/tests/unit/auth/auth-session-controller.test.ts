import { afterEach, beforeEach, describe, expect, it, jest } from "@jest/globals";

import { ApiClientError, type AuthApi, type ProblemDetails } from "../../../src/core/api/auth-api";
import { createRequestScope } from "../../../src/core/api/operation-runtime";
import {
  DefaultAuthSessionCoordinator,
  type AuthSessionCoordinator,
} from "../../../src/core/security/auth-session-controller";
import type { AuthStorage } from "../../../src/core/security/secure-auth-storage";
import type { AuthCredentials, AuthSession } from "../../../src/core/security/auth-session-types";

type AuthApiMock = {
  [Key in keyof AuthApi]: jest.MockedFunction<AuthApi[Key]>;
};

type AuthStorageMock = {
  [Key in keyof AuthStorage]: jest.MockedFunction<AuthStorage[Key]>;
};

const sampleSession: AuthSession = {
  accessToken: "access-token-value",
  accessTokenExpiresAt: "2026-08-17T12:00:00.000Z",
  actor: {
    membership: {
      displayName: "Test Member",
      eligibleAsNamedParticipant: true,
      id: "membership-1",
    },
    organization: {
      id: "organization-1",
      name: "Husaynia",
      timeZone: "America/Los_Angeles",
    },
    roles: ["Member"],
  },
  installationId: "94da1fc8-f16f-48d5-9df5-f2148e4f9fa8",
  refreshToken: "refresh-token-value",
  refreshTokenExpiresAt: "2026-09-16T12:00:00.000Z",
};

const rotatedSession: AuthSession = {
  ...sampleSession,
  accessToken: "rotated-access-token",
  accessTokenExpiresAt: "2026-08-17T12:10:00.000Z",
  refreshToken: "rotated-refresh-token",
  refreshTokenExpiresAt: "2026-09-17T12:10:00.000Z",
};

const secondRotatedSession: AuthSession = {
  ...rotatedSession,
  accessToken: "second-rotated-access-token",
  accessTokenExpiresAt: "2026-08-17T12:30:00.000Z",
  refreshToken: "second-rotated-refresh-token",
  refreshTokenExpiresAt: "2026-09-17T12:30:00.000Z",
};

describe("DefaultAuthSessionCoordinator", () => {
  let api: AuthApiMock;
  let storage: AuthStorageMock;
  let controller: AuthSessionCoordinator;
  let consoleErrorSpy: jest.SpiedFunction<typeof console.error>;
  let consoleLogSpy: jest.SpiedFunction<typeof console.log>;
  let consoleWarnSpy: jest.SpiedFunction<typeof console.warn>;

  beforeEach(() => {
    api = {
      getCurrentActor: jest.fn<AuthApi["getCurrentActor"]>(),
      login: jest.fn<AuthApi["login"]>(),
      logoutSession: jest.fn<AuthApi["logoutSession"]>(),
      refreshSession: jest.fn<AuthApi["refreshSession"]>(),
    };
    storage = {
      clearSession: jest.fn<AuthStorage["clearSession"]>().mockResolvedValue(undefined),
      loadInstallationId: jest
        .fn<AuthStorage["loadInstallationId"]>()
        .mockResolvedValue(sampleSession.installationId),
      loadSession: jest.fn<AuthStorage["loadSession"]>().mockResolvedValue(null),
      saveInstallationId: jest
        .fn<AuthStorage["saveInstallationId"]>()
        .mockResolvedValue(undefined),
      saveSession: jest.fn<AuthStorage["saveSession"]>().mockResolvedValue(undefined),
    };

    controller = new DefaultAuthSessionCoordinator({
      api,
      createInstallationId: () => sampleSession.installationId,
      now: () => Date.parse("2026-08-17T11:00:00.000Z"),
      storage,
    });

    consoleErrorSpy = jest.spyOn(console, "error").mockImplementation(() => undefined);
    consoleLogSpy = jest.spyOn(console, "log").mockImplementation(() => undefined);
    consoleWarnSpy = jest.spyOn(console, "warn").mockImplementation(() => undefined);
  });

  afterEach(() => {
    consoleErrorSpy.mockRestore();
    consoleLogSpy.mockRestore();
    consoleWarnSpy.mockRestore();
  });

  it("signs in, persists the session, and keeps tokens out of logs", async () => {
    const credentials: AuthCredentials = {
      email: "member@example.test",
      password: "Passw0rd!Passw0rd!",
    };

    api.login.mockResolvedValueOnce({
      accessToken: sampleSession.accessToken,
      accessTokenExpiresAt: sampleSession.accessTokenExpiresAt,
      refreshToken: sampleSession.refreshToken,
      refreshTokenExpiresAt: sampleSession.refreshTokenExpiresAt,
    });
    api.getCurrentActor.mockResolvedValueOnce(sampleSession.actor);

    const result = await controller.signIn(credentials);

    expect(result).toEqual(sampleSession);
    expect(api.login).toHaveBeenCalledWith(
      credentials,
      sampleSession.installationId,
      undefined,
    );
    expect(storage.saveSession).toHaveBeenCalledWith(sampleSession);
    expect(consoleLogSpy).not.toHaveBeenCalled();
    expect(consoleWarnSpy).not.toHaveBeenCalled();
    expect(consoleErrorSpy).not.toHaveBeenCalled();
  });

  it("rotates the refresh token when restore finds an expired access token", async () => {
    storage.loadSession.mockResolvedValueOnce({
      ...sampleSession,
      accessTokenExpiresAt: "2026-08-17T10:59:59.000Z",
    });
    api.refreshSession.mockResolvedValueOnce({
      accessToken: rotatedSession.accessToken,
      accessTokenExpiresAt: rotatedSession.accessTokenExpiresAt,
      refreshToken: rotatedSession.refreshToken,
      refreshTokenExpiresAt: rotatedSession.refreshTokenExpiresAt,
    });
    api.getCurrentActor.mockResolvedValueOnce(rotatedSession.actor);

    const result = await controller.restore();

    expect(result).toEqual(rotatedSession);
    expect(api.refreshSession).toHaveBeenCalledWith(
      sampleSession.refreshToken,
      sampleSession.installationId,
      undefined,
    );
    expect(storage.saveSession).toHaveBeenCalledWith(rotatedSession);
  });

  it("preserves rotated tokens when /me fails after a successful refresh", async () => {
    storage.loadSession.mockResolvedValueOnce({
      ...sampleSession,
      accessTokenExpiresAt: "2026-08-17T10:59:59.000Z",
    });
    api.refreshSession.mockResolvedValueOnce({
      accessToken: rotatedSession.accessToken,
      accessTokenExpiresAt: rotatedSession.accessTokenExpiresAt,
      refreshToken: rotatedSession.refreshToken,
      refreshTokenExpiresAt: rotatedSession.refreshTokenExpiresAt,
    });
    api.getCurrentActor.mockRejectedValueOnce(
      createProblemError(
        503,
        "dependency_unavailable",
        "A required dependency is temporarily unavailable.",
      ),
    );

    const result = await controller.restore();

    expect(result).toEqual(rotatedSession);
    expect(storage.saveSession).toHaveBeenCalledWith(rotatedSession);
    expect(storage.clearSession).not.toHaveBeenCalled();
  });

  it("returns persisted rotated tokens when refreshed /me ignores timeout abort", async () => {
    const expiredSession = {
      ...sampleSession,
      accessTokenExpiresAt: "2026-08-17T10:59:59.000Z",
    };
    const actorResult = deferred<AuthSession["actor"]>();
    const timeoutController = new AbortController();
    storage.loadSession.mockResolvedValueOnce(expiredSession);
    api.refreshSession.mockResolvedValueOnce({
      accessToken: rotatedSession.accessToken,
      accessTokenExpiresAt: rotatedSession.accessTokenExpiresAt,
      refreshToken: rotatedSession.refreshToken,
      refreshTokenExpiresAt: rotatedSession.refreshTokenExpiresAt,
    });
    api.getCurrentActor.mockReturnValueOnce(actorResult.promise);

    const restoration = controller.restore(timeoutController.signal);
    await waitForCall(storage.saveSession);
    await waitForCall(api.getCurrentActor);

    const timeoutError = new Error("The request timed out.");
    timeoutError.name = "TimeoutError";
    timeoutController.abort(timeoutError);

    await expect(restoration).resolves.toEqual(rotatedSession);
    actorResult.resolve(rotatedSession.actor);
    await Promise.resolve();
    expect(storage.saveSession).toHaveBeenCalledWith(rotatedSession);
    expect(storage.clearSession).not.toHaveBeenCalled();
  });

  it("does not reuse a completed synchronization", async () => {
    const unauthorized = createProblemError(
      401,
      "unauthorized",
      "Authentication required.",
    );
    api.getCurrentActor
      .mockRejectedValueOnce(unauthorized)
      .mockResolvedValueOnce(rotatedSession.actor)
      .mockRejectedValueOnce(unauthorized)
      .mockResolvedValueOnce(secondRotatedSession.actor);
    api.refreshSession
      .mockResolvedValueOnce({
        accessToken: rotatedSession.accessToken,
        accessTokenExpiresAt: rotatedSession.accessTokenExpiresAt,
        refreshToken: rotatedSession.refreshToken,
        refreshTokenExpiresAt: rotatedSession.refreshTokenExpiresAt,
      })
      .mockResolvedValueOnce({
        accessToken: secondRotatedSession.accessToken,
        accessTokenExpiresAt: secondRotatedSession.accessTokenExpiresAt,
        refreshToken: secondRotatedSession.refreshToken,
        refreshTokenExpiresAt: secondRotatedSession.refreshTokenExpiresAt,
      });

    await expect(controller.synchronize(sampleSession)).resolves.toEqual(
      rotatedSession,
    );
    await expect(controller.synchronize(sampleSession)).resolves.toEqual(
      secondRotatedSession,
    );

    expect(api.refreshSession).toHaveBeenCalledTimes(2);
  });

  it("refreshes expired rotated access with the current rotated refresh token", async () => {
    let now = Date.parse("2026-08-17T11:00:00.000Z");
    controller = new DefaultAuthSessionCoordinator({
      api,
      now: () => now,
      storage,
    });
    const unauthorized = createProblemError(
      401,
      "unauthorized",
      "Authentication required.",
    );
    api.getCurrentActor
      .mockRejectedValueOnce(unauthorized)
      .mockResolvedValueOnce(rotatedSession.actor)
      .mockResolvedValueOnce(secondRotatedSession.actor);
    api.refreshSession
      .mockResolvedValueOnce({
        accessToken: rotatedSession.accessToken,
        accessTokenExpiresAt: rotatedSession.accessTokenExpiresAt,
        refreshToken: rotatedSession.refreshToken,
        refreshTokenExpiresAt: rotatedSession.refreshTokenExpiresAt,
      })
      .mockResolvedValueOnce({
        accessToken: secondRotatedSession.accessToken,
        accessTokenExpiresAt: secondRotatedSession.accessTokenExpiresAt,
        refreshToken: secondRotatedSession.refreshToken,
        refreshTokenExpiresAt: secondRotatedSession.refreshTokenExpiresAt,
      });

    const firstSynchronization = await controller.synchronize(sampleSession);
    api.getCurrentActor.mockClear();
    now = Date.parse("2026-08-17T12:11:00.000Z");
    const secondSynchronization =
      await controller.synchronize(firstSynchronization);

    expect(secondSynchronization).toEqual(secondRotatedSession);
    expect(api.refreshSession).toHaveBeenNthCalledWith(
      2,
      rotatedSession.refreshToken,
      rotatedSession.installationId,
      undefined,
    );
    expect(api.getCurrentActor).not.toHaveBeenCalledWith(
      rotatedSession.accessToken,
      undefined,
    );
  });

  it("does not persist rotation after the synchronization signal aborts", async () => {
    const unauthorized = createProblemError(
      401,
      "unauthorized",
      "Authentication required.",
    );
    const refreshResult =
      deferred<Awaited<ReturnType<AuthApi["refreshSession"]>>>();
    const sessionController = new AbortController();
    api.getCurrentActor.mockRejectedValueOnce(unauthorized);
    api.refreshSession.mockReturnValueOnce(refreshResult.promise);

    const synchronization = controller.synchronize(
      sampleSession,
      sessionController.signal,
    );
    await waitForCall(api.refreshSession);
    sessionController.abort();
    refreshResult.resolve({
      accessToken: rotatedSession.accessToken,
      accessTokenExpiresAt: rotatedSession.accessTokenExpiresAt,
      refreshToken: rotatedSession.refreshToken,
      refreshTokenExpiresAt: rotatedSession.refreshTokenExpiresAt,
    });

    await expect(synchronization).rejects.toMatchObject({
      name: "AbortError",
    });
    await Promise.resolve();
    await Promise.resolve();
    expect(storage.saveSession).not.toHaveBeenCalled();
  });

  it("purges secure session state when a refresh attempt fails during synchronization", async () => {
    const meUnauthorized = createProblemError(
      401,
      "unauthorized",
      "Authentication required.",
    );
    const refreshReused = createProblemError(
      401,
      "refresh_token_reused",
      "The refresh token was already used and the session has been revoked.",
    );

    api.getCurrentActor.mockRejectedValueOnce(meUnauthorized);
    api.refreshSession.mockRejectedValueOnce(refreshReused);

    await expect(controller.synchronize(sampleSession)).rejects.toBe(refreshReused);

    expect(storage.clearSession).toHaveBeenCalledTimes(1);
  });

  it("purges the stored session even when the logout endpoint fails", async () => {
    api.logoutSession.mockRejectedValueOnce(
      createProblemError(
        503,
        "dependency_unavailable",
        "A required dependency is temporarily unavailable.",
      ),
    );

    await controller.signOut(sampleSession);

    expect(api.logoutSession).toHaveBeenCalledWith(
      sampleSession.refreshToken,
      sampleSession.installationId,
      expect.any(Object),
    );
    expect(storage.clearSession).toHaveBeenCalledTimes(1);
    expect(storage.clearSession).toHaveBeenCalledTimes(1);
  });

  it("attempts remote revocation when local session deletion fails", async () => {
    const deletionError = new Error("secure session deletion failed");
    storage.clearSession.mockRejectedValueOnce(deletionError);
    api.logoutSession.mockResolvedValueOnce(undefined);

    await expect(controller.signOut(sampleSession)).rejects.toBe(
      deletionError,
    );

    expect(api.logoutSession).toHaveBeenCalledWith(
      sampleSession.refreshToken,
      sampleSession.installationId,
      expect.any(Object),
    );
  });

  it("attempts remote revocation independently while local deletion times out", async () => {
    jest.useFakeTimers();
    const deletion = deferred<void>();
    const callerScope = createRequestScope();
    storage.clearSession.mockReturnValueOnce(deletion.promise);
    api.logoutSession.mockResolvedValueOnce(undefined);
    let signOutOperation: Promise<void> | undefined;

    try {
      signOutOperation = controller.signOut(
        sampleSession,
        callerScope.signal,
      );
      await Promise.resolve();
      await Promise.resolve();

      expect(storage.clearSession).toHaveBeenCalledTimes(1);
      expect(api.logoutSession).toHaveBeenCalledWith(
        sampleSession.refreshToken,
        sampleSession.installationId,
        expect.any(Object),
      );

      const rejection = expect(signOutOperation).rejects.toMatchObject({
        name: "TimeoutError",
      });
      await jest.advanceTimersByTimeAsync(10_000);

      await rejection;
    } finally {
      deletion.resolve(undefined);
      if (signOutOperation) {
        await Promise.allSettled([signOutOperation]);
      }
      callerScope.dispose();
      jest.useRealTimers();
    }
  });

  it("does not repersist a synchronized session after logout supersedes it", async () => {
    const actorResult = deferred<AuthSession["actor"]>();
    api.getCurrentActor.mockReturnValueOnce(actorResult.promise);

    const synchronization = controller.synchronize(sampleSession);
    await controller.signOut(null);
    actorResult.resolve({
      ...sampleSession.actor,
      membership: {
        ...sampleSession.actor.membership,
        displayName: "Stale Member",
      },
    });

    await expect(synchronization).rejects.toMatchObject({
      name: "AbortError",
    });
    expect(storage.saveSession).not.toHaveBeenCalled();
    expect(storage.clearSession).toHaveBeenCalledTimes(1);
  });

  it("does not let stale synchronization overwrite a newer sign in", async () => {
    const staleActorResult = deferred<AuthSession["actor"]>();
    const freshSession: AuthSession = {
      ...sampleSession,
      accessToken: "fresh-access-token",
      refreshToken: "fresh-refresh-token",
      actor: {
        ...sampleSession.actor,
        membership: {
          ...sampleSession.actor.membership,
          displayName: "Fresh Member",
        },
      },
    };
    api.getCurrentActor
      .mockReturnValueOnce(staleActorResult.promise)
      .mockResolvedValueOnce(freshSession.actor);
    api.login.mockResolvedValueOnce({
      accessToken: freshSession.accessToken,
      accessTokenExpiresAt: freshSession.accessTokenExpiresAt,
      refreshToken: freshSession.refreshToken,
      refreshTokenExpiresAt: freshSession.refreshTokenExpiresAt,
    });

    const synchronization = controller.synchronize(sampleSession);
    await expect(
      controller.signIn({
        email: "fresh@example.test",
        password: "Passw0rd!Passw0rd!",
      }),
    ).resolves.toEqual(freshSession);
    staleActorResult.resolve({
      ...sampleSession.actor,
      membership: {
        ...sampleSession.actor.membership,
        displayName: "Stale Member",
      },
    });

    await expect(synchronization).rejects.toMatchObject({
      name: "AbortError",
    });
    expect(storage.saveSession).toHaveBeenCalledTimes(1);
    expect(storage.saveSession).toHaveBeenCalledWith(freshSession);
  });

  it("does not persist rotated tokens after an aborted refresh", async () => {
    const refreshResult = deferred<Awaited<ReturnType<AuthApi["refreshSession"]>>>();
    api.getCurrentActor.mockRejectedValueOnce(
      createProblemError(401, "unauthorized", "Authentication required."),
    );
    api.refreshSession.mockReturnValueOnce(refreshResult.promise);

    const synchronization = controller.synchronize(sampleSession);
    await controller.signOut(null);
    refreshResult.resolve({
      accessToken: rotatedSession.accessToken,
      accessTokenExpiresAt: rotatedSession.accessTokenExpiresAt,
      refreshToken: rotatedSession.refreshToken,
      refreshTokenExpiresAt: rotatedSession.refreshTokenExpiresAt,
    });

    await expect(synchronization).rejects.toMatchObject({
      name: "AbortError",
    });
    expect(storage.saveSession).not.toHaveBeenCalled();
    expect(storage.clearSession).toHaveBeenCalledTimes(1);
  });
});

function deferred<T>(): {
  promise: Promise<T>;
  reject: (reason?: unknown) => void;
  resolve: (value: T) => void;
} {
  let reject!: (reason?: unknown) => void;
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((promiseResolve, promiseReject) => {
    resolve = promiseResolve;
    reject = promiseReject;
  });

  return {
    promise,
    reject,
    resolve,
  };
}

async function waitForCall(mock: {
  readonly mock: { readonly calls: readonly unknown[][] };
}): Promise<void> {
  for (let attempt = 0; attempt < 20 && mock.mock.calls.length === 0; attempt += 1) {
    await Promise.resolve();
  }

  expect(mock.mock.calls.length).toBeGreaterThan(0);
}

function createProblemError(
  status: number,
  code: string,
  detail: string,
): ApiClientError {
  const problem: ProblemDetails = {
    code,
    detail,
    status,
    title: "Problem",
    traceId: "trace-id",
    type: `https://httpstatuses.com/${status}`,
  };

  return new ApiClientError({
    problem,
    status,
  });
}
