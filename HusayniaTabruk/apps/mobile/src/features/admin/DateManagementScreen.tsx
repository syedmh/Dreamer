import { Link, useFocusEffect, useLocalSearchParams } from "expo-router";
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
  type HelpNeedResponse,
  type PatchHelpNeedRequest,
  type PatchServiceDateRequest,
  type ServiceDateResponse,
  type T18Api,
} from "../../core/api/auth-api";
import {
  publishFeatureRefresh,
  subscribeFeatureRefresh,
} from "../../core/api/feature-refresh-bus";
import { createRequestScope } from "../../core/api/operation-runtime";
import { authTheme } from "../auth/auth-theme";
import { useAuth } from "../auth/useAuth";
import { formatCategory } from "../dates/DateDetailScreen";
import {
  IdempotentCommandStore,
  toManagementError,
} from "./coordination-management";

export type DateManagementConfirmation = "cancel" | "close";

export type DateManagementValues = PatchServiceDateRequest;

export interface DateManagementViewProps {
  readonly confirmation: DateManagementConfirmation | null;
  readonly date: ServiceDateResponse;
  readonly error: string | null;
  readonly isBusy: boolean;
  readonly onChange: (
    field: keyof DateManagementValues,
    value: string,
  ) => void;
  readonly onConfirm: (reason: string) => void;
  readonly onEditNeed: (
    need: HelpNeedResponse,
    request: PatchHelpNeedRequest,
  ) => void;
  readonly onPublish: () => void;
  readonly onRefresh: () => void;
  readonly onRequestConfirmation: (
    value: DateManagementConfirmation | null,
  ) => void;
  readonly onSave: () => void;
  readonly pendingOutcome: string | null;
  readonly values: DateManagementValues;
}

type DateIdempotentCommand =
  | {
      readonly kind: "publish";
      readonly dateId: string;
    }
  | {
      readonly dateId: string;
      readonly expectedVersion: number;
      readonly kind: "cancel" | "close";
      readonly reason: string | null;
    };

export function DateManagementScreen({
  api = getMobileApi(),
}: {
  readonly api?: T18Api;
} = {}) {
  const params = useLocalSearchParams<{ dateId?: string | string[] }>();
  const dateId = firstParam(params.dateId);
  const { session } = useAuth();
  const state = useDateManagement(dateId, api);

  if (state.isLoading && !state.date) {
    return <ManagementMessage message="Loading service date management..." />;
  }

  if (!state.date) {
    return (
      <ManagementMessage
        actionLabel="Refresh service date status"
        message={state.error ?? "The service date is unavailable."}
        onAction={() => {
          void state.refresh();
        }}
      />
    );
  }

  if (state.date.managerMembershipId !== session?.actor.membership.id) {
    return (
      <ManagementMessage message="Only the active managing Food Incharge can manage this service date." />
    );
  }

  return (
    <DateManagementView
      confirmation={state.confirmation}
      date={state.date}
      error={state.error}
      isBusy={state.busyAction !== null}
      onChange={state.setValue}
      onConfirm={(reason) => {
        void state.confirm(reason);
      }}
      onEditNeed={(need, request) => {
        void state.editNeed(need, request);
      }}
      onPublish={() => {
        void state.publish();
      }}
      onRefresh={() => {
        void state.refresh();
      }}
      onRequestConfirmation={state.setConfirmation}
      onSave={() => {
        void state.save();
      }}
      pendingOutcome={state.pendingOutcome}
      values={state.values}
    />
  );
}

export function DateManagementView({
  confirmation,
  date,
  error,
  isBusy,
  onChange,
  onConfirm,
  onEditNeed,
  onPublish,
  onRefresh,
  onRequestConfirmation,
  onSave,
  pendingOutcome,
  values,
}: DateManagementViewProps) {
  const [reason, setReason] = useState("");

  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView
        contentContainerStyle={styles.container}
        contentInsetAdjustmentBehavior="automatic"
      >
        <Text accessibilityRole="header" style={styles.title}>
          Manage service date
        </Text>
        <Text style={styles.status}>Date status: {formatWord(date.status)}</Text>
        <Text style={styles.help}>
          Times must be complete ISO-8601 UTC values ending in Z. The
          cancellation deadline controls member self-cancellation.
        </Text>

        <LabeledInput
          label="Title"
          onChangeText={(value) => onChange("title", value)}
          value={values.title}
        />
        <LabeledInput
          label="Instructions"
          multiline
          onChangeText={(value) => onChange("instructions", value)}
          value={values.instructions}
        />
        <LabeledInput
          label="Starts at in UTC"
          onChangeText={(value) => onChange("startsAt", value)}
          value={values.startsAt}
        />
        <LabeledInput
          label="Ends at in UTC"
          onChangeText={(value) => onChange("endsAt", value)}
          value={values.endsAt}
        />
        <LabeledInput
          label="Cancellation deadline in UTC"
          onChangeText={(value) =>
            onChange("cancellationDeadlineAt", value)
          }
          value={values.cancellationDeadlineAt}
        />

        {error ? <ErrorCard message={error} /> : null}
        {pendingOutcome ? <PendingCard message={pendingOutcome} /> : null}

        <ActionButton
          disabled={isBusy}
          label="Save date changes"
          onPress={onSave}
        />
        {date.status === "draft" ? (
          <ActionButton
            disabled={isBusy}
            label="Publish service date"
            onPress={onPublish}
          />
        ) : null}
        <ActionButton
          disabled={isBusy}
          label="Refresh service date status"
          onPress={onRefresh}
          secondary
        />
        {date.status !== "closed" && date.status !== "cancelled" ? (
          <>
            <ActionButton
              disabled={isBusy}
              label="Close service date"
              onPress={() => onRequestConfirmation("close")}
              secondary
            />
            <ActionButton
              disabled={isBusy}
              label="Cancel service date"
              onPress={() => onRequestConfirmation("cancel")}
              destructive
            />
          </>
        ) : null}

        <Link
          accessibilityLabel={`Manage roster for ${date.title}`}
          accessibilityRole="link"
          href={{
            pathname: "/(incharge)/manage/dates/[dateId]/roster",
            params: { dateId: date.id },
          }}
          style={styles.link}
        >
          Manage roster and signup decisions
        </Link>

        <Text accessibilityRole="header" style={styles.sectionTitle}>
          Help needs
        </Text>
        {date.helpNeeds.map((need) => (
          <HelpNeedEditor
            disabled={isBusy}
            key={`${need.id}:${need.version}`}
            need={need}
            onSave={(request) => onEditNeed(need, request)}
          />
        ))}

        {confirmation ? (
          <View
            accessibilityLabel={`${formatWord(confirmation)} service date confirmation`}
            accessibilityViewIsModal
            style={styles.dialog}
          >
            <Text accessibilityRole="header" style={styles.dialogTitle}>
              Confirm {confirmation}
            </Text>
            <Text style={styles.body}>
              {confirmation === "cancel"
                ? "Canceling visibly cancels affected signups and prevents new requests."
                : "Closing prevents new requests while preserving roster history."}
            </Text>
            <LabeledInput
              label={`${formatWord(confirmation)} reason (optional)`}
              multiline
              onChangeText={setReason}
              value={reason}
            />
            <ActionButton
              busy={isBusy}
              disabled={isBusy}
              label={`Confirm ${confirmation} service date`}
              onPress={() => onConfirm(reason)}
              destructive={confirmation === "cancel"}
            />
            <ActionButton
              disabled={isBusy}
              label={`Keep service date ${formatWord(date.status).toLowerCase()}`}
              onPress={() => {
                setReason("");
                onRequestConfirmation(null);
              }}
              secondary
            />
          </View>
        ) : null}
      </ScrollView>
    </SafeAreaView>
  );
}

export function useDateManagement(
  dateId: string,
  api: T18Api = getMobileApi(),
) {
  const { runAuthenticatedRequest } = useAuth();
  const [date, setDate] = useState<ServiceDateResponse | null>(null);
  const [values, setValues] = useState<DateManagementValues>(emptyValues);
  const [error, setError] = useState<string | null>(null);
  const [pendingOutcome, setPendingOutcome] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [confirmation, setConfirmation] =
    useState<DateManagementConfirmation | null>(null);
  const busyRef = useRef(false);
  const controller = useRef<AbortController | null>(null);
  const commandStore = useRef(
    new IdempotentCommandStore<DateIdempotentCommand>(),
  );

  const applyDate = useCallback((nextDate: ServiceDateResponse) => {
    setDate(nextDate);
    setValues(toValues(nextDate));
  }, []);

  const refresh = useCallback(async () => {
    controller.current?.abort();
    const nextController = new AbortController();
    controller.current = nextController;
    const scope = createRequestScope(nextController.signal);
    setIsLoading(true);
    setError(null);

    try {
      const nextDate = await runAuthenticatedRequest(
        (accessToken) =>
          api.getServiceDate(accessToken, dateId, scope.signal),
        scope.signal,
      );
      if (!nextController.signal.aborted) {
        applyDate(nextDate);
        setPendingOutcome(null);
        commandStore.current.confirmMatching(
          (command) =>
            (command.kind === "publish" && nextDate.status === "open") ||
            (command.kind === "close" && nextDate.status === "closed") ||
            (command.kind === "cancel" && nextDate.status === "cancelled"),
        );
      }
    } catch (loadError) {
      if (!nextController.signal.aborted) {
        setError(toManagementError(loadError, "service date").message);
      }
    } finally {
      scope.dispose();
      if (!nextController.signal.aborted) {
        setIsLoading(false);
      }
    }
  }, [api, applyDate, dateId, runAuthenticatedRequest]);

  const runMutation = useCallback(
    async <T,>(
      action: string,
      resourceLabel: string,
      operation: (accessToken: string, signal: AbortSignal) => Promise<T>,
      apply: (result: T) => void,
    ): Promise<void> => {
      if (busyRef.current) {
        return;
      }

      busyRef.current = true;
      const scope = createRequestScope();
      setBusyAction(action);
      setError(null);
      setPendingOutcome(null);
      try {
        const result = await runAuthenticatedRequest(
          (accessToken) => operation(accessToken, scope.signal),
          scope.signal,
        );
        apply(result);
        commandStore.current.confirm(action);
        publishFeatureRefresh("dates-changed");
        publishFeatureRefresh("roster-changed");
        publishFeatureRefresh("signups-changed");
      } catch (mutationError) {
        const viewError = toManagementError(mutationError, resourceLabel);
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

  const save = useCallback(async () => {
    if (!date) {
      return;
    }
    await runMutation(
      `edit-date:${date.id}`,
      "service date",
      (accessToken, signal) =>
        api.editServiceDate(
          accessToken,
          date.id,
          date.version,
          values,
          signal,
        ),
      applyDate,
    );
  }, [api, applyDate, date, runMutation, values]);

  const publish = useCallback(async () => {
    if (!date) {
      return;
    }
    const action = `publish:${date.id}`;
    const command = commandStore.current.get(action, () => ({
      dateId: date.id,
      kind: "publish",
    }));
    await runMutation(
      action,
      "publication",
      (accessToken, signal) =>
        api.openServiceDate(
          accessToken,
          command.request.dateId,
          command.key,
          signal,
        ),
      applyDate,
    );
  }, [api, applyDate, date, runMutation]);

  const confirm = useCallback(
    async (reason: string) => {
      if (!date || !confirmation) {
        return;
      }
      const action = `${confirmation}:${date.id}`;
      const command = commandStore.current.get(action, () => ({
        dateId: date.id,
        expectedVersion: date.version,
        kind: confirmation,
        reason: reason.trim() || null,
      }));
      await runMutation(
        action,
        confirmation,
        (accessToken, signal) =>
          command.request.kind === "close"
            ? api.closeServiceDate(
                accessToken,
                command.request.dateId,
                command.key,
                command.request.expectedVersion,
                { reason: command.request.reason },
                signal,
              )
            : command.request.kind === "cancel"
              ? api.cancelServiceDate(
                accessToken,
                command.request.dateId,
                command.key,
                command.request.expectedVersion,
                { reason: command.request.reason },
                signal,
                )
              : Promise.reject(
                  new Error("The date command is not close or cancel."),
                ),
        applyDate,
      );
      setConfirmation(null);
    },
    [api, applyDate, confirmation, date, runMutation],
  );

  const editNeed = useCallback(
    async (need: HelpNeedResponse, request: PatchHelpNeedRequest) => {
      await runMutation(
        `edit-need:${need.id}`,
        "help need",
        (accessToken, signal) =>
          api.editHelpNeed(
            accessToken,
            need.id,
            need.version,
            request,
            signal,
          ),
        (nextNeed) => {
          setDate((current) =>
            current
              ? {
                  ...current,
                  helpNeeds: current.helpNeeds.map((item) =>
                    item.id === nextNeed.id ? nextNeed : item,
                  ),
                }
              : current,
          );
        },
      );
    },
    [api, runMutation],
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
        if (event === "dates-changed") {
          void refresh();
        } else if (event === "session-cleared") {
          controller.current?.abort();
          setDate(null);
          commandStore.current.clear();
        }
      }),
    [refresh],
  );

  return {
    busyAction,
    confirmation,
    confirm,
    date,
    editNeed,
    error,
    isLoading,
    pendingOutcome,
    publish,
    refresh,
    save,
    setConfirmation,
    setValue: (field: keyof DateManagementValues, value: string) =>
      setValues((current) => ({ ...current, [field]: value })),
    values,
  };
}

function HelpNeedEditor({
  disabled,
  need,
  onSave,
}: {
  readonly disabled: boolean;
  readonly need: HelpNeedResponse;
  readonly onSave: (request: PatchHelpNeedRequest) => void;
}) {
  const [instructions, setInstructions] = useState(need.instructions);
  const [capacity, setCapacity] = useState(
    need.availability === null ? "" : String(need.availability),
  );
  const [status, setStatus] = useState<HelpNeedResponse["status"]>(need.status);
  const parsedCapacity =
    capacity.trim() === "" ? null : Number.parseInt(capacity, 10);
  const invalidCapacity =
    parsedCapacity !== null &&
    (!Number.isInteger(parsedCapacity) || parsedCapacity < 0);

  return (
    <View style={styles.card}>
      <Text accessibilityRole="header" style={styles.cardTitle}>
        {formatCategory(need.category)}
      </Text>
      <Text style={styles.status}>Help status: {formatWord(status)}</Text>
      <LabeledInput
        label={`${formatCategory(need.category)} instructions`}
        multiline
        onChangeText={setInstructions}
        value={instructions}
      />
      <LabeledInput
        keyboardType="number-pad"
        label={`${formatCategory(need.category)} capacity; blank means not capped`}
        onChangeText={setCapacity}
        value={capacity}
      />
      <ActionButton
        disabled={disabled}
        label={`${status === "open" ? "Close" : "Open"} ${formatCategory(need.category)} help need`}
        onPress={() => setStatus(status === "open" ? "closed" : "open")}
        secondary
      />
      <ActionButton
        disabled={disabled || invalidCapacity}
        label={`Save ${formatCategory(need.category)} help need`}
        onPress={() =>
          onSave({
            capacity: parsedCapacity,
            instructions,
            status,
          })
        }
      />
      {invalidCapacity ? (
        <Text accessibilityLiveRegion="assertive" style={styles.errorText}>
          Capacity must be a whole number zero or greater, or blank for no cap.
        </Text>
      ) : null}
    </View>
  );
}

function LabeledInput({
  label,
  ...props
}: {
  readonly label: string;
  readonly keyboardType?: "default" | "number-pad";
  readonly multiline?: boolean;
  readonly onChangeText: (value: string) => void;
  readonly value: string;
}) {
  return (
    <View style={styles.field}>
      <Text style={styles.label}>{label}</Text>
      <TextInput
        {...props}
        accessibilityLabel={label}
        autoCapitalize="none"
        style={[styles.input, props.multiline ? styles.multiline : null]}
      />
    </View>
  );
}

function ActionButton({
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
          secondary ? styles.secondaryButtonText : null,
        ]}
      >
        {label}
      </Text>
    </Pressable>
  );
}

function ErrorCard({ message }: { readonly message: string }) {
  return (
    <View style={styles.errorCard}>
      <Text accessibilityLiveRegion="assertive" style={styles.errorText}>
        {message}
      </Text>
    </View>
  );
}

function PendingCard({ message }: { readonly message: string }) {
  return (
    <View style={styles.pendingCard}>
      <Text accessibilityLiveRegion="assertive" style={styles.body}>
        Pending confirmation: {message}
      </Text>
    </View>
  );
}

function ManagementMessage({
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
          <ActionButton
            disabled={false}
            label={actionLabel}
            onPress={onAction}
          />
        ) : null}
      </View>
    </SafeAreaView>
  );
}

function toValues(date: ServiceDateResponse): DateManagementValues {
  return {
    cancellationDeadlineAt: date.cancellationDeadlineAt,
    endsAt: date.endsAt,
    instructions: date.instructions,
    startsAt: date.startsAt,
    title: date.title,
  };
}

function firstParam(value: string | string[] | undefined): string {
  return Array.isArray(value) ? value[0] ?? "" : value ?? "";
}

function formatWord(value: string): string {
  return `${value.charAt(0).toUpperCase()}${value.slice(1)}`;
}

const emptyValues: DateManagementValues = {
  cancellationDeadlineAt: "",
  endsAt: "",
  instructions: "",
  startsAt: "",
  title: "",
};

const styles = StyleSheet.create({
  body: {
    color: authTheme.primaryText,
    fontSize: 16,
    lineHeight: 24,
  },
  button: {
    alignItems: "center",
    backgroundColor: authTheme.accent,
    borderColor: authTheme.accent,
    borderRadius: 14,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: 48,
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
  buttonText: {
    color: authTheme.primaryTextOnAccent,
    fontSize: 16,
    fontWeight: "800",
    textAlign: "center",
  },
  card: {
    backgroundColor: authTheme.panel,
    borderColor: authTheme.border,
    borderRadius: 18,
    borderWidth: 1,
    gap: 12,
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
  destructiveButton: {
    backgroundColor: authTheme.errorText,
    borderColor: authTheme.errorText,
  },
  dialog: {
    backgroundColor: authTheme.panel,
    borderColor: authTheme.accent,
    borderRadius: 20,
    borderWidth: 2,
    gap: 14,
    padding: 18,
  },
  dialogTitle: {
    color: authTheme.accent,
    fontSize: 21,
    fontWeight: "800",
  },
  disabled: {
    opacity: 0.55,
  },
  errorCard: {
    backgroundColor: authTheme.errorBackground,
    borderColor: authTheme.errorBorder,
    borderRadius: 14,
    borderWidth: 1,
    padding: 14,
  },
  errorText: {
    color: authTheme.errorText,
    fontSize: 16,
    lineHeight: 23,
  },
  field: {
    gap: 7,
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
    minHeight: 48,
    paddingHorizontal: 13,
    paddingVertical: 11,
  },
  label: {
    color: authTheme.primaryText,
    fontSize: 15,
    fontWeight: "700",
  },
  link: {
    color: authTheme.accent,
    fontSize: 16,
    fontWeight: "800",
    minHeight: 44,
    paddingVertical: 10,
  },
  multiline: {
    minHeight: 96,
    textAlignVertical: "top",
  },
  pendingCard: {
    backgroundColor: authTheme.panelAlt,
    borderColor: authTheme.accent,
    borderRadius: 14,
    borderWidth: 1,
    padding: 14,
  },
  safeArea: {
    backgroundColor: authTheme.background,
    flex: 1,
  },
  secondaryButton: {
    backgroundColor: authTheme.panel,
  },
  secondaryButtonText: {
    color: authTheme.accent,
  },
  sectionTitle: {
    color: authTheme.primaryText,
    fontSize: 22,
    fontWeight: "800",
    marginTop: 8,
  },
  status: {
    color: authTheme.accent,
    fontSize: 16,
    fontWeight: "800",
  },
  title: {
    color: authTheme.accent,
    fontSize: 28,
    fontWeight: "800",
  },
});
