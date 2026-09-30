import { beforeEach, describe, expect, it, jest } from "@jest/globals";
import type { ReactNode } from "react";
import { TextInput } from "react-native";
import { act, create, type ReactTestRenderer } from "react-test-renderer";

import AuthLayout from "../../../app/(auth)/_layout";
import MemberLayout from "../../../app/(member)/_layout";
import {
  ApiClientError,
  type AuthApi,
} from "../../../src/core/api/auth-api";
import { subscribeFeatureRefresh } from "../../../src/core/api/feature-refresh-bus";
import {
  DefaultAuthSessionCoordinator,
  type AuthSessionCoordinator,
} from "../../../src/core/security/auth-session-controller";
import type { PendingSignupStorage } from "../../../src/core/security/pending-signup-storage";
import type { AuthStorage } from "../../../src/core/security/secure-auth-storage";
import type { AuthSession } from "../../../src/core/security/auth-session-types";
import {
  AuthSessionProvider,
  type AuthContextValue,
} from "../../../src/features/auth/AuthSessionProvider";
import { LoginScreen } from "../../../src/features/auth/LoginScreen";
import { MemberShellScreen } from "../../../src/features/auth/MemberShellScreen";
import { useAuth } from "../../../src/features/auth/useAuth";

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");

  const Stack = Object.assign(
    (props: Record<string, unknown>) =>
      React.createElement("Stack", props, props.children as ReactNode),
    {
      Screen: (props: Record<string, unknown>) =>
        React.createElement("StackScreen", props, props.children as ReactNode),
    },
  );

  return {
    Link: (props: Record<string, unknown>) =>
      React.createElement("Link", props, props.children as ReactNode),
    Redirect: (props: Record<string, unknown>) => React.createElement("Redirect", props),
    Stack,
  };
});

const sampleSession: AuthSession = {
  accessToken: "access-token-value",
  accessTokenExpiresAt: "2026-08-17T12:00:00.000Z",
  actor: {
    membership: {
      displayName: "Restored Member",
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
  installationId: "ee0c3f79-6738-4417-9d68-a3b08b708fba",
  refreshToken: "refresh-token-value",
  refreshTokenExpiresAt: "2026-09-16T12:00:00.000Z",
};

const freshSession: AuthSession = {
  ...sampleSession,
  accessToken: "fresh-access-token",
  accessTokenExpiresAt: "2026-08-17T12:10:00.000Z",
  actor: {
    ...sampleSession.actor,
    membership: {
      ...sampleSession.actor.membership,
      displayName: "Fresh Member",
    },
  },
  installationId: "0b3a7451-2d2d-4bd0-8c4f-7785b592ffb6",
  refreshToken: "fresh-refresh-token",
  refreshTokenExpiresAt: "2026-09-16T12:10:00.000Z",
};

const secondFreshSession: AuthSession = {
  ...freshSession,
  accessToken: "second-fresh-access-token",
  accessTokenExpiresAt: "2026-08-17T12:30:00.000Z",
  refreshToken: "second-fresh-refresh-token",
  refreshTokenExpiresAt: "2026-09-16T12:30:00.000Z",
};

describe("auth routing", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("redirects restored sessions away from the auth group", async () => {
    const controller = createControllerDouble({
      restore: jest
        .fn<AuthSessionCoordinator["restore"]>()
        .mockResolvedValue(sampleSession),
    });

    const renderer = await renderWithProvider(controller, <AuthLayout />);

    expect(renderer.root.findByProps({ href: "/(member)" }).props.href).toBe("/(member)");
  });

  it("redirects unauthenticated state away from the member group", async () => {
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(null),
    });

    const renderer = await renderWithProvider(controller, <MemberLayout />);

    expect(renderer.root.findByProps({ href: "/(auth)" }).props.href).toBe("/(auth)");
  });

  it("exits restoring when SecureStore ignores abort and ignores its late result", async () => {
    jest.useFakeTimers();
    const restoreResult = createDeferred<AuthSession | null>();
    const {
      api,
      authStorage,
      controller,
    } = createRealControllerHarness();
    authStorage.loadSession.mockReturnValueOnce(restoreResult.promise);
    api.login.mockResolvedValueOnce({
      accessToken: freshSession.accessToken,
      accessTokenExpiresAt: freshSession.accessTokenExpiresAt,
      refreshToken: freshSession.refreshToken,
      refreshTokenExpiresAt: freshSession.refreshTokenExpiresAt,
    });
    api.getCurrentActor.mockResolvedValueOnce(freshSession.actor);
    let auth: AuthContextValue | null = null;
    let renderer: ReactTestRenderer | undefined;

    try {
      renderer = await renderWithProvider(
        controller,
        <>
          <AuthLayout />
          <CaptureAuth onCapture={(value) => { auth = value; }} />
        </>,
      );

      expect(auth!.status).toBe("restoring");

      await act(async () => {
        await jest.advanceTimersByTimeAsync(10_000);
      });
      await flush();
      expect(auth!.status).toBe("unauthenticated");
      expect(auth!.status).toBe("unauthenticated");

      await act(async () => {
        await auth!.signIn({
          email: "member@example.test",
          password: "Passw0rd!Passw0rd!",
        });
      });
      await flush();

      expect(auth!.status).toBe("authenticated");
      expect(auth!.session?.actor.membership.displayName).toBe("Fresh Member");

      await act(async () => {
        restoreResult.resolve(sampleSession);
        await Promise.resolve();
      });
      await flush();

      expect(auth!.status).toBe("authenticated");
      expect(auth!.session?.actor.membership.displayName).toBe("Fresh Member");
    } finally {
      restoreResult.resolve(sampleSession);
      if (renderer) {
        await act(async () => {
          renderer?.unmount();
        });
      }
      jest.useRealTimers();
    }
  });

  it("switches the auth shell to the member route after a successful sign in", async () => {
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(null),
      signIn: jest
        .fn<AuthSessionCoordinator["signIn"]>()
        .mockResolvedValue(sampleSession),
    });

    const renderer = await renderWithProvider(
      controller,
      <>
        <AuthLayout />
        <LoginScreen />
      </>,
    );

    await submitCredentials(renderer);

    expect(renderer.root.findByProps({ href: "/(member)" }).props.href).toBe("/(member)");
    expect(controller.signIn).toHaveBeenCalledWith(
      {
        email: "member@example.test",
        password: "Passw0rd!Passw0rd!",
      },
      expect.any(Object),
    );
  });

  it("redirects to /(auth) after tapping sign out from the member shell", async () => {
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(sampleSession),
    });

    const renderer = await renderWithProvider(
      controller,
      <>
        <MemberLayout />
        <MemberShellScreen />
      </>,
    );

    await pressSignOut(renderer);

    expect(renderer.root.findByProps({ href: "/(auth)" }).props.href).toBe("/(auth)");
    expect(controller.signOut).toHaveBeenCalledWith(sampleSession, expect.any(Object));
  });

  it("does not report sign out complete until both secure deletions succeed", async () => {
    const authDeletion = createDeferred<void>();
    const pendingDeletion = createDeferred<void>();
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(sampleSession),
      signOut: jest
        .fn<AuthSessionCoordinator["signOut"]>()
        .mockReturnValue(authDeletion.promise),
    });
    const pendingStorage = createPendingStorageDouble();
    pendingStorage.purge.mockReturnValueOnce(pendingDeletion.promise);
    let auth: AuthContextValue | null = null;
    const renderer = await renderWithProvider(
      controller,
      <CaptureAuth onCapture={(value) => { auth = value; }} />,
      pendingStorage,
    );
    let signOutOperation!: Promise<void>;

    await act(async () => {
      signOutOperation = auth!.signOut();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(auth!.status).toBe("authenticated");
    expect(auth!.isSigningOut).toBe(true);

    await act(async () => {
      authDeletion.resolve(undefined);
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(auth!.status).toBe("authenticated");
    expect(auth!.isSigningOut).toBe(true);

    await act(async () => {
      pendingDeletion.resolve(undefined);
      await signOutOperation;
    });
    await flush();

    expect(auth!.status).toBe("unauthenticated");
    expect(auth!.isSigningOut).toBe(false);

    await act(async () => {
      renderer.unmount();
    });
  });

  it("surfaces pending purge failure, keeps the session, and supports retry", async () => {
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(sampleSession),
    });
    const pendingStorage = createPendingStorageDouble();
    pendingStorage.purge
      .mockRejectedValueOnce(new Error("secure pending deletion failed"))
      .mockResolvedValueOnce(undefined);
    let auth: AuthContextValue | null = null;
    const renderer = await renderWithProvider(
      controller,
      <CaptureAuth onCapture={(value) => { auth = value; }} />,
      pendingStorage,
    );

    await act(async () => {
      await auth!.signOut();
    });
    await flush();

    expect(auth!.status).toBe("authenticated");
    expect(auth!.session).not.toBeNull();
    expect(auth!.error?.detail).toBe("secure pending deletion failed");

    await act(async () => {
      await auth!.signOut();
    });
    await flush();

    expect(auth!.status).toBe("unauthenticated");
    expect(auth!.session).toBeNull();
    expect(controller.signOut).toHaveBeenCalledTimes(2);
    expect(pendingStorage.purge).toHaveBeenCalledTimes(2);

    await act(async () => {
      renderer.unmount();
    });
  });

  it("keeps sign out busy until pending purge settles after local deletion fails", async () => {
    const pendingDeletion = createDeferred<void>();
    const deletionError = new Error("secure auth deletion failed");
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(sampleSession),
      signOut: jest
        .fn<AuthSessionCoordinator["signOut"]>()
        .mockRejectedValue(deletionError),
    });
    const pendingStorage = createPendingStorageDouble();
    pendingStorage.purge.mockReturnValueOnce(pendingDeletion.promise);
    let auth: AuthContextValue | null = null;
    const renderer = await renderWithProvider(
      controller,
      <CaptureAuth onCapture={(value) => { auth = value; }} />,
      pendingStorage,
    );
    let signOutOperation!: Promise<void>;

    await act(async () => {
      signOutOperation = auth!.signOut();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(auth!.status).toBe("authenticated");
    expect(auth!.isSigningOut).toBe(true);

    await act(async () => {
      pendingDeletion.resolve(undefined);
      await signOutOperation;
    });
    await flush();

    expect(auth!.status).toBe("authenticated");
    expect(auth!.isSigningOut).toBe(false);
    expect(auth!.error?.detail).toBe(deletionError.message);

    await act(async () => {
      renderer.unmount();
    });
  });

  it("waits for bounded remote logout before reporting durable sign out", async () => {
    jest.useFakeTimers();
    const remoteLogout = createDeferred<void>();
    const {
      api,
      authStorage,
      controller,
    } = createRealControllerHarness();
    api.logoutSession.mockReturnValueOnce(remoteLogout.promise);
    const pendingStorage = createPendingStorageDouble();
    let auth: AuthContextValue | null = null;
    let renderer: ReactTestRenderer | undefined;
    let signOutOperation: Promise<void> | undefined;

    try {
      renderer = await renderWithProvider(
        controller,
        <>
          <MemberLayout />
          <CaptureAuth onCapture={(value) => { auth = value; }} />
        </>,
        pendingStorage,
      );
      authStorage.clearSession.mockClear();

      await act(async () => {
        signOutOperation = auth!.signOut();
        await Promise.resolve();
        await Promise.resolve();
      });
      await flush();

      expect(auth!.status).toBe("authenticated");
      expect(auth!.isSigningOut).toBe(true);
      expect(
        renderer.root.findAllByProps({ href: "/(auth)" }),
      ).toHaveLength(0);
      expect(pendingStorage.purge).toHaveBeenCalledTimes(1);
      expect(authStorage.clearSession).toHaveBeenCalledTimes(1);

      let didSettle = false;
      void signOutOperation!.then(
        () => {
          didSettle = true;
        },
        () => {
          didSettle = true;
        },
      );

      await act(async () => {
        await jest.advanceTimersByTimeAsync(10_000);
        await signOutOperation;
      });
      await flush();

      expect(didSettle).toBe(true);
      await expect(signOutOperation).resolves.toBeUndefined();
      expect(auth!.status).toBe("unauthenticated");
      expect(renderer.root.findByProps({ href: "/(auth)" }).props.href).toBe("/(auth)");

      await act(async () => {
        remoteLogout.reject(new Error("late remote logout failure"));
        await Promise.resolve();
      });
      await flush();

      expect(auth!.status).toBe("unauthenticated");
      expect(renderer.root.findByProps({ href: "/(auth)" }).props.href).toBe("/(auth)");
    } finally {
      remoteLogout.resolve(undefined);
      if (signOutOperation) {
        await Promise.allSettled([signOutOperation]);
      }
      if (renderer) {
        await act(async () => {
          renderer?.unmount();
        });
      }
      jest.useRealTimers();
    }
  });

  it("aborts feature work before purging the pending command on sign out", async () => {
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(sampleSession),
    });
    const pendingStorage = createPendingStorageDouble();
    let sessionCleared = false;
    const unsubscribe = subscribeFeatureRefresh((event) => {
      if (event === "session-cleared") {
        sessionCleared = true;
      }
    });
    pendingStorage.purge.mockImplementationOnce(async () => {
      expect(sessionCleared).toBe(true);
    });
    const renderer = await renderWithProvider(
      controller,
      <MemberShellScreen />,
      pendingStorage,
    );

    await pressSignOut(renderer);

    expect(pendingStorage.purge).toHaveBeenCalledTimes(1);
    unsubscribe();
  });

  it("ignores stale restore synchronization that resolves after sign out", async () => {
    const synchronizeDeferred = createDeferred<AuthSession>();
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(sampleSession),
      synchronize: jest
        .fn<AuthSessionCoordinator["synchronize"]>()
        .mockReturnValue(synchronizeDeferred.promise),
    });

    const renderer = await renderWithProvider(
      controller,
      <>
        <MemberLayout />
        <MemberShellScreen />
      </>,
    );

    await pressSignOut(renderer);

    expect(renderer.root.findByProps({ href: "/(auth)" }).props.href).toBe("/(auth)");

    await act(async () => {
      synchronizeDeferred.resolve(sampleSession);
      await Promise.resolve();
    });

    await flush();

    expect(renderer.root.findByProps({ href: "/(auth)" }).props.href).toBe("/(auth)");
  });

  it("ignores stale restore synchronization after signing out and back in", async () => {
    const synchronizeDeferred = createDeferred<AuthSession>();
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(sampleSession),
      signIn: jest
        .fn<AuthSessionCoordinator["signIn"]>()
        .mockResolvedValue(freshSession),
      synchronize: jest
        .fn<AuthSessionCoordinator["synchronize"]>()
        .mockReturnValue(synchronizeDeferred.promise),
    });

    const renderer = await renderWithProvider(
      controller,
      <>
        <AuthLayout />
        <MemberLayout />
        <LoginScreen />
        <MemberShellScreen />
      </>,
    );

    await pressSignOut(renderer);
    await submitCredentials(renderer);

    expect(renderer.root.findByProps({ href: "/(member)" }).props.href).toBe("/(member)");
    expect(readText(renderer.root)).toContain("Fresh Member");

    await act(async () => {
      synchronizeDeferred.resolve(sampleSession);
      await Promise.resolve();
    });

    await flush();

    expect(renderer.root.findByProps({ href: "/(member)" }).props.href).toBe("/(member)");
    expect(readText(renderer.root)).toContain("Fresh Member");
    expect(readText(renderer.root)).not.toContain("Restored Member");
  });

  it("refreshes once after a 401, retries with the rotated token, and hides refresh tokens", async () => {
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(sampleSession),
    });
    let auth: AuthContextValue | null = null;
    await renderWithProvider(controller, <CaptureAuth onCapture={(value) => { auth = value; }} />);
    const synchronize = controller.synchronize as jest.MockedFunction<
      AuthSessionCoordinator["synchronize"]
    >;
    synchronize.mockClear();
    synchronize.mockResolvedValueOnce(freshSession);
    const request = jest
      .fn<(accessToken: string) => Promise<string>>()
      .mockRejectedValueOnce(new ApiClientError({ status: 401 }))
      .mockResolvedValueOnce("loaded");

    let loaded: string | undefined;
    await act(async () => {
      loaded = await auth!.runAuthenticatedRequest(request);
    });
    await flush();

    expect(loaded).toBe("loaded");
    expect(request).toHaveBeenNthCalledWith(1, sampleSession.accessToken);
    expect(request).toHaveBeenNthCalledWith(2, freshSession.accessToken);
    expect(synchronize).toHaveBeenCalledTimes(1);
    expect(auth!.session).not.toHaveProperty("refreshToken");
  });

  it("single-flights concurrent 401 refreshes and retries both requests with the rotated token", async () => {
    const api: {
      [Key in keyof AuthApi]: jest.MockedFunction<AuthApi[Key]>;
    } = {
      getCurrentActor: jest
        .fn<AuthApi["getCurrentActor"]>()
        .mockResolvedValue(sampleSession.actor),
      login: jest.fn<AuthApi["login"]>(),
      logoutSession: jest.fn<AuthApi["logoutSession"]>(),
      refreshSession: jest.fn<AuthApi["refreshSession"]>(),
    };
    const authStorage: {
      [Key in keyof AuthStorage]: jest.MockedFunction<AuthStorage[Key]>;
    } = {
      clearSession: jest
        .fn<AuthStorage["clearSession"]>()
        .mockResolvedValue(undefined),
      loadInstallationId: jest
        .fn<AuthStorage["loadInstallationId"]>()
        .mockResolvedValue(sampleSession.installationId),
      loadSession: jest
        .fn<AuthStorage["loadSession"]>()
        .mockResolvedValue(sampleSession),
      saveInstallationId: jest
        .fn<AuthStorage["saveInstallationId"]>()
        .mockResolvedValue(undefined),
      saveSession: jest
        .fn<AuthStorage["saveSession"]>()
        .mockResolvedValue(undefined),
    };
    const controller = new DefaultAuthSessionCoordinator({
      api,
      now: () => Date.parse("2026-08-17T11:00:00.000Z"),
      storage: authStorage,
    });
    const pendingStorage = createPendingStorageDouble();
    let auth: AuthContextValue | null = null;
    await renderWithProvider(
      controller,
      <CaptureAuth onCapture={(value) => { auth = value; }} />,
      pendingStorage,
    );
    api.getCurrentActor.mockClear();
    authStorage.clearSession.mockClear();
    authStorage.saveSession.mockClear();
    let restoredAccessToken = "";
    await auth!.runAuthenticatedRequest(async (accessToken) => {
      restoredAccessToken = accessToken;
      return undefined;
    });
    expect(restoredAccessToken).toBe(sampleSession.accessToken);

    const firstRefresh = createDeferred<{
      accessToken: string;
      accessTokenExpiresAt: string;
      refreshToken: string;
      refreshTokenExpiresAt: string;
    }>();
    const unauthorized = new ApiClientError({ status: 401 });
    const reusedRefresh = new ApiClientError({
      problem: {
        code: "refresh_token_reused",
        detail: "The refresh token was already used.",
        status: 401,
        title: "Unauthorized",
        traceId: "trace-id",
        type: "https://httpstatuses.com/401",
      },
      status: 401,
    });
    api.getCurrentActor.mockImplementation(async (accessToken) => {
      if (accessToken === sampleSession.accessToken) {
        throw unauthorized;
      }
      return sampleSession.actor;
    });
    api.refreshSession.mockImplementationOnce(() => firstRefresh.promise);
    api.refreshSession.mockRejectedValueOnce(reusedRefresh);
    const firstTokens: string[] = [];
    const secondTokens: string[] = [];
    const firstRequest = jest.fn(async (accessToken: string) => {
      firstTokens.push(accessToken);
      if (firstTokens.length === 1) {
        throw unauthorized;
      }
      return "first-loaded";
    });
    const secondRequest = jest.fn(async (accessToken: string) => {
      secondTokens.push(accessToken);
      if (secondTokens.length === 1) {
        throw unauthorized;
      }
      return "second-loaded";
    });

    let outcomes!: PromiseSettledResult<string>[];
    await act(async () => {
      const concurrentRequests = Promise.allSettled([
        auth!.runAuthenticatedRequest(firstRequest),
        auth!.runAuthenticatedRequest(secondRequest),
      ]);
      await Promise.resolve();
      await Promise.resolve();
      firstRefresh.resolve({
        accessToken: freshSession.accessToken,
        accessTokenExpiresAt: freshSession.accessTokenExpiresAt,
        refreshToken: freshSession.refreshToken,
        refreshTokenExpiresAt: freshSession.refreshTokenExpiresAt,
      });
      outcomes = await concurrentRequests;
    });
    await flush();

    expect(outcomes).toEqual([
      { status: "fulfilled", value: "first-loaded" },
      { status: "fulfilled", value: "second-loaded" },
    ]);
    expect(api.refreshSession).toHaveBeenCalledTimes(1);
    expect(api.refreshSession).toHaveBeenCalledWith(
      sampleSession.refreshToken,
      sampleSession.installationId,
      expect.any(Object),
    );
    expect(firstTokens).toEqual([
      sampleSession.accessToken,
      freshSession.accessToken,
    ]);
    expect(secondTokens).toEqual([
      sampleSession.accessToken,
      freshSession.accessToken,
    ]);
    expect(authStorage.clearSession).not.toHaveBeenCalled();
    expect(pendingStorage.purge).not.toHaveBeenCalled();
    expect(auth!.status).toBe("authenticated");
  });

  it("keeps a rotated session usable when the initiating refresh waiter aborts during refreshed /me", async () => {
    const api: {
      [Key in keyof AuthApi]: jest.MockedFunction<AuthApi[Key]>;
    } = {
      getCurrentActor: jest
        .fn<AuthApi["getCurrentActor"]>()
        .mockResolvedValue(sampleSession.actor),
      login: jest.fn<AuthApi["login"]>(),
      logoutSession: jest.fn<AuthApi["logoutSession"]>(),
      refreshSession: jest.fn<AuthApi["refreshSession"]>(),
    };
    let storedSession: AuthSession | null = sampleSession;
    const authStorage: {
      [Key in keyof AuthStorage]: jest.MockedFunction<AuthStorage[Key]>;
    } = {
      clearSession: jest
        .fn<AuthStorage["clearSession"]>()
        .mockImplementation(async () => {
          storedSession = null;
        }),
      loadInstallationId: jest
        .fn<AuthStorage["loadInstallationId"]>()
        .mockResolvedValue(sampleSession.installationId),
      loadSession: jest
        .fn<AuthStorage["loadSession"]>()
        .mockImplementation(async () => storedSession),
      saveInstallationId: jest
        .fn<AuthStorage["saveInstallationId"]>()
        .mockResolvedValue(undefined),
      saveSession: jest
        .fn<AuthStorage["saveSession"]>()
        .mockImplementation(async (session) => {
          storedSession = session;
        }),
    };
    const controller = new DefaultAuthSessionCoordinator({
      api,
      now: () => Date.parse("2026-08-17T11:00:00.000Z"),
      storage: authStorage,
    });
    const pendingStorage = createPendingStorageDouble();
    let auth: AuthContextValue | null = null;
    await renderWithProvider(
      controller,
      <CaptureAuth onCapture={(value) => { auth = value; }} />,
      pendingStorage,
    );
    api.getCurrentActor.mockClear();
    authStorage.clearSession.mockClear();
    authStorage.saveSession.mockClear();

    const unauthorized = new ApiClientError({ status: 401 });
    const reusedRefresh = new ApiClientError({
      problem: {
        code: "refresh_token_reused",
        detail: "The refresh token was already used.",
        status: 401,
        title: "Unauthorized",
        traceId: "trace-id",
        type: "https://httpstatuses.com/401",
      },
      status: 401,
    });
    const rotatedMeStarted = createDeferred<void>();
    const rotatedActor = createDeferred<AuthSession["actor"]>();
    api.getCurrentActor.mockImplementation((accessToken, signal) => {
      if (accessToken === sampleSession.accessToken) {
        return Promise.reject(unauthorized);
      }

      rotatedMeStarted.resolve(undefined);
      signal?.addEventListener(
        "abort",
        () => {
          const abortError = new Error("The refreshed actor request was aborted.");
          abortError.name = "AbortError";
          rotatedActor.reject(abortError);
        },
        { once: true },
      );
      return rotatedActor.promise;
    });
    api.refreshSession
      .mockResolvedValueOnce({
        accessToken: freshSession.accessToken,
        accessTokenExpiresAt: freshSession.accessTokenExpiresAt,
        refreshToken: freshSession.refreshToken,
        refreshTokenExpiresAt: freshSession.refreshTokenExpiresAt,
      })
      .mockRejectedValueOnce(reusedRefresh);

    const initiatingCaller = new AbortController();
    const immediateUnauthorized = createDeferred<string>();
    const firstRequest = jest.fn(async (accessToken: string) => {
      if (accessToken === sampleSession.accessToken) {
        throw unauthorized;
      }

      return "first-loaded";
    });
    const immediateRequest = jest.fn(async (accessToken: string) => {
      if (accessToken === sampleSession.accessToken) {
        return immediateUnauthorized.promise;
      }

      return "immediate-loaded";
    });
    const laterRequest = jest.fn(async (accessToken: string) => {
      if (accessToken === sampleSession.accessToken) {
        throw unauthorized;
      }

      return "later-loaded";
    });

    let firstOutcome!: PromiseSettledResult<string>;
    let immediateOutcome!: PromiseSettledResult<string>;
    await act(async () => {
      const first = auth!.runAuthenticatedRequest(
        firstRequest,
        initiatingCaller.signal,
      );
      await rotatedMeStarted.promise;
      expect(storedSession?.accessToken).toBe(freshSession.accessToken);

      const immediate = auth!.runAuthenticatedRequest(immediateRequest);
      immediateUnauthorized.reject(unauthorized);
      initiatingCaller.abort();
      await Promise.resolve();
      rotatedActor.resolve(freshSession.actor);

      [firstOutcome, immediateOutcome] = await Promise.allSettled([
        first,
        immediate,
      ]);
    });
    await flush();

    let laterOutcome!: PromiseSettledResult<string>;
    await act(async () => {
      [laterOutcome] = await Promise.allSettled([
        auth!.runAuthenticatedRequest(laterRequest),
      ]);
    });
    await flush();

    expect(firstOutcome).toMatchObject({
      reason: { name: "AbortError" },
      status: "rejected",
    });
    expect(immediateOutcome).toEqual({
      status: "fulfilled",
      value: "immediate-loaded",
    });
    expect(laterOutcome).toEqual({
      status: "fulfilled",
      value: "later-loaded",
    });
    expect(api.refreshSession).toHaveBeenCalledTimes(1);
    expect(api.refreshSession).toHaveBeenCalledWith(
      sampleSession.refreshToken,
      sampleSession.installationId,
      expect.any(Object),
    );
    expect(storedSession).toEqual({
      ...freshSession,
      installationId: sampleSession.installationId,
    });
    expect(authStorage.clearSession).not.toHaveBeenCalled();
    expect(pendingStorage.purge).not.toHaveBeenCalled();
    expect(auth!.status).toBe("authenticated");
  });

  it("commits a rotated session after every initiating waiter aborts", async () => {
    const {
      api,
      authStorage,
      controller,
      getStoredSession,
    } = createRealControllerHarness();
    const pendingStorage = createPendingStorageDouble();
    let auth: AuthContextValue | null = null;
    await renderWithProvider(
      controller,
      <CaptureAuth onCapture={(value) => { auth = value; }} />,
      pendingStorage,
    );
    api.getCurrentActor.mockClear();
    authStorage.clearSession.mockClear();
    authStorage.saveSession.mockClear();

    const unauthorized = new ApiClientError({ status: 401 });
    const rotatedMeStarted = createDeferred<void>();
    const rotatedActor = createDeferred<AuthSession["actor"]>();
    api.getCurrentActor.mockImplementation((accessToken) => {
      if (accessToken === sampleSession.accessToken) {
        return Promise.reject(unauthorized);
      }

      rotatedMeStarted.resolve(undefined);
      return rotatedActor.promise;
    });
    api.refreshSession.mockResolvedValueOnce({
      accessToken: freshSession.accessToken,
      accessTokenExpiresAt: freshSession.accessTokenExpiresAt,
      refreshToken: freshSession.refreshToken,
      refreshTokenExpiresAt: freshSession.refreshTokenExpiresAt,
    });

    const firstCaller = new AbortController();
    const secondCaller = new AbortController();
    const request = jest.fn(async (accessToken: string) => {
      if (accessToken === sampleSession.accessToken) {
        throw unauthorized;
      }

      return "loaded";
    });

    let outcomes!: PromiseSettledResult<string>[];
    await act(async () => {
      const firstPendingRequest = auth!.runAuthenticatedRequest(
        request,
        firstCaller.signal,
      );
      const secondPendingRequest = auth!.runAuthenticatedRequest(
        request,
        secondCaller.signal,
      );
      await rotatedMeStarted.promise;
      expect(getStoredSession()?.accessToken).toBe(freshSession.accessToken);
      firstCaller.abort();
      secondCaller.abort();
      rotatedActor.resolve(freshSession.actor);
      outcomes = await Promise.allSettled([
        firstPendingRequest,
        secondPendingRequest,
      ]);
    });
    await flush();

    let canonicalAccessToken = "";
    await act(async () => {
      await auth!.runAuthenticatedRequest(async (accessToken) => {
        canonicalAccessToken = accessToken;
        return undefined;
      });
    });

    expect(outcomes).toHaveLength(2);
    expect(outcomes).toEqual([
      expect.objectContaining({
        reason: expect.objectContaining({ name: "AbortError" }),
        status: "rejected",
      }),
      expect.objectContaining({
        reason: expect.objectContaining({ name: "AbortError" }),
        status: "rejected",
      }),
    ]);
    expect(canonicalAccessToken).toBe(freshSession.accessToken);
    expect(api.refreshSession).toHaveBeenCalledTimes(1);
    expect(authStorage.clearSession).not.toHaveBeenCalled();
    expect(pendingStorage.purge).not.toHaveBeenCalled();
    expect(auth!.status).toBe("authenticated");
  });

  it("refreshes an expired committed rotation with its rotated refresh token", async () => {
    let now = Date.parse("2026-08-17T11:00:00.000Z");
    const {
      api,
      authStorage,
      controller,
    } = createRealControllerHarness(() => now);
    const pendingStorage = createPendingStorageDouble();
    let auth: AuthContextValue | null = null;
    await renderWithProvider(
      controller,
      <CaptureAuth onCapture={(value) => { auth = value; }} />,
      pendingStorage,
    );
    api.getCurrentActor.mockClear();
    authStorage.clearSession.mockClear();
    authStorage.saveSession.mockClear();

    const unauthorized = new ApiClientError({ status: 401 });
    const rotatedMeStarted = createDeferred<void>();
    const rotatedActor = createDeferred<AuthSession["actor"]>();
    api.getCurrentActor.mockImplementation((accessToken) => {
      if (accessToken === sampleSession.accessToken) {
        return Promise.reject(unauthorized);
      }

      if (accessToken === freshSession.accessToken) {
        rotatedMeStarted.resolve(undefined);
        return rotatedActor.promise;
      }

      return Promise.resolve(secondFreshSession.actor);
    });
    api.refreshSession
      .mockResolvedValueOnce({
        accessToken: freshSession.accessToken,
        accessTokenExpiresAt: freshSession.accessTokenExpiresAt,
        refreshToken: freshSession.refreshToken,
        refreshTokenExpiresAt: freshSession.refreshTokenExpiresAt,
      })
      .mockResolvedValueOnce({
        accessToken: secondFreshSession.accessToken,
        accessTokenExpiresAt: secondFreshSession.accessTokenExpiresAt,
        refreshToken: secondFreshSession.refreshToken,
        refreshTokenExpiresAt: secondFreshSession.refreshTokenExpiresAt,
      });

    const caller = new AbortController();
    let abortedOutcome!: PromiseSettledResult<string>;
    await act(async () => {
      const pendingRequest = auth!.runAuthenticatedRequest(
        async (accessToken) => {
          if (accessToken === sampleSession.accessToken) {
            throw unauthorized;
          }

          return "first-loaded";
        },
        caller.signal,
      );
      await rotatedMeStarted.promise;
      caller.abort();
      rotatedActor.resolve(freshSession.actor);
      [abortedOutcome] = await Promise.allSettled([pendingRequest]);
    });
    await flush();

    now = Date.parse("2026-08-17T12:11:00.000Z");
    let loaded: string | undefined;
    await act(async () => {
      loaded = await auth!.runAuthenticatedRequest(async (accessToken) => {
        if (accessToken !== secondFreshSession.accessToken) {
          throw unauthorized;
        }

        return "second-loaded";
      });
    });
    await flush();

    expect(abortedOutcome).toMatchObject({
      reason: { name: "AbortError" },
      status: "rejected",
    });
    expect(loaded).toBe("second-loaded");
    expect(api.refreshSession).toHaveBeenCalledTimes(2);
    expect(api.refreshSession).toHaveBeenNthCalledWith(
      2,
      freshSession.refreshToken,
      sampleSession.installationId,
      expect.any(Object),
    );
    expect(authStorage.clearSession).not.toHaveBeenCalled();
    expect(pendingStorage.purge).not.toHaveBeenCalled();
    expect(auth!.status).toBe("authenticated");
  });

  it("hard-times out refreshed /me that ignores abort, commits rotation, and clears the flight", async () => {
    jest.useFakeTimers();
    const {
      api,
      authStorage,
      controller,
    } = createRealControllerHarness();
    const pendingStorage = createPendingStorageDouble();
    let auth: AuthContextValue | null = null;
    const rotatedActor = createDeferred<AuthSession["actor"]>();
    let sharedSignal: AbortSignal | undefined;
    let pendingRequest: Promise<string> | undefined;

    try {
      await renderWithProvider(
        controller,
        <CaptureAuth onCapture={(value) => { auth = value; }} />,
        pendingStorage,
      );
      api.getCurrentActor.mockClear();
      authStorage.clearSession.mockClear();
      authStorage.saveSession.mockClear();

      const unauthorized = new ApiClientError({ status: 401 });
      let refreshedMeCalls = 0;
      api.getCurrentActor.mockImplementation((accessToken, signal) => {
        if (accessToken === sampleSession.accessToken) {
          return Promise.reject(unauthorized);
        }

        refreshedMeCalls += 1;
        if (refreshedMeCalls > 1) {
          return Promise.resolve(freshSession.actor);
        }

        sharedSignal = signal;
        return rotatedActor.promise;
      });
      api.refreshSession.mockResolvedValueOnce({
        accessToken: freshSession.accessToken,
        accessTokenExpiresAt: freshSession.accessTokenExpiresAt,
        refreshToken: freshSession.refreshToken,
        refreshTokenExpiresAt: freshSession.refreshTokenExpiresAt,
      });

      const request = jest.fn(async (accessToken: string) => {
        if (accessToken === sampleSession.accessToken) {
          throw unauthorized;
        }

        return "loaded";
      });

      await act(async () => {
        pendingRequest = auth!.runAuthenticatedRequest(request);
        await waitForCall(api.refreshSession);
        await Promise.resolve();
      });

      expect(sharedSignal?.aborted).toBe(false);
      let pendingOutcome: PromiseSettledResult<string> | undefined;
      void pendingRequest!.then(
        (value) => {
          pendingOutcome = { status: "fulfilled", value };
        },
        (reason: unknown) => {
          pendingOutcome = { reason, status: "rejected" };
        },
      );

      await act(async () => {
        await jest.advanceTimersByTimeAsync(10_000);
      });
      await flush();

      expect(sharedSignal?.aborted).toBe(true);
      expect(pendingOutcome).toEqual({
        status: "fulfilled",
        value: "loaded",
      });

      let subsequentAttempts = 0;
      let subsequentResult: string | undefined;
      await act(async () => {
        subsequentResult = await auth!.runAuthenticatedRequest(
          async (accessToken) => {
            subsequentAttempts += 1;
            if (subsequentAttempts === 1) {
              throw unauthorized;
            }

            expect(accessToken).toBe(freshSession.accessToken);
            return "continued";
          },
        );
      });
      await flush();

      expect(subsequentResult).toBe("continued");
      expect(refreshedMeCalls).toBe(2);
      expect(api.refreshSession).toHaveBeenCalledTimes(1);
      expect(authStorage.clearSession).not.toHaveBeenCalled();
      expect(pendingStorage.purge).not.toHaveBeenCalled();
      expect(auth!.status).toBe("authenticated");
    } finally {
      rotatedActor.resolve(freshSession.actor);
      if (pendingRequest) {
        await Promise.allSettled([pendingRequest]);
      }
      jest.useRealTimers();
    }
  });

  it("does not reuse an expired flight whose controller promise never settles", async () => {
    jest.useFakeTimers();
    const firstSynchronization = createDeferred<AuthSession>();
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(sampleSession),
    });
    const pendingStorage = createPendingStorageDouble();
    let auth: AuthContextValue | null = null;
    let firstSignal: AbortSignal | undefined;
    let firstOperation: Promise<string> | undefined;
    let secondOperation: Promise<string> | undefined;

    try {
      await renderWithProvider(
        controller,
        <CaptureAuth onCapture={(value) => { auth = value; }} />,
        pendingStorage,
      );
      const synchronize = controller.synchronize as jest.MockedFunction<
        AuthSessionCoordinator["synchronize"]
      >;
      synchronize.mockClear();
      synchronize
        .mockImplementationOnce((_session, signal) => {
          firstSignal = signal;
          return firstSynchronization.promise;
        })
        .mockResolvedValueOnce(freshSession);
      const unauthorized = new ApiClientError({ status: 401 });

      await act(async () => {
        firstOperation = auth!.runAuthenticatedRequest(async (accessToken) => {
          if (accessToken === sampleSession.accessToken) {
            throw unauthorized;
          }

          return "first-loaded";
        });
        void firstOperation.catch(() => undefined);
        await waitForCall(synchronize);
      });

      expect(synchronize).toHaveBeenCalledTimes(1);
      jest.advanceTimersToNextTimer();
      expect(firstSignal?.aborted).toBe(true);

      await act(async () => {
        secondOperation = auth!.runAuthenticatedRequest(async (accessToken) => {
          if (accessToken === sampleSession.accessToken) {
            throw unauthorized;
          }

          return "second-loaded";
        });
        await Promise.resolve();
        await Promise.resolve();
      });

      expect(synchronize).toHaveBeenCalledTimes(2);

      let outcomes!: PromiseSettledResult<string>[];
      await act(async () => {
        await jest.runOnlyPendingTimersAsync();
        outcomes = await Promise.allSettled([
          firstOperation!,
          secondOperation!,
        ]);
      });
      await flush();

      expect(outcomes[0]).toMatchObject({
        reason: expect.objectContaining({ name: "TimeoutError" }),
        status: "rejected",
      });
      expect(outcomes[1]).toEqual({
        status: "fulfilled",
        value: "second-loaded",
      });
      expect(auth!.status).toBe("authenticated");
      expect(auth!.session?.actor.membership.displayName).toBe("Fresh Member");
      expect(pendingStorage.purge).not.toHaveBeenCalled();
    } finally {
      firstSynchronization.resolve(sampleSession);
      await Promise.allSettled(
        [firstOperation, secondOperation].filter(
          (operation): operation is Promise<string> => operation !== undefined,
        ),
      );
      jest.useRealTimers();
    }
  });

  it("uses the canonical rotated session for a late concurrent 401", async () => {
    const synchronization = createDeferred<AuthSession>();
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(sampleSession),
    });
    let auth: AuthContextValue | null = null;
    await renderWithProvider(
      controller,
      <CaptureAuth onCapture={(value) => { auth = value; }} />,
    );
    const synchronize = controller.synchronize as jest.MockedFunction<
      AuthSessionCoordinator["synchronize"]
    >;
    synchronize.mockClear();
    synchronize.mockReturnValueOnce(synchronization.promise);
    const unauthorized = new ApiClientError({ status: 401 });
    const lateUnauthorized = createDeferred<string>();
    const firstTokens: string[] = [];
    const lateTokens: string[] = [];
    const firstRequest = jest.fn(async (accessToken: string) => {
      firstTokens.push(accessToken);
      if (accessToken === sampleSession.accessToken) {
        throw unauthorized;
      }

      return "first-loaded";
    });
    const lateRequest = jest.fn(async (accessToken: string) => {
      lateTokens.push(accessToken);
      if (accessToken === sampleSession.accessToken) {
        return lateUnauthorized.promise;
      }

      return "late-loaded";
    });

    let outcomes!: PromiseSettledResult<string>[];
    await act(async () => {
      const first = auth!.runAuthenticatedRequest(firstRequest);
      const late = auth!.runAuthenticatedRequest(lateRequest);
      await waitForCall(synchronize);
      synchronization.resolve(freshSession);
      await first;
      lateUnauthorized.reject(unauthorized);
      outcomes = await Promise.allSettled([first, late]);
    });
    await flush();

    expect(outcomes).toEqual([
      { status: "fulfilled", value: "first-loaded" },
      { status: "fulfilled", value: "late-loaded" },
    ]);
    expect(synchronize).toHaveBeenCalledTimes(1);
    expect(firstTokens).toEqual([
      sampleSession.accessToken,
      freshSession.accessToken,
    ]);
    expect(lateTokens).toEqual([
      sampleSession.accessToken,
      freshSession.accessToken,
    ]);
    expect(auth!.status).toBe("authenticated");
  });

  it("signs out and purges the pending command after a second 401", async () => {
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(sampleSession),
    });
    const pendingStorage = createPendingStorageDouble();
    let auth: AuthContextValue | null = null;
    await renderWithProvider(
      controller,
      <CaptureAuth onCapture={(value) => { auth = value; }} />,
      pendingStorage,
    );
    const synchronize = controller.synchronize as jest.MockedFunction<
      AuthSessionCoordinator["synchronize"]
    >;
    synchronize.mockClear();
    synchronize.mockResolvedValueOnce(freshSession);
    const request = jest
      .fn<(accessToken: string) => Promise<string>>()
      .mockRejectedValue(new ApiClientError({ status: 401 }));

    let caught: unknown;
    await act(async () => {
      try {
        await auth!.runAuthenticatedRequest(request);
      } catch (error) {
        caught = error;
      }
    });
    await flush();

    expect(caught).toMatchObject({ status: 401 });
    expect(controller.signOut).toHaveBeenCalled();
    expect(pendingStorage.purge).toHaveBeenCalled();
    expect(auth!.status).toBe("unauthenticated");
  });

  it("signs out and purges when refresh synchronization fails", async () => {
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(sampleSession),
    });
    const pendingStorage = createPendingStorageDouble();
    let auth: AuthContextValue | null = null;
    await renderWithProvider(
      controller,
      <CaptureAuth onCapture={(value) => { auth = value; }} />,
      pendingStorage,
    );
    const synchronize = controller.synchronize as jest.MockedFunction<
      AuthSessionCoordinator["synchronize"]
    >;
    synchronize.mockClear();
    synchronize.mockRejectedValueOnce(new ApiClientError({ status: 401 }));
    const request = jest
      .fn<(accessToken: string) => Promise<string>>()
      .mockRejectedValueOnce(new ApiClientError({ status: 401 }));

    let caught: unknown;
    await act(async () => {
      try {
        await auth!.runAuthenticatedRequest(request);
      } catch (error) {
        caught = error;
      }
    });
    await flush();

    expect(caught).toMatchObject({ status: 401 });
    expect(controller.signOut).toHaveBeenCalled();
    expect(pendingStorage.purge).toHaveBeenCalled();
    expect(auth!.status).toBe("unauthenticated");
  });

  it("keeps terminal session loss blocked after deletion interruption and completes cleanup after remount", async () => {
    const synchronizationError = new ApiClientError({
      problem: {
        code: "refresh_token_reused",
        detail: "The refresh token was already used.",
        status: 401,
        title: "Unauthorized",
        traceId: "trace-id",
        type: "https://httpstatuses.com/401",
      },
      status: 401,
    });
    const deletionError = new Error("secure auth deletion interrupted");
    let storedSession: AuthSession | null = sampleSession;
    let pendingCommandPresent = true;
    let deletionAttempts = 0;
    const signOut = jest
      .fn<AuthSessionCoordinator["signOut"]>()
      .mockImplementation(async () => {
        deletionAttempts += 1;
        if (deletionAttempts === 1) {
          throw deletionError;
        }
        storedSession = null;
      });
    const controller = createControllerDouble({
      restore: jest
        .fn<AuthSessionCoordinator["restore"]>()
        .mockImplementation(async () => storedSession),
      signOut,
      synchronize: jest
        .fn<AuthSessionCoordinator["synchronize"]>()
        .mockRejectedValue(synchronizationError),
    });
    const pendingStorage = createPendingStorageDouble();
    pendingStorage.purge.mockImplementation(async () => {
      pendingCommandPresent = false;
    });
    let auth: AuthContextValue | null = null;
    let renderer = await renderWithProvider(
      controller,
      <>
        <MemberLayout />
        <CaptureAuth onCapture={(value) => { auth = value; }} />
      </>,
      pendingStorage,
    );

    await waitForCall(signOut);
    await flush();

    expect(storedSession).toBe(sampleSession);
    expect(pendingCommandPresent).toBe(false);
    expect(auth!.session).toBeNull();
    expect(auth!.status).toBe("restoring");
    expect(auth!.error?.detail).toBe(deletionError.message);
    expect(
      renderer.root.findByProps({
        accessibilityLabel: "Retry secure sign-out",
      }),
    ).toBeDefined();

    await act(async () => {
      renderer.unmount();
    });

    auth = null;
    renderer = await renderWithProvider(
      controller,
      <>
        <MemberLayout />
        <CaptureAuth onCapture={(value) => { auth = value; }} />
      </>,
      pendingStorage,
    );

    for (
      let attempt = 0;
      attempt < 20 && signOut.mock.calls.length < 2;
      attempt += 1
    ) {
      await flush();
    }

    expect(signOut).toHaveBeenCalledTimes(2);
    expect(storedSession).toBeNull();
    expect(pendingCommandPresent).toBe(false);
    expect(auth!.status).toBe("unauthenticated");
    expect(renderer.root.findByProps({ href: "/(auth)" }).props.href).toBe(
      "/(auth)",
    );

    await act(async () => {
      renderer.unmount();
    });
  });

  it("purges and aborts an in-flight request when synchronization changes identity", async () => {
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(sampleSession),
    });
    const pendingStorage = createPendingStorageDouble();
    let auth: AuthContextValue | null = null;
    await renderWithProvider(
      controller,
      <CaptureAuth onCapture={(value) => { auth = value; }} />,
      pendingStorage,
    );
    const synchronize = controller.synchronize as jest.MockedFunction<
      AuthSessionCoordinator["synchronize"]
    >;
    synchronize.mockClear();
    synchronize.mockResolvedValueOnce({
      ...freshSession,
      actor: {
        ...freshSession.actor,
        membership: {
          ...freshSession.actor.membership,
          id: "membership-2",
        },
      },
    });
    const request = jest
      .fn<(accessToken: string) => Promise<string>>()
      .mockRejectedValueOnce(new ApiClientError({ status: 401 }));

    let caught: unknown;
    await act(async () => {
      try {
        await auth!.runAuthenticatedRequest(request);
      } catch (error) {
        caught = error;
      }
    });
    await flush();

    expect(caught).toMatchObject({ name: "AbortError" });
    expect(request).toHaveBeenCalledTimes(1);
    expect(pendingStorage.purge).toHaveBeenCalled();
    expect(auth!.session?.actor.membership.id).toBe("membership-2");
  });
});

async function renderWithProvider(
  controller: AuthSessionCoordinator,
  element: ReactNode,
  pendingSignupStorage?: PendingSignupStorage,
): Promise<ReactTestRenderer> {
  let renderer: ReactTestRenderer | undefined;

  await act(async () => {
    renderer = create(
      <AuthSessionProvider
        controller={controller}
        pendingSignupStorage={pendingSignupStorage}
      >
        {element}
      </AuthSessionProvider>,
    );
  });

  await flush();

  if (!renderer) {
    throw new Error("Renderer was not created.");
  }

  return renderer;
}

function CaptureAuth({
  onCapture,
}: {
  readonly onCapture: (value: AuthContextValue) => void;
}) {
  onCapture(useAuth());
  return null;
}

function createPendingStorageDouble(): jest.Mocked<PendingSignupStorage> {
  return {
    admit: jest
      .fn<PendingSignupStorage["admit"]>()
      .mockRejectedValue(new Error("not used")),
    clearIfCurrent: jest
      .fn<PendingSignupStorage["clearIfCurrent"]>()
      .mockResolvedValue(false),
    load: jest.fn<PendingSignupStorage["load"]>().mockResolvedValue(null),
    loadForIdentity: jest
      .fn<PendingSignupStorage["loadForIdentity"]>()
      .mockResolvedValue(null),
    purge: jest
      .fn<PendingSignupStorage["purge"]>()
      .mockResolvedValue(undefined),
    replaceIfCurrent: jest
      .fn<PendingSignupStorage["replaceIfCurrent"]>()
      .mockResolvedValue(null),
  };
}

function createRealControllerHarness(now = () =>
  Date.parse("2026-08-17T11:00:00.000Z")) {
  const api: {
    [Key in keyof AuthApi]: jest.MockedFunction<AuthApi[Key]>;
  } = {
    getCurrentActor: jest
      .fn<AuthApi["getCurrentActor"]>()
      .mockResolvedValue(sampleSession.actor),
    login: jest.fn<AuthApi["login"]>(),
    logoutSession: jest.fn<AuthApi["logoutSession"]>(),
    refreshSession: jest.fn<AuthApi["refreshSession"]>(),
  };
  let storedSession: AuthSession | null = sampleSession;
  const authStorage: {
    [Key in keyof AuthStorage]: jest.MockedFunction<AuthStorage[Key]>;
  } = {
    clearSession: jest
      .fn<AuthStorage["clearSession"]>()
      .mockImplementation(async () => {
        storedSession = null;
      }),
    loadInstallationId: jest
      .fn<AuthStorage["loadInstallationId"]>()
      .mockResolvedValue(sampleSession.installationId),
    loadSession: jest
      .fn<AuthStorage["loadSession"]>()
      .mockImplementation(async () => storedSession),
    saveInstallationId: jest
      .fn<AuthStorage["saveInstallationId"]>()
      .mockResolvedValue(undefined),
    saveSession: jest
      .fn<AuthStorage["saveSession"]>()
      .mockImplementation(async (session) => {
        storedSession = session;
      }),
  };

  return {
    api,
    authStorage,
    controller: new DefaultAuthSessionCoordinator({
      api,
      now,
      storage: authStorage,
    }),
    getStoredSession: () => storedSession,
  };
}

async function submitCredentials(renderer: ReactTestRenderer): Promise<void> {
  const inputs = renderer.root.findAllByType(TextInput);
  const emailInput = inputs.find(
    (candidate) => candidate.props.accessibilityLabel === "Email address",
  );
  const passwordInput = inputs.find(
    (candidate) => candidate.props.accessibilityLabel === "Password",
  );
  const button = renderer.root.findByProps({
    accessibilityLabel: "Sign in securely",
  });

  await act(async () => {
    emailInput?.props.onChangeText("member@example.test");
    passwordInput?.props.onChangeText("Passw0rd!Passw0rd!");
  });

  await flush();

  await act(async () => {
    button.props.onPress();
  });

  await flush();
}

async function pressSignOut(renderer: ReactTestRenderer): Promise<void> {
  const button = renderer.root.findByProps({
    accessibilityLabel: "Sign out securely",
  });

  await act(async () => {
    button.props.onPress();
  });

  await flush();
}

function createControllerDouble(
  overrides: Partial<{
    restore: jest.MockedFunction<AuthSessionCoordinator["restore"]>;
    signIn: jest.MockedFunction<AuthSessionCoordinator["signIn"]>;
    signOut: jest.MockedFunction<AuthSessionCoordinator["signOut"]>;
    synchronize: jest.MockedFunction<AuthSessionCoordinator["synchronize"]>;
  }> = {},
): AuthSessionCoordinator {
  return {
    restore: overrides.restore ?? jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(null),
    signIn:
      overrides.signIn ??
      jest.fn<AuthSessionCoordinator["signIn"]>().mockResolvedValue(sampleSession),
    signOut:
      overrides.signOut ??
      jest.fn<AuthSessionCoordinator["signOut"]>().mockResolvedValue(undefined),
    synchronize:
      overrides.synchronize ??
      jest
        .fn<AuthSessionCoordinator["synchronize"]>()
        .mockResolvedValue(sampleSession),
  };
}

async function flush(): Promise<void> {
  await act(async () => {
    await Promise.resolve();
  });
}

function createDeferred<T>(): {
  readonly promise: Promise<T>;
  readonly resolve: (value: T | PromiseLike<T>) => void;
  readonly reject: (reason?: unknown) => void;
} {
  let resolve!: (value: T | PromiseLike<T>) => void;
  let reject!: (reason?: unknown) => void;

  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
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

function readText(node: { children?: readonly unknown[] }): string {
  return (node.children ?? [])
    .flatMap((child) => {
      if (typeof child === "string") {
        return [child];
      }

      if (typeof child === "object" && child !== null && "children" in child) {
        return [readText(child as { children?: readonly unknown[] })];
      }

      return [];
    })
    .join(" ");
}
