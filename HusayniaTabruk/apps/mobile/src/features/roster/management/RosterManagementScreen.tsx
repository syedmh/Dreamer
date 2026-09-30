import { useFocusEffect, useLocalSearchParams } from "expo-router";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import {
  getMobileApi,
  type ServiceDateResponse,
  type SignupResponse,
  type T18Api,
} from "../../../core/api/auth-api";
import {
  publishFeatureRefresh,
  subscribeFeatureRefresh,
} from "../../../core/api/feature-refresh-bus";
import { createRequestScope } from "../../../core/api/operation-runtime";
import {
  IdempotentCommandStore,
  type PendingIdempotentCommand,
  toManagementError,
} from "../../admin/coordination-management";
import { authTheme } from "../../auth/auth-theme";
import { useAuth } from "../../auth/useAuth";
import { formatCategory } from "../../dates/DateDetailScreen";
import { formatStatus } from "../../signups/SignupPresentation";
import { SignupCancellationPanel } from "../../signups/management/SignupCancellationPanel";
import { loadManagedRoster } from "./roster-management-use-cases";

type Decision = "approve" | "decline" | "waitlist";

interface RosterIdempotentCommand {
  readonly expectedVersion: number;
  readonly helpNeedId: string;
  readonly reason: string | null;
  readonly signupId: string;
  readonly targetStatus: SignupResponse["status"];
  readonly type:
    | Decision
    | "override"
    | "reassign"
    | "withdraw";
}

export interface RosterManagementViewProps {
  readonly actionReason: string;
  readonly busyAction: string | null;
  readonly currentMembershipId?: string;
  readonly date: ServiceDateResponse;
  readonly error: string | null;
  readonly onActionReasonChange: (value: string) => void;
  readonly onDecision: (signup: SignupResponse, decision: Decision) => void;
  readonly onOverride: (signup: SignupResponse) => void;
  readonly onReassign: (signup: SignupResponse) => void;
  readonly onRefresh: () => void;
  readonly onWithdraw?: (signup: SignupResponse) => void;
  readonly now?: Date;
  readonly pendingOutcome: string | null;
  readonly signups: readonly SignupResponse[];
}

export function RosterManagementScreen({
  api = getMobileApi(),
}: {
  readonly api?: T18Api;
} = {}) {
  const params = useLocalSearchParams<{ dateId?: string | string[] }>();
  const dateId = firstParam(params.dateId);
  const { session } = useAuth();
  const state = useRosterManagement(dateId, api);

  if (state.isLoading && !state.date) {
    return <CenteredMessage message="Loading managed roster..." />;
  }
  if (!state.date) {
    return (
      <CenteredMessage
        actionLabel="Refresh managed roster"
        message={state.error ?? "The managed roster is unavailable."}
        onAction={() => {
          void state.refresh();
        }}
      />
    );
  }
  if (state.date.managerMembershipId !== session?.actor.membership.id) {
    return (
      <CenteredMessage message="Only the active managing Food Incharge can manage this roster." />
    );
  }

  return (
    <RosterManagementView
      actionReason={state.actionReason}
      busyAction={state.busyAction}
      currentMembershipId={session.actor.membership.id}
      date={state.date}
      error={state.error}
      onActionReasonChange={state.setActionReason}
      onDecision={(signup, decision) => {
        void state.decide(signup, decision);
      }}
      onOverride={(signup) => {
        void state.override(signup);
      }}
      onReassign={(signup) => {
        void state.reassign(signup);
      }}
      onRefresh={() => {
        void state.refresh();
      }}
      onWithdraw={(signup) => {
        void state.withdraw(signup);
      }}
      pendingOutcome={state.pendingOutcome}
      signups={state.signups}
    />
  );
}

export function RosterManagementView({
  actionReason,
  busyAction,
  currentMembershipId = "",
  date,
  error,
  onActionReasonChange,
  onDecision,
  onOverride,
  onReassign,
  onRefresh,
  onWithdraw,
  now = new Date(),
  pendingOutcome,
  signups,
}: RosterManagementViewProps) {
  const [confirmation, setConfirmation] = useState<{
    action: Decision | "override" | "reassign";
    signup: SignupResponse;
  } | null>(null);
  const busy = busyAction !== null;
  const deadlineHasPassed =
    now.getTime() >= new Date(date.cancellationDeadlineAt).getTime();

  const confirm = () => {
    if (!confirmation) {
      return;
    }
    if (
      confirmation.action === "approve" ||
      confirmation.action === "decline" ||
      confirmation.action === "waitlist"
    ) {
      onDecision(confirmation.signup, confirmation.action);
    } else if (confirmation.action === "override") {
      onOverride(confirmation.signup);
    } else {
      onReassign(confirmation.signup);
    }
    setConfirmation(null);
  };

  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView
        contentContainerStyle={styles.container}
        contentInsetAdjustmentBehavior="automatic"
      >
        <Text accessibilityRole="header" style={styles.title}>
          Manage roster
        </Text>
        <Text style={styles.subtitle}>{date.title}</Text>
        <Text style={styles.deadline}>
          Self-cancellation deadline: {date.cancellationDeadlineAt}
        </Text>
        <Text style={styles.help}>
          Reasons are included with the selected action. A decline and a
          deadline override require a reason.
        </Text>
        <TextInput
          accessibilityLabel="Decision, override, or reassignment reason"
          multiline
          onChangeText={onActionReasonChange}
          style={styles.input}
          value={actionReason}
        />
        {error ? (
          <Text accessibilityLiveRegion="assertive" style={styles.error}>
            {error}
          </Text>
        ) : null}
        {pendingOutcome ? (
          <Text accessibilityLiveRegion="assertive" style={styles.pending}>
            Pending confirmation: {pendingOutcome}
          </Text>
        ) : null}
        <ManagementButton
          disabled={busy}
          label="Refresh managed roster"
          onPress={onRefresh}
          secondary
        />

        {!busy && !error && signups.length === 0 ? (
          <Text style={styles.help}>No signup requests for this date</Text>
        ) : null}

        {signups.map((signup) => (
          <View
            accessibilityLabel={`Signup for ${formatCategory(signup.category)}, ${formatStatus(signup.status)}`}
            key={signup.id}
            style={styles.card}
          >
            <Text accessibilityRole="header" style={styles.cardTitle}>
              {formatCategory(signup.category)}
            </Text>
            <Text style={styles.status}>
              Status: {formatStatus(signup.status)}
            </Text>
            <Text style={styles.body}>
              Primary contact: {signup.primaryContact.displayName}
            </Text>
            <Text style={styles.body}>
              Composition: {formatWord(signup.kind)}
            </Text>
            {signup.label ? (
              <Text style={styles.body}>Generic label: {signup.label}</Text>
            ) : null}
            <Text style={styles.body}>
              Total participants: {signup.totalParticipantCount}
            </Text>
            {signup.memberParticipants.map((participant) => (
              <Text key={participant.membershipId} style={styles.body}>
                Eligible member: {participant.displayName}
              </Text>
            ))}
            <Text style={styles.body}>
              Unnamed participants: {signup.unnamedParticipantCount}
            </Text>
            {signup.status === "waitlisted" ? (
              <Text style={styles.status}>
                Waitlist position: {signup.waitlistOrder}
              </Text>
            ) : null}

            {signup.status === "pending" ? (
              <View style={styles.actions}>
                {(["approve", "decline", "waitlist"] as const).map(
                  (decision) => (
                    <ManagementButton
                      disabled={
                        busy ||
                        (decision === "decline" &&
                          actionReason.trim().length === 0)
                      }
                      key={decision}
                      label={`${formatWord(decision)} signup for ${signup.primaryContact.displayName}`}
                      onPress={() => setConfirmation({ action: decision, signup })}
                      secondary={decision !== "approve"}
                    />
                  ),
                )}
              </View>
            ) : null}

            {signup.status === "waitlisted" ? (
              <ManagementButton
                disabled={busy}
                label={`Reassign place to ${signup.primaryContact.displayName}`}
                onPress={() =>
                  setConfirmation({ action: "reassign", signup })
                }
              />
            ) : null}

            {signup.status === "approved" ? (
              <>
                {deadlineHasPassed ? (
                  <>
                    <Text style={styles.help}>
                      Override reason is required and will be recorded.
                    </Text>
                    <ManagementButton
                      busy={busyAction === `override:${signup.id}`}
                      destructive
                      disabled={busy || actionReason.trim().length === 0}
                      label={`Override cancellation deadline for ${signup.primaryContact.displayName}`}
                      onPress={() =>
                        setConfirmation({ action: "override", signup })
                      }
                    />
                  </>
                ) : (
                  <Text style={styles.help}>
                    The primary contact can self-cancel before the deadline.
                  </Text>
                )}
                {onWithdraw ? (
                  <SignupCancellationPanel
                    busy={busy}
                    cancellationDeadlineAt={date.cancellationDeadlineAt}
                    currentMembershipId={currentMembershipId}
                    error={error}
                    onWithdraw={() => onWithdraw(signup)}
                    pendingOutcome={pendingOutcome}
                    signup={signup}
                  />
                ) : null}
              </>
            ) : null}
          </View>
        ))}

        {confirmation ? (
          <View
            accessibilityLabel={`Confirm ${confirmation.action} signup action`}
            accessibilityViewIsModal
            style={styles.dialog}
          >
            <Text accessibilityRole="header" style={styles.dialogTitle}>
              Confirm {confirmation.action}
            </Text>
            <Text style={styles.body}>
              Apply this action to{" "}
              {confirmation.signup.primaryContact.displayName}?
            </Text>
            <ManagementButton
              busy={busy}
              destructive={confirmation.action === "override"}
              disabled={busy}
              label={`Confirm ${confirmation.action} for ${confirmation.signup.primaryContact.displayName}`}
              onPress={confirm}
            />
            <ManagementButton
              disabled={busy}
              label="Go back without changing the signup"
              onPress={() => setConfirmation(null)}
              secondary
            />
          </View>
        ) : null}
      </ScrollView>
    </SafeAreaView>
  );
}

export function useRosterManagement(
  dateId: string,
  api: T18Api = getMobileApi(),
) {
  const { runAuthenticatedRequest } = useAuth();
  const [date, setDate] = useState<ServiceDateResponse | null>(null);
  const [signups, setSignups] = useState<readonly SignupResponse[]>([]);
  const [actionReason, setActionReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [pendingOutcome, setPendingOutcome] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const busyRef = useRef(false);
  const controller = useRef<AbortController | null>(null);
  const commandStore = useRef(
    new IdempotentCommandStore<RosterIdempotentCommand>(),
  );

  const refresh = useCallback(async () => {
    controller.current?.abort();
    const nextController = new AbortController();
    controller.current = nextController;
    const scope = createRequestScope(nextController.signal);
    setIsLoading(true);
    setError(null);

    try {
      const [nextDate, nextSignups] = await runAuthenticatedRequest(
        (accessToken) =>
          Promise.all([
            api.getServiceDate(accessToken, dateId, scope.signal),
            loadManagedRoster(
              api,
              accessToken,
              dateId,
              scope.signal,
            ),
          ]),
        scope.signal,
      );
      if (!nextController.signal.aborted) {
        setDate(nextDate);
        setSignups(nextSignups);
        setPendingOutcome(null);
        commandStore.current.confirmMatching((command) =>
          nextSignups.some(
            (signup) =>
              signup.id === command.signupId &&
              signup.status === command.targetStatus,
          ),
        );
      }
    } catch (loadError) {
      if (!nextController.signal.aborted) {
        setError(toManagementError(loadError, "managed roster").message);
      }
    } finally {
      scope.dispose();
      if (!nextController.signal.aborted) {
        setIsLoading(false);
      }
    }
  }, [api, dateId, runAuthenticatedRequest]);

  const mutate = useCallback(
    async (
      action: string,
      command: PendingIdempotentCommand<RosterIdempotentCommand>,
      operation: (
        accessToken: string,
        key: string,
        signal: AbortSignal,
      ) => Promise<SignupResponse>,
    ) => {
      if (busyRef.current) {
        return;
      }
      busyRef.current = true;
      const scope = createRequestScope();
      setBusyAction(action);
      setError(null);
      setPendingOutcome(null);
      try {
        const updated = await runAuthenticatedRequest(
          (accessToken) =>
            operation(
              accessToken,
              command.key,
              scope.signal,
            ),
          scope.signal,
        );
        setSignups((current) =>
          current.map((item) => (item.id === updated.id ? updated : item)),
        );
        commandStore.current.confirm(action);
        setActionReason("");
        publishFeatureRefresh("roster-changed");
        publishFeatureRefresh("signups-changed");
      } catch (mutationError) {
        const viewError = toManagementError(mutationError, "signup");
        if (viewError.kind === "unknown") {
          setPendingOutcome(viewError.message);
        } else {
          commandStore.current.confirm(action);
          setError(viewError.message);
        }
      } finally {
        scope.dispose();
        busyRef.current = false;
        setBusyAction(null);
      }
    },
    [runAuthenticatedRequest],
  );

  const decide = useCallback(
    async (signup: SignupResponse, decision: Decision) => {
      const reason = actionReason.trim();
      if (decision === "decline" && !reason) {
        setError("A decline reason is required.");
        return;
      }
      await mutate(
        `${decision}:${signup.id}`,
        commandStore.current.get(`${decision}:${signup.id}`, () => ({
          expectedVersion: signup.signupVersion,
          helpNeedId: signup.helpNeedId,
          reason: reason || null,
          signupId: signup.id,
          targetStatus:
            decision === "approve"
              ? "approved"
              : decision === "decline"
                ? "declined"
                : "waitlisted",
          type: decision,
        })),
        (accessToken, key, signal) => {
          const command = commandStore.current.get(
            `${decision}:${signup.id}`,
            () => {
              throw new Error("The decision command was not prepared.");
            },
          ).request;
          if (decision === "approve") {
            return api.approveSignup(
              accessToken,
              command.signupId,
              key,
              command.expectedVersion,
              { reason: command.reason },
              signal,
            );
          }
          if (decision === "decline") {
            return api.declineSignup(
              accessToken,
              command.signupId,
              key,
              command.expectedVersion,
              { reason: command.reason ?? "" },
              signal,
            );
          }
          return api.waitlistSignup(
            accessToken,
            command.signupId,
            key,
            command.expectedVersion,
            { reason: command.reason },
            signal,
          );
        },
      );
    },
    [actionReason, api, mutate],
  );

  const override = useCallback(
    async (signup: SignupResponse) => {
      const reason = actionReason.trim();
      if (!reason) {
        setError("An override reason is required.");
        return;
      }
      await mutate(
        `override:${signup.id}`,
        commandStore.current.get(`override:${signup.id}`, () => ({
          expectedVersion: signup.signupVersion,
          helpNeedId: signup.helpNeedId,
          reason,
          signupId: signup.id,
          targetStatus: "cancelled",
          type: "override",
        })),
        (accessToken, key, signal) =>
          {
            const command = commandStore.current.get(
              `override:${signup.id}`,
              () => {
                throw new Error("The override command was not prepared.");
              },
            ).request;
            return api.overrideSignupCancellation(
              accessToken,
              command.signupId,
              key,
              command.expectedVersion,
              {
                reason: command.reason ?? "",
                targetState: "cancelled",
              },
              signal,
            );
          },
      );
    },
    [actionReason, api, mutate],
  );

  const reassign = useCallback(
    async (signup: SignupResponse) => {
      const reason = actionReason.trim();
      await mutate(
        `reassign:${signup.id}`,
        commandStore.current.get(`reassign:${signup.id}`, () => ({
          expectedVersion: signup.signupVersion,
          helpNeedId: signup.helpNeedId,
          reason: reason || null,
          signupId: signup.id,
          targetStatus: "approved",
          type: "reassign",
        })),
        (accessToken, key, signal) =>
          {
            const command = commandStore.current.get(
              `reassign:${signup.id}`,
              () => {
                throw new Error("The reassignment command was not prepared.");
              },
            ).request;
            return api.reassignWaitlistedSignup(
              accessToken,
              command.helpNeedId,
              key,
              command.expectedVersion,
              {
                reason: command.reason,
                signupId: command.signupId,
              },
              signal,
            );
          },
      );
    },
    [actionReason, api, mutate],
  );

  const withdraw = useCallback(
    async (signup: SignupResponse) => {
      await mutate(
        `withdraw:${signup.id}`,
        commandStore.current.get(`withdraw:${signup.id}`, () => ({
          expectedVersion: signup.signupVersion,
          helpNeedId: signup.helpNeedId,
          reason: null,
          signupId: signup.id,
          targetStatus: "withdrawn",
          type: "withdraw",
        })),
        (accessToken, key, signal) =>
          {
            const command = commandStore.current.get(
              `withdraw:${signup.id}`,
              () => {
                throw new Error("The withdrawal command was not prepared.");
              },
            ).request;
            return api.withdrawSignup(
              accessToken,
              command.signupId,
              key,
              command.expectedVersion,
              {},
              signal,
            );
          },
      );
    },
    [api, mutate],
  );

  useFocusEffect(
    useCallback(() => {
      void refresh();
      return () => controller.current?.abort();
    }, [refresh]),
  );

  useEffect(
    () =>
      subscribeFeatureRefresh((event) => {
        if (event === "roster-changed" || event === "dates-changed") {
          void refresh();
        } else if (event === "session-cleared") {
          controller.current?.abort();
          setDate(null);
          setSignups([]);
          commandStore.current.clear();
        }
      }),
    [refresh],
  );

  return {
    actionReason,
    busyAction,
    date,
    decide,
    error,
    isLoading,
    override,
    pendingOutcome,
    reassign,
    refresh,
    setActionReason,
    signups,
    withdraw,
  };
}

function ManagementButton({
  busy = false,
  destructive = false,
  disabled,
  label,
  onPress,
  secondary = false,
}: {
  readonly busy?: boolean;
  readonly destructive?: boolean;
  readonly disabled: boolean;
  readonly label: string;
  readonly onPress: () => void;
  readonly secondary?: boolean;
}) {
  return (
    <Pressable
      accessibilityLabel={label}
      accessibilityRole="button"
      accessibilityState={{ busy, disabled }}
      disabled={disabled}
      onPress={onPress}
      style={[
        styles.button,
        secondary ? styles.secondaryButton : null,
        destructive ? styles.destructiveButton : null,
        disabled ? styles.disabled : null,
      ]}
    >
      <Text
        style={[
          styles.buttonText,
          secondary ? styles.secondaryText : null,
        ]}
      >
        {label}
      </Text>
    </Pressable>
  );
}

function CenteredMessage({
  actionLabel,
  message,
  onAction,
}: {
  readonly actionLabel?: string;
  readonly message: string;
  readonly onAction?: () => void;
}) {
  return (
    <SafeAreaView style={styles.safeArea}>
      <View style={styles.centered}>
        <Text accessibilityLiveRegion="polite" style={styles.body}>
          {message}
        </Text>
        {actionLabel && onAction ? (
          <ManagementButton
            disabled={false}
            label={actionLabel}
            onPress={onAction}
          />
        ) : null}
      </View>
    </SafeAreaView>
  );
}

function firstParam(value: string | string[] | undefined): string {
  return Array.isArray(value) ? value[0] ?? "" : value ?? "";
}

function formatWord(value: string): string {
  return `${value.charAt(0).toUpperCase()}${value.slice(1)}`;
}

const styles = StyleSheet.create({
  actions: {
    gap: 9,
  },
  body: {
    color: authTheme.primaryText,
    fontSize: 15,
    lineHeight: 22,
  },
  button: {
    alignItems: "center",
    backgroundColor: authTheme.accent,
    borderColor: authTheme.accent,
    borderRadius: 12,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: 46,
    paddingHorizontal: 14,
    paddingVertical: 11,
  },
  buttonText: {
    color: authTheme.primaryTextOnAccent,
    fontSize: 15,
    fontWeight: "800",
    textAlign: "center",
  },
  card: {
    backgroundColor: authTheme.panel,
    borderColor: authTheme.border,
    borderRadius: 18,
    borderWidth: 1,
    gap: 9,
    padding: 17,
  },
  cardTitle: {
    color: authTheme.primaryText,
    fontSize: 19,
    fontWeight: "800",
  },
  centered: {
    alignItems: "center",
    flex: 1,
    gap: 16,
    justifyContent: "center",
    padding: 24,
  },
  container: {
    gap: 16,
    padding: 20,
  },
  deadline: {
    color: authTheme.primaryText,
    fontSize: 16,
    fontWeight: "700",
    lineHeight: 23,
  },
  destructiveButton: {
    backgroundColor: authTheme.errorText,
    borderColor: authTheme.errorText,
  },
  dialog: {
    backgroundColor: authTheme.panelAlt,
    borderColor: authTheme.accent,
    borderRadius: 18,
    borderWidth: 2,
    gap: 12,
    padding: 16,
  },
  dialogTitle: {
    color: authTheme.accent,
    fontSize: 20,
    fontWeight: "800",
  },
  disabled: {
    opacity: 0.55,
  },
  error: {
    backgroundColor: authTheme.errorBackground,
    borderColor: authTheme.errorBorder,
    borderRadius: 12,
    borderWidth: 1,
    color: authTheme.errorText,
    fontSize: 16,
    lineHeight: 23,
    padding: 13,
  },
  help: {
    color: authTheme.mutedText,
    fontSize: 15,
    lineHeight: 22,
  },
  input: {
    backgroundColor: authTheme.panel,
    borderColor: authTheme.border,
    borderRadius: 12,
    borderWidth: 1,
    color: authTheme.primaryText,
    fontSize: 16,
    minHeight: 88,
    padding: 12,
    textAlignVertical: "top",
  },
  pending: {
    backgroundColor: authTheme.panelAlt,
    borderColor: authTheme.accent,
    borderRadius: 12,
    borderWidth: 1,
    color: authTheme.primaryText,
    fontSize: 16,
    lineHeight: 23,
    padding: 13,
  },
  safeArea: {
    backgroundColor: authTheme.background,
    flex: 1,
  },
  secondaryButton: {
    backgroundColor: authTheme.panel,
  },
  secondaryText: {
    color: authTheme.accent,
  },
  status: {
    color: authTheme.accent,
    fontSize: 16,
    fontWeight: "800",
  },
  subtitle: {
    color: authTheme.primaryText,
    fontSize: 20,
    fontWeight: "700",
  },
  title: {
    color: authTheme.accent,
    fontSize: 28,
    fontWeight: "800",
  },
});
