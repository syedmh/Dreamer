import type { PropsWithChildren } from "react";
import {
  createContext,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";

import { ApiClientError, getMobileApi } from "../../core/api/auth-api";
import { publishFeatureRefresh } from "../../core/api/feature-refresh-bus";
import {
  createRequestScope,
  runWithRequestScope,
  runWithSignal,
  type RequestScope,
} from "../../core/api/operation-runtime";
import {
  DefaultAuthSessionCoordinator,
  isAbortError,
  type AuthSessionCoordinator,
} from "../../core/security/auth-session-controller";
import {
  getPendingSignupStorage,
  type PendingSignupStorage,
} from "../../core/security/pending-signup-storage";
import { SecureStoreAuthStorage } from "../../core/security/secure-auth-storage";
import type {
  AuthCredentials,
  AuthSession,
} from "../../core/security/auth-session-types";
import { toAuthViewError, type AuthViewError } from "./auth-errors";

export interface AuthenticatedActorSession {
  readonly actor: AuthSession["actor"];
}

export interface AuthContextValue {
  readonly clearError: () => void;
  readonly error: AuthViewError | null;
  readonly isSigningIn: boolean;
  readonly isSigningOut: boolean;
  readonly runAuthenticatedRequest: <T>(
    request: (accessToken: string) => Promise<T>,
    signal?: AbortSignal,
  ) => Promise<T>;
  readonly session: AuthenticatedActorSession | null;
  readonly signIn: (credentials: AuthCredentials) => Promise<void>;
  readonly signOut: () => Promise<void>;
  readonly status: "authenticated" | "restoring" | "unauthenticated";
}

export interface AuthSessionProviderProps extends PropsWithChildren {
  readonly controller?: AuthSessionCoordinator;
  readonly pendingSignupStorage?: PendingSignupStorage;
}

interface SessionSynchronizationFlight {
  readonly promise: Promise<AuthSession>;
  readonly scope: RequestScope;
  readonly sessionController: AbortController;
  readonly sessionVersion: number;
  readonly synchronizationGeneration: number;
}

export const AuthContext = createContext<AuthContextValue | null>(null);

let defaultController: AuthSessionCoordinator | null = null;

function getDefaultController(): AuthSessionCoordinator {
  if (!defaultController) {
    defaultController = new DefaultAuthSessionCoordinator({
      api: getMobileApi(),
      storage: new SecureStoreAuthStorage(),
    });
  }

  return defaultController;
}

function getDefaultPendingSignupStorage(): PendingSignupStorage {
  return getPendingSignupStorage();
}

export function AuthSessionProvider({
  children,
  controller,
  pendingSignupStorage,
}: AuthSessionProviderProps) {
  const resolvedController = useMemo(
    () => controller ?? getDefaultController(),
    [controller],
  );
  const resolvedPendingStorage = useMemo(
    () => pendingSignupStorage ?? getDefaultPendingSignupStorage(),
    [pendingSignupStorage],
  );
  const [session, setSession] = useState<AuthSession | null>(null);
  const [status, setStatus] = useState<AuthContextValue["status"]>("restoring");
  const [error, setError] = useState<AuthViewError | null>(null);
  const [isSigningIn, setIsSigningIn] = useState(false);
  const [isSigningOut, setIsSigningOut] = useState(false);
  const actionControllerRef = useRef<AbortController | null>(null);
  const isMountedRef = useRef(true);
  const sessionVersionRef = useRef(0);
  const sessionRef = useRef<AuthSession | null>(null);
  const synchronizationGenerationRef = useRef(0);
  const synchronizationFlightRef =
    useRef<SessionSynchronizationFlight | null>(null);
  const sessionWorkControllerRef = useRef<AbortController | null>(null);

  useEffect(
    () => () => {
      isMountedRef.current = false;
      synchronizationFlightRef.current = null;
      sessionWorkControllerRef.current?.abort();
      actionControllerRef.current?.abort();
    },
    [],
  );

  const clearError = useCallback(() => {
    setError(null);
  }, []);

  const advanceSessionVersion = useCallback((): number => {
    sessionVersionRef.current += 1;
    return sessionVersionRef.current;
  }, []);

  const isCurrentSessionVersion = useCallback(
    (sessionVersion: number): boolean =>
      sessionVersionRef.current === sessionVersion,
    [],
  );

  const replaceActionController = useCallback((): AbortController => {
    actionControllerRef.current?.abort();
    const nextController = new AbortController();
    actionControllerRef.current = nextController;
    return nextController;
  }, []);

  const cancelSessionWork = useCallback((): void => {
    synchronizationGenerationRef.current += 1;
    synchronizationFlightRef.current = null;
    sessionWorkControllerRef.current?.abort();
    sessionWorkControllerRef.current = null;
  }, []);

  const replaceSessionWorkController = useCallback((): AbortController => {
    cancelSessionWork();
    const nextController = new AbortController();
    sessionWorkControllerRef.current = nextController;
    return nextController;
  }, [cancelSessionWork]);

  const purgePendingSignup = useCallback(async (
    signal?: AbortSignal,
  ): Promise<void> => {
    publishFeatureRefresh("session-cleared");
    const purge = resolvedPendingStorage.purge();
    await runWithSignal(
      () => purge,
      signal,
    );
  }, [resolvedPendingStorage]);

  const applyAuthenticatedState = useCallback(
    (nextSession: AuthSession, sessionVersion: number): boolean => {
      if (!isMountedRef.current || !isCurrentSessionVersion(sessionVersion)) {
        return false;
      }

      sessionRef.current = nextSession;
      setSession(nextSession);
      setStatus("authenticated");
      setError(null);
      return true;
    },
    [isCurrentSessionVersion],
  );

  const applySignedOutState = useCallback(
    (nextError: AuthViewError | null, sessionVersion: number): boolean => {
      if (!isMountedRef.current || !isCurrentSessionVersion(sessionVersion)) {
        return false;
      }

      sessionRef.current = null;
      setSession(null);
      setStatus("unauthenticated");
      setError(nextError);
      return true;
    },
    [isCurrentSessionVersion],
  );

  const runSessionCleanup = useCallback(
    async (
      currentSession: AuthSession | null,
      parentSignal?: AbortSignal,
    ): Promise<void> => {
      const results = await Promise.allSettled([
        runWithRequestScope(
          (signal) => purgePendingSignup(signal),
          parentSignal,
        ),
        resolvedController.signOut(currentSession, parentSignal),
      ]);
      const failure = results.find(
        (result): result is PromiseRejectedResult =>
          result.status === "rejected",
      );
      if (failure) {
        throw failure.reason;
      }
    },
    [purgePendingSignup, resolvedController],
  );

  const commitSessionCleanup = useCallback(
    async (
      currentSession: AuthSession | null,
      sessionError: unknown,
      sessionVersion: number,
    ): Promise<boolean> => {
      if (!isMountedRef.current || !isCurrentSessionVersion(sessionVersion)) {
        return false;
      }

      sessionRef.current = null;
      setSession(null);
      setStatus("restoring");
      setError(toAuthViewError(sessionError));
      setIsSigningOut(true);
      const cleanupController = replaceActionController();

      try {
        await runSessionCleanup(currentSession, cleanupController.signal);
        if (
          cleanupController.signal.aborted ||
          !isMountedRef.current ||
          !isCurrentSessionVersion(sessionVersion)
        ) {
          return false;
        }

        return applySignedOutState(
          toAuthViewError(sessionError),
          sessionVersion,
        );
      } catch (cleanupError) {
        if (
          cleanupController.signal.aborted ||
          !isMountedRef.current ||
          !isCurrentSessionVersion(sessionVersion)
        ) {
          return false;
        }

        setError(toAuthViewError(cleanupError));
        return true;
      } finally {
        if (
          isMountedRef.current &&
          actionControllerRef.current === cleanupController
        ) {
          setIsSigningOut(false);
        }
      }
    },
    [
      applySignedOutState,
      isCurrentSessionVersion,
      replaceActionController,
      runSessionCleanup,
    ],
  );

  const prepareIdentityChange = useCallback(
    async (
      previousSession: AuthSession | null,
      nextSession: AuthSession,
    ): Promise<void> => {
      if (previousSession && !isSameIdentity(previousSession, nextSession)) {
        await purgePendingSignup();
      }
    },
    [purgePendingSignup],
  );

  const commitSynchronizedSession = useCallback(
    async (
      currentSession: AuthSession,
      synchronizedSession: AuthSession,
      sessionVersion: number,
      synchronizationGeneration: number,
      sessionController: AbortController,
    ): Promise<boolean> => {
      if (
        !isMountedRef.current ||
        sessionController.signal.aborted ||
        !isCurrentSessionVersion(sessionVersion) ||
        synchronizationGenerationRef.current !== synchronizationGeneration
      ) {
        return false;
      }

      if (isSameIdentity(currentSession, synchronizedSession)) {
        return applyAuthenticatedState(synchronizedSession, sessionVersion);
      }

      cancelSessionWork();
      const replacementVersion = advanceSessionVersion();
      sessionRef.current = null;
      await prepareIdentityChange(currentSession, synchronizedSession);

      if (
        !isMountedRef.current ||
        !isCurrentSessionVersion(replacementVersion)
      ) {
        return false;
      }

      replaceSessionWorkController();
      return applyAuthenticatedState(synchronizedSession, replacementVersion);
    },
    [
      advanceSessionVersion,
      applyAuthenticatedState,
      cancelSessionWork,
      isCurrentSessionVersion,
      prepareIdentityChange,
      replaceSessionWorkController,
    ],
  );

  const commitTerminalSessionLoss = useCallback(
    async (
      currentSession: AuthSession,
      sessionVersion: number,
      synchronizationGeneration: number,
      sessionController: AbortController,
      syncError: unknown,
    ): Promise<boolean> => {
      if (
        !isMountedRef.current ||
        sessionController.signal.aborted ||
        !isCurrentSessionVersion(sessionVersion) ||
        synchronizationGenerationRef.current !== synchronizationGeneration
      ) {
        return false;
      }

      cancelSessionWork();
      const signedOutVersion = advanceSessionVersion();
      return commitSessionCleanup(
        currentSession,
        syncError,
        signedOutVersion,
      );
    },
    [
      advanceSessionVersion,
      cancelSessionWork,
      commitSessionCleanup,
      isCurrentSessionVersion,
    ],
  );

  const getSynchronizationFlight = useCallback(
    (
      currentSession: AuthSession,
      sessionVersion: number,
    ): SessionSynchronizationFlight => {
      const existingFlight = synchronizationFlightRef.current;
      if (
        existingFlight?.sessionVersion === sessionVersion &&
        existingFlight.synchronizationGeneration ===
          synchronizationGenerationRef.current &&
        !existingFlight.scope.signal.aborted
      ) {
        return existingFlight;
      }

      if (existingFlight) {
        if (!existingFlight.scope.signal.aborted) {
          existingFlight.sessionController.abort();
        }
        synchronizationFlightRef.current = null;
      }

      const sessionController = sessionWorkControllerRef.current;
      if (
        !sessionController ||
        sessionController.signal.aborted ||
        !isMountedRef.current ||
        !isCurrentSessionVersion(sessionVersion)
      ) {
        throw createAbortError();
      }

      const scope = createRequestScope(sessionController.signal);
      const synchronizationGeneration =
        synchronizationGenerationRef.current + 1;
      synchronizationGenerationRef.current = synchronizationGeneration;
      let flight!: SessionSynchronizationFlight;
      const clearExpiredFlight = (): void => {
        if (synchronizationFlightRef.current === flight) {
          synchronizationFlightRef.current = null;
        }
      };
      const promise = (async (): Promise<AuthSession> => {
        try {
          const synchronizedSession = await runWithSignal(
            () => resolvedController.synchronize(
              currentSession,
              scope.signal,
            ),
            scope.signal,
            true,
          );
          const didCommit = await commitSynchronizedSession(
            currentSession,
            synchronizedSession,
            sessionVersion,
            synchronizationGeneration,
            sessionController,
          );
          if (!didCommit) {
            throw createAbortError();
          }

          return synchronizedSession;
        } catch (syncError) {
          if (
            isAbortError(syncError) ||
            scope.signal.aborted ||
            sessionController.signal.aborted ||
            !isMountedRef.current ||
            !isCurrentSessionVersion(sessionVersion) ||
            synchronizationGenerationRef.current !==
              synchronizationGeneration
          ) {
            throw syncError;
          }

          const didCommit = await commitTerminalSessionLoss(
            currentSession,
            sessionVersion,
            synchronizationGeneration,
            sessionController,
            syncError,
          );
          if (!didCommit) {
            throw createAbortError();
          }

          throw syncError;
        } finally {
          scope.signal.removeEventListener("abort", clearExpiredFlight);
          scope.dispose();
          if (synchronizationFlightRef.current === flight) {
            synchronizationFlightRef.current = null;
          }
        }
      })();

      flight = {
        promise,
        scope,
        sessionController,
        sessionVersion,
        synchronizationGeneration,
      };
      synchronizationFlightRef.current = flight;
      scope.signal.addEventListener("abort", clearExpiredFlight, {
        once: true,
      });
      return flight;
    },
    [
      commitSynchronizedSession,
      commitTerminalSessionLoss,
      isCurrentSessionVersion,
      resolvedController,
    ],
  );

  const awaitSessionSynchronization = useCallback(
    (
      currentSession: AuthSession,
      sessionVersion: number,
      waiterSignal?: AbortSignal,
    ): Promise<AuthSession> => {
      const flight = getSynchronizationFlight(
        currentSession,
        sessionVersion,
      );
      return awaitForWaiter(flight.promise, waiterSignal);
    },
    [getSynchronizationFlight],
  );

  useEffect(() => {
    const restoreController = replaceSessionWorkController();
    const restoreScope = createRequestScope(restoreController.signal);
    const sessionVersion = advanceSessionVersion();
    let isActive = true;

    void (async () => {
      try {
        const restoredSession = await runWithSignal(
          () => resolvedController.restore(restoreScope.signal),
          restoreScope.signal,
          true,
        );

        if (!isActive || restoreController.signal.aborted) {
          return;
        }

        if (!restoredSession) {
          await commitSessionCleanup(null, null, sessionVersion);
          return;
        }

        if (!applyAuthenticatedState(restoredSession, sessionVersion)) {
          return;
        }

        void awaitSessionSynchronization(restoredSession, sessionVersion).catch(
          () => undefined,
        );
      } catch (restoreError) {
        if (
          isAbortError(restoreError) ||
          !isActive ||
          restoreController.signal.aborted
        ) {
          return;
        }
        cancelSessionWork();
        await commitSessionCleanup(null, restoreError, sessionVersion);
      } finally {
        restoreScope.dispose();
      }
    })();

    return () => {
      isActive = false;
      if (sessionWorkControllerRef.current === restoreController) {
        cancelSessionWork();
      } else {
        restoreController.abort();
      }
      restoreScope.dispose();
      actionControllerRef.current?.abort();
    };
  }, [
    advanceSessionVersion,
    applyAuthenticatedState,
    awaitSessionSynchronization,
    cancelSessionWork,
    commitSessionCleanup,
    replaceSessionWorkController,
    resolvedController,
  ]);

  const supersedeActiveSession = useCallback((): number => {
    cancelSessionWork();
    return advanceSessionVersion();
  }, [advanceSessionVersion, cancelSessionWork]);

  const signIn = useCallback(
    async (credentials: AuthCredentials): Promise<void> => {
      const previousSession = sessionRef.current;
      const sessionVersion = supersedeActiveSession();
      setIsSigningOut(false);
      setIsSigningIn(true);
      setError(null);
      const requestController = replaceActionController();

      try {
        const nextSession = await resolvedController.signIn(
          credentials,
          requestController.signal,
        );
        if (requestController.signal.aborted) {
          return;
        }

        await prepareIdentityChange(previousSession, nextSession);
        if (
          requestController.signal.aborted ||
          !isCurrentSessionVersion(sessionVersion)
        ) {
          return;
        }

        replaceSessionWorkController();
        applyAuthenticatedState(nextSession, sessionVersion);
      } catch (signInError) {
        if (isAbortError(signInError) || requestController.signal.aborted) {
          return;
        }

        await purgePendingSignup();
        applySignedOutState(toAuthViewError(signInError), sessionVersion);
      } finally {
        if (
          isMountedRef.current &&
          actionControllerRef.current === requestController
        ) {
          setIsSigningIn(false);
        }
      }
    },
    [
      applyAuthenticatedState,
      applySignedOutState,
      isCurrentSessionVersion,
      prepareIdentityChange,
      purgePendingSignup,
      replaceActionController,
      replaceSessionWorkController,
      resolvedController,
      supersedeActiveSession,
    ],
  );

  const signOut = useCallback(async (): Promise<void> => {
    const signedOutSession = sessionRef.current;
    const sessionVersion = supersedeActiveSession();
    setIsSigningIn(false);
    setIsSigningOut(true);
    setError(null);
    const requestController = replaceActionController();

    try {
      await runSessionCleanup(signedOutSession, requestController.signal);
      if (requestController.signal.aborted) {
        return;
      }

      applySignedOutState(null, sessionVersion);
    } catch (signOutError) {
      if (isAbortError(signOutError) || requestController.signal.aborted) {
        return;
      }

      if (isMountedRef.current && isCurrentSessionVersion(sessionVersion)) {
        replaceSessionWorkController();
        setError(toAuthViewError(signOutError));
      }
    } finally {
      if (
        isMountedRef.current &&
        actionControllerRef.current === requestController
      ) {
        setIsSigningOut(false);
      }
    }
  }, [
    applySignedOutState,
    isCurrentSessionVersion,
    replaceActionController,
    replaceSessionWorkController,
    runSessionCleanup,
    supersedeActiveSession,
  ]);

  const runAuthenticatedRequest = useCallback(
    async <T,>(
      request: (accessToken: string) => Promise<T>,
      signal?: AbortSignal,
    ): Promise<T> => {
      const currentSession = sessionRef.current;
      const sessionVersion = sessionVersionRef.current;

      if (!currentSession || signal?.aborted) {
        throw createUnauthenticatedError();
      }

      try {
        return await runWithSignal(
          () => request(currentSession.accessToken),
          signal,
        );
      } catch (requestError) {
        if (
          signal?.aborted ||
          !(requestError instanceof ApiClientError) ||
          requestError.status !== 401
        ) {
          throw requestError;
        }
      }

      let canonicalSession = sessionRef.current;
      if (
        !canonicalSession ||
        signal?.aborted ||
        !isMountedRef.current ||
        !isCurrentSessionVersion(sessionVersion) ||
        !isSameIdentity(currentSession, canonicalSession)
      ) {
        throw createAbortError();
      }

      if (isSameSessionVersion(currentSession, canonicalSession)) {
        await awaitSessionSynchronization(
          canonicalSession,
          sessionVersion,
          signal,
        );
      }

      canonicalSession = sessionRef.current;
      if (
        !canonicalSession ||
        signal?.aborted ||
        !isMountedRef.current ||
        !isCurrentSessionVersion(sessionVersion) ||
        !isSameIdentity(currentSession, canonicalSession)
      ) {
        throw createAbortError();
      }

      try {
        return await runWithSignal(
          () => request(canonicalSession.accessToken),
          signal,
        );
      } catch (retryError) {
        if (
          !signal?.aborted &&
          retryError instanceof ApiClientError &&
          retryError.status === 401
        ) {
          await signOut();
        }
        throw retryError;
      }
    },
    [
      awaitSessionSynchronization,
      isCurrentSessionVersion,
      signOut,
    ],
  );

  const publicSession = useMemo<AuthenticatedActorSession | null>(
    () => (session ? { actor: session.actor } : null),
    [session],
  );

  const value = useMemo<AuthContextValue>(
    () => ({
      clearError,
      error,
      isSigningIn,
      isSigningOut,
      runAuthenticatedRequest,
      session: publicSession,
      signIn,
      signOut,
      status,
    }),
    [
      clearError,
      error,
      isSigningIn,
      isSigningOut,
      publicSession,
      runAuthenticatedRequest,
      signIn,
      signOut,
      status,
    ],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

function isSameIdentity(
  left: AuthSession | null,
  right: AuthSession | null,
): boolean {
  return (
    left !== null &&
    right !== null &&
    left.actor.organization.id === right.actor.organization.id &&
    left.actor.membership.id === right.actor.membership.id
  );
}

function isSameSessionVersion(left: AuthSession, right: AuthSession): boolean {
  return (
    left.accessToken === right.accessToken &&
    left.accessTokenExpiresAt === right.accessTokenExpiresAt &&
    left.refreshToken === right.refreshToken &&
    left.refreshTokenExpiresAt === right.refreshTokenExpiresAt &&
    left.installationId === right.installationId
  );
}

function awaitForWaiter<T>(
  promise: Promise<T>,
  signal?: AbortSignal,
): Promise<T> {
  if (!signal) {
    return promise;
  }

  if (signal.aborted) {
    return Promise.reject(createAbortError());
  }

  return new Promise<T>((resolve, reject) => {
    let isSettled = false;
    const settle = (continuation: () => void): void => {
      if (isSettled) {
        return;
      }

      isSettled = true;
      signal.removeEventListener("abort", onAbort);
      continuation();
    };
    const onAbort = (): void => {
      settle(() => reject(createAbortError()));
    };

    signal.addEventListener("abort", onAbort, { once: true });
    promise.then(
      (value) => settle(() => resolve(value)),
      (error: unknown) => settle(() => reject(error)),
    );
  });
}

function createUnauthenticatedError(): ApiClientError {
  return new ApiClientError({
    status: 401,
    message: "Authentication is required.",
  });
}

function createAbortError(): Error {
  const error = new Error("The authenticated request was superseded.");
  error.name = "AbortError";
  return error;
}
