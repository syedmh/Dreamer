import {
  AppState,
  Pressable,
  StyleSheet,
  Text,
  View,
} from "react-native";
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";

import {
  getMobileApi,
  type T14Api,
} from "../../core/api/auth-api";
import {
  publishFeatureRefresh,
  subscribeFeatureRefresh,
} from "../../core/api/feature-refresh-bus";
import { createRequestScope } from "../../core/api/operation-runtime";
import {
  getPendingSignupStorage,
  type PendingSignupStorage,
} from "../../core/security/pending-signup-storage";
import { authTheme } from "../auth/auth-theme";
import type { AuthContextValue } from "../auth/AuthSessionProvider";
import { useAuth } from "../auth/useAuth";
import { OperationGeneration } from "../dates/date-use-cases";
import {
  SignupSyncCoordinator,
  type SignupSyncResult,
} from "./signup-sync-coordinator";
import { loadAllMySignups } from "./useSignupSubmission";

export interface SignupRecoveryRuntimeProps {
  readonly api?: T14Api;
  readonly storage?: PendingSignupStorage;
}

interface AuthenticatedSignupRecoveryRuntimeProps {
  readonly api: T14Api;
  readonly identity: string;
  readonly organizationId: string;
  readonly primaryMembershipId: string;
  readonly runAuthenticatedRequest: AuthContextValue["runAuthenticatedRequest"];
  readonly storage: PendingSignupStorage;
}

export function SignupRecoveryRuntime({
  api = getMobileApi(),
  storage = getPendingSignupStorage(),
}: SignupRecoveryRuntimeProps) {
  const { runAuthenticatedRequest, session, status } = useAuth();
  const organizationId = session?.actor.organization.id ?? "";
  const primaryMembershipId = session?.actor.membership.id ?? "";
  const identity = `${organizationId}:${primaryMembershipId}`;

  if (
    status !== "authenticated" ||
    !organizationId ||
    !primaryMembershipId
  ) {
    return null;
  }

  return (
    <AuthenticatedSignupRecoveryRuntime
      api={api}
      identity={identity}
      key={identity}
      organizationId={organizationId}
      primaryMembershipId={primaryMembershipId}
      runAuthenticatedRequest={runAuthenticatedRequest}
      storage={storage}
    />
  );
}

function AuthenticatedSignupRecoveryRuntime({
  api,
  identity,
  organizationId,
  primaryMembershipId,
  runAuthenticatedRequest,
  storage,
}: AuthenticatedSignupRecoveryRuntimeProps) {
  const generation = useMemo(
    () => new OperationGeneration(identity),
    [identity],
  );
  const activeController = useRef<AbortController | null>(null);
  const [terminalMessage, setTerminalMessage] = useState<string | null>(null);
  const coordinator = useMemo(
    () =>
      new SignupSyncCoordinator({
        storage,
        send: (command, signal) =>
          runAuthenticatedRequest(
            (accessToken) =>
              api.submitSignup(
                accessToken,
                command.helpNeedId,
                command.idempotencyKey,
                command.body,
                signal,
              ),
            signal,
          ),
        reconcile: async (command, signal) => {
          const signups = await runAuthenticatedRequest(
            (accessToken) =>
              loadAllMySignups(api, accessToken, signal),
            signal,
          );
          return (
            signups.find(
              (signup) =>
                signup.helpNeedId === command.helpNeedId &&
                signup.serviceDateId === command.serviceDateId &&
                signup.primaryContact.membershipId ===
                  command.primaryMembershipId,
            ) ?? null
          );
        },
      }),
    [api, runAuthenticatedRequest, storage],
  );

  const recover = useCallback(async (): Promise<void> => {
    activeController.current?.abort();
    const controller = new AbortController();
    activeController.current = controller;
    const currentGeneration = generation.begin();
    const scope = createRequestScope(controller.signal);

    try {
      const result = await coordinator.retry(
        organizationId,
        primaryMembershipId,
        {
          operationSignal: controller.signal,
          requestSignal: scope.signal,
        },
      );
      if (
        result &&
        result.type !== "pendingSync" &&
        generation.isCurrent(currentGeneration) &&
        !controller.signal.aborted
      ) {
        if (isTerminalRecoveryResult(result)) {
          setTerminalMessage(result.message);
        }
        publishFeatureRefresh("signups-changed");
      }
    } catch {
      // Recovery is best-effort. Auth/session handling owns visible failures.
    } finally {
      scope.dispose();
    }
  }, [
    coordinator,
    generation,
    organizationId,
    primaryMembershipId,
  ]);

  useEffect(() => {
    let active = true;
    void Promise.resolve().then(() => {
      if (active) {
        return recover();
      }
      return undefined;
    });
    const appStateSubscription = AppState.addEventListener(
      "change",
      (nextState) => {
        if (nextState === "active") {
          void recover();
        }
      },
    );
    const unsubscribeRefresh = subscribeFeatureRefresh((event) => {
      if (event === "session-cleared") {
        activeController.current?.abort();
        generation.supersede();
        setTerminalMessage(null);
      }
    });

    return () => {
      active = false;
      unsubscribeRefresh();
      appStateSubscription.remove();
      activeController.current?.abort();
      generation.supersede();
    };
  }, [generation, recover]);

  if (!terminalMessage) {
    return null;
  }

  return (
    <View
      accessibilityLiveRegion="assertive"
      accessibilityRole="alert"
      style={styles.notice}
    >
      <Text style={styles.title}>Signup update</Text>
      <Text style={styles.message}>{terminalMessage}</Text>
      <Pressable
        accessibilityLabel="Dismiss signup recovery message"
        accessibilityRole="button"
        onPress={() => setTerminalMessage(null)}
        style={styles.dismissButton}
      >
        <Text style={styles.dismissText}>Dismiss</Text>
      </Pressable>
    </View>
  );
}

function isTerminalRecoveryResult(
  result: SignupSyncResult,
): result is Extract<
  SignupSyncResult,
  { type: "expired" | "permanentError" }
> {
  return result.type === "expired" || result.type === "permanentError";
}

const styles = StyleSheet.create({
  dismissButton: {
    alignItems: "center",
    alignSelf: "flex-start",
    borderColor: authTheme.errorBorder,
    borderRadius: 12,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: 44,
    paddingHorizontal: 16,
    paddingVertical: 10,
  },
  dismissText: {
    color: authTheme.errorText,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 21,
  },
  message: {
    color: authTheme.errorText,
    fontSize: 15,
    lineHeight: 22,
  },
  notice: {
    backgroundColor: authTheme.errorBackground,
    borderBottomColor: authTheme.errorBorder,
    borderBottomWidth: 1,
    gap: 8,
    paddingHorizontal: 20,
    paddingVertical: 12,
  },
  title: {
    color: authTheme.errorText,
    fontSize: 17,
    fontWeight: "800",
    lineHeight: 24,
  },
});
