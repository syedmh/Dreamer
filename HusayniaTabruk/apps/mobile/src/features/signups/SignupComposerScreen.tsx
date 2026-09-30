import { useLocalSearchParams } from "expo-router";
import { useMemo, useState } from "react";
import {
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import type {
  SubmitSignupRequest,
} from "../../core/api/auth-api";
import type { EligibleSignupParticipantResponse } from "../../core/api/generated/api-contract-client";
import type {
  HelpCategory,
  PendingSignupCommand,
} from "../../core/security/pending-signup-storage";
import { authTheme } from "../auth/auth-theme";
import { useAuth } from "../auth/useAuth";
import { formatCategory } from "../dates/DateDetailScreen";
import { useServiceDate } from "../dates/useServiceDate";
import {
  validateSignupComposition,
  type GenericLabelBase,
  type SignupKind,
} from "./signup-composition";
import { SignupPresentation } from "./SignupPresentation";
import type { SignupSyncResult } from "./signup-sync-coordinator";
import { useEligibleParticipants } from "./useEligibleParticipants";
import { useSignupSubmission } from "./useSignupSubmission";

export interface SignupComposerViewProps {
  readonly category: HelpCategory | null;
  readonly eligibleError: string | null;
  readonly eligibleLoading: boolean;
  readonly isBusy: boolean;
  readonly onRetryEligible: () => void;
  readonly onRetryPending: () => void;
  readonly onSubmit: (body: SubmitSignupRequest) => void;
  readonly participants: readonly EligibleSignupParticipantResponse[];
  readonly pendingCommand: PendingSignupCommand | null;
  readonly primaryMembershipId: string;
  readonly result: SignupSyncResult | null;
}

export function SignupComposerScreen() {
  const params = useLocalSearchParams<{
    dateId?: string | string[];
    needId?: string | string[];
  }>();
  const dateId = firstParam(params.dateId);
  const needId = firstParam(params.needId);
  const { session } = useAuth();
  const dateState = useServiceDate(dateId);
  const need = dateState.date?.helpNeeds.find((item) => item.id === needId);
  const eligibleState = useEligibleParticipants();
  const submission = useSignupSubmission({
    category: need?.category ?? null,
    helpNeedId: needId,
    serviceDateId: dateId,
  });

  return (
    <SignupComposerView
      category={need?.category ?? null}
      eligibleError={eligibleState.error}
      eligibleLoading={eligibleState.isLoading}
      isBusy={submission.isBusy}
      onRetryEligible={() => {
        void eligibleState.refresh();
      }}
      onRetryPending={() => {
        void submission.retry();
      }}
      onSubmit={(body) => {
        void submission.submit(body);
      }}
      participants={eligibleState.participants}
      pendingCommand={submission.pendingCommand}
      primaryMembershipId={session?.actor.membership.id ?? ""}
      result={submission.result}
    />
  );
}

export function SignupComposerView({
  category,
  eligibleError,
  eligibleLoading,
  isBusy,
  onRetryEligible,
  onRetryPending,
  onSubmit,
  participants,
  pendingCommand,
  primaryMembershipId,
  result,
}: SignupComposerViewProps) {
  const [kind, setKind] = useState<SignupKind>("individual");
  const [selectedIds, setSelectedIds] = useState<readonly string[]>([]);
  const [unnamedCount, setUnnamedCount] = useState(0);
  const [labelBase, setLabelBase] = useState<GenericLabelBase>(null);
  const [labelSuffix, setLabelSuffix] = useState(0);

  const composition = useMemo(
    () =>
      validateSignupComposition({
        kind,
        labelBase,
        labelSuffix: labelSuffix === 0 ? null : labelSuffix,
        memberParticipantIds: selectedIds,
        primaryMembershipId,
        unnamedParticipantCount: unnamedCount,
      }),
    [
      kind,
      labelBase,
      labelSuffix,
      primaryMembershipId,
      selectedIds,
      unnamedCount,
    ],
  );
  const total = 1 + selectedIds.length + unnamedCount;
  const canSubmit =
    Boolean(category) &&
    composition.valid &&
    !isBusy &&
    !pendingCommand;

  const chooseKind = (nextKind: SignupKind) => {
    setKind(nextKind);
    if (nextKind === "individual") {
      setSelectedIds([]);
      setUnnamedCount(0);
      setLabelBase(null);
      setLabelSuffix(0);
    }
  };

  const toggleParticipant = (membershipId: string) => {
    setSelectedIds((current) =>
      current.includes(membershipId)
        ? current.filter((id) => id !== membershipId)
        : [...current, membershipId],
    );
  };

  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView
        contentContainerStyle={styles.container}
        contentInsetAdjustmentBehavior="automatic"
      >
        <Text accessibilityRole="header" style={styles.title}>
          Compose signup
        </Text>
        <Text style={styles.subtitle}>
          {category
            ? `Help category: ${formatCategory(category)}`
            : "Loading help category..."}
        </Text>

        <Text accessibilityRole="header" style={styles.sectionTitle}>
          Composition
        </Text>
        <View accessibilityRole="radiogroup" style={styles.rowWrap}>
          {(["individual", "household", "team"] as const).map((option) => (
            <Pressable
              accessibilityLabel={`${formatKind(option)} signup`}
              accessibilityRole="radio"
              accessibilityState={{ checked: kind === option }}
              key={option}
              onPress={() => chooseKind(option)}
              style={[
                styles.choice,
                kind === option ? styles.choiceSelected : null,
              ]}
            >
              <Text style={styles.choiceText}>{formatKind(option)}</Text>
            </Pressable>
          ))}
        </View>

        {kind !== "individual" ? (
          <>
            <Text accessibilityRole="header" style={styles.sectionTitle}>
              Eligible adult members
            </Text>
            {eligibleLoading ? (
              <Text accessibilityLiveRegion="polite" style={styles.message}>
                Loading eligible members...
              </Text>
            ) : null}
            {eligibleError ? (
              <View style={styles.errorCard}>
                <Text style={styles.errorText}>{eligibleError}</Text>
                <Pressable
                  accessibilityLabel="Retry loading eligible members"
                  accessibilityRole="button"
                  onPress={onRetryEligible}
                  style={styles.secondaryButton}
                >
                  <Text style={styles.secondaryButtonText}>Retry</Text>
                </Pressable>
              </View>
            ) : null}
            {participants
              .filter(
                (participant) =>
                  participant.membershipId !== primaryMembershipId,
              )
              .map((participant) => {
                const checked = selectedIds.includes(participant.membershipId);
                const disabled =
                  !checked && (selectedIds.length >= 20 || total >= 25);
                return (
                  <Pressable
                    accessibilityLabel={`Include ${participant.displayName}`}
                    accessibilityRole="checkbox"
                    accessibilityState={{ checked, disabled }}
                    disabled={disabled}
                    key={participant.membershipId}
                    onPress={() =>
                      toggleParticipant(participant.membershipId)
                    }
                    style={[
                      styles.memberChoice,
                      checked ? styles.choiceSelected : null,
                      disabled ? styles.disabled : null,
                    ]}
                  >
                    <Text style={styles.choiceText}>
                      {checked ? "Selected: " : ""}
                      {participant.displayName}
                    </Text>
                  </Pressable>
                );
              })}

            <Counter
              label="Unnamed participants"
              onDecrement={() =>
                setUnnamedCount((value) => Math.max(0, value - 1))
              }
              onIncrement={() =>
                setUnnamedCount((value) => Math.min(20, value + 1))
              }
              value={unnamedCount}
              decrementDisabled={unnamedCount === 0}
              incrementDisabled={unnamedCount >= 20 || total >= 25}
            />

            <Text accessibilityRole="header" style={styles.sectionTitle}>
              Optional generic label
            </Text>
            <View accessibilityRole="radiogroup" style={styles.rowWrap}>
              {labelOptions.map((option) => (
                <Pressable
                  accessibilityLabel={`Generic label ${option.label}`}
                  accessibilityRole="radio"
                  accessibilityState={{ checked: labelBase === option.value }}
                  key={option.label}
                  onPress={() => {
                    setLabelBase(option.value);
                    if (option.value === null) {
                      setLabelSuffix(0);
                    }
                  }}
                  style={[
                    styles.choice,
                    labelBase === option.value
                      ? styles.choiceSelected
                      : null,
                  ]}
                >
                  <Text style={styles.choiceText}>{option.label}</Text>
                </Pressable>
              ))}
            </View>
            {labelBase ? (
              <Counter
                label="Generic label number"
                onDecrement={() =>
                  setLabelSuffix((value) =>
                    value <= 1 ? 0 : value - 1,
                  )
                }
                onIncrement={() =>
                  setLabelSuffix((value) =>
                    value === 0 ? 1 : Math.min(999, value + 1),
                  )
                }
                value={labelSuffix}
                valueText={labelSuffix === 0 ? "None" : String(labelSuffix)}
                decrementDisabled={labelSuffix === 0}
                incrementDisabled={labelSuffix === 999}
              />
            ) : null}
          </>
        ) : null}

        <View style={styles.summary}>
          <Text style={styles.summaryText}>Total participants: {total}</Text>
          {!composition.valid
            ? composition.errors.map((message) => (
                <Text
                  accessibilityLiveRegion="polite"
                  key={message}
                  style={styles.errorText}
                >
                  {message}
                </Text>
              ))
            : null}
        </View>

        <Pressable
          accessibilityLabel="Submit signup request"
          accessibilityRole="button"
          accessibilityState={{
            busy: isBusy,
            disabled: !canSubmit,
          }}
          disabled={!canSubmit}
          onPress={() => {
            if (composition.valid) {
              onSubmit(composition.body);
            }
          }}
          style={[
            styles.primaryButton,
            !canSubmit ? styles.disabled : null,
          ]}
        >
          <Text style={styles.primaryButtonText}>
            {isBusy ? "Submitting..." : "Submit signup"}
          </Text>
        </Pressable>

        {result?.type === "confirmed" ? (
          <>
            <Text accessibilityLiveRegion="polite" style={styles.resultText}>
              Pending
            </Text>
            <SignupPresentation signup={result.signup} />
          </>
        ) : null}
        {result?.type === "duplicate" ? (
          <>
            <Text accessibilityLiveRegion="polite" style={styles.resultText}>
              Existing signup loaded
            </Text>
            <SignupPresentation signup={result.signup} />
          </>
        ) : null}
        {result?.type === "pendingSync" ? (
          <>
            <SignupPresentation pendingCommand={result.command} />
            <Pressable
              accessibilityLabel="Retry pending signup sync"
              accessibilityRole="button"
              accessibilityState={{ busy: isBusy, disabled: isBusy }}
              disabled={isBusy}
              onPress={onRetryPending}
              style={styles.secondaryButton}
            >
              <Text style={styles.secondaryButtonText}>Retry sync</Text>
            </Pressable>
          </>
        ) : null}
        {result?.type === "permanentError" ||
        result?.type === "expired" ? (
          <Text accessibilityLiveRegion="assertive" style={styles.errorText}>
            {result.message}
          </Text>
        ) : null}
      </ScrollView>
    </SafeAreaView>
  );
}

function Counter({
  decrementDisabled,
  incrementDisabled,
  label,
  onDecrement,
  onIncrement,
  value,
  valueText,
}: {
  readonly decrementDisabled: boolean;
  readonly incrementDisabled: boolean;
  readonly label: string;
  readonly onDecrement: () => void;
  readonly onIncrement: () => void;
  readonly value: number;
  readonly valueText?: string;
}) {
  return (
    <View style={styles.counter}>
      <Text style={styles.summaryText}>
        {label}: {valueText ?? value}
      </Text>
      <View style={styles.rowWrap}>
        <Pressable
          accessibilityLabel={`Decrease ${label.toLowerCase()}`}
          accessibilityRole="button"
          accessibilityState={{ disabled: decrementDisabled }}
          disabled={decrementDisabled}
          onPress={onDecrement}
          style={[
            styles.counterButton,
            decrementDisabled ? styles.disabled : null,
          ]}
        >
          <Text style={styles.counterButtonText}>−</Text>
        </Pressable>
        <Pressable
          accessibilityLabel={`Increase ${label.toLowerCase()}`}
          accessibilityRole="button"
          accessibilityState={{ disabled: incrementDisabled }}
          disabled={incrementDisabled}
          onPress={onIncrement}
          style={[
            styles.counterButton,
            incrementDisabled ? styles.disabled : null,
          ]}
        >
          <Text style={styles.counterButtonText}>+</Text>
        </Pressable>
      </View>
    </View>
  );
}

function formatKind(kind: SignupKind): string {
  return `${kind.charAt(0).toUpperCase()}${kind.slice(1)}`;
}

function firstParam(value: string | string[] | undefined): string {
  return Array.isArray(value) ? value[0] ?? "" : value ?? "";
}

const labelOptions: readonly {
  readonly label: string;
  readonly value: GenericLabelBase;
}[] = [
  { label: "None", value: null },
  { label: "Household", value: "Household" },
  { label: "Team", value: "Team" },
  { label: "Group", value: "Group" },
];

const styles = StyleSheet.create({
  choice: {
    borderColor: authTheme.border,
    borderRadius: 15,
    borderWidth: 1,
    minHeight: 44,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  choiceSelected: {
    backgroundColor: authTheme.successTint,
    borderColor: authTheme.accent,
  },
  choiceText: {
    color: authTheme.primaryText,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 21,
  },
  container: {
    gap: 16,
    padding: 20,
  },
  counter: {
    backgroundColor: authTheme.panelAlt,
    borderRadius: 16,
    gap: 10,
    padding: 14,
  },
  counterButton: {
    alignItems: "center",
    borderColor: authTheme.accent,
    borderRadius: 14,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: 48,
    minWidth: 56,
  },
  counterButtonText: {
    color: authTheme.accent,
    fontSize: 24,
    fontWeight: "800",
  },
  disabled: {
    opacity: 0.5,
  },
  errorCard: {
    backgroundColor: authTheme.errorBackground,
    borderColor: authTheme.errorBorder,
    borderRadius: 16,
    borderWidth: 1,
    gap: 10,
    padding: 14,
  },
  errorText: {
    color: authTheme.errorText,
    fontSize: 15,
    lineHeight: 22,
  },
  memberChoice: {
    backgroundColor: authTheme.panel,
    borderColor: authTheme.border,
    borderRadius: 15,
    borderWidth: 1,
    minHeight: 48,
    padding: 13,
  },
  message: {
    color: authTheme.mutedText,
    fontSize: 15,
    lineHeight: 22,
  },
  primaryButton: {
    alignItems: "center",
    backgroundColor: authTheme.accent,
    borderRadius: 16,
    justifyContent: "center",
    minHeight: 52,
    paddingHorizontal: 18,
    paddingVertical: 13,
  },
  primaryButtonText: {
    color: authTheme.primaryTextOnAccent,
    fontSize: 17,
    fontWeight: "800",
  },
  resultText: {
    color: authTheme.accent,
    fontSize: 18,
    fontWeight: "800",
  },
  rowWrap: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 9,
  },
  safeArea: {
    backgroundColor: authTheme.background,
    flex: 1,
  },
  secondaryButton: {
    alignItems: "center",
    alignSelf: "flex-start",
    borderColor: authTheme.accent,
    borderRadius: 14,
    borderWidth: 1,
    minHeight: 44,
    paddingHorizontal: 15,
    paddingVertical: 10,
  },
  secondaryButtonText: {
    color: authTheme.accent,
    fontSize: 15,
    fontWeight: "800",
  },
  sectionTitle: {
    color: authTheme.primaryText,
    fontSize: 20,
    fontWeight: "800",
    marginTop: 6,
  },
  subtitle: {
    color: authTheme.mutedText,
    fontSize: 16,
    lineHeight: 24,
  },
  summary: {
    backgroundColor: authTheme.panel,
    borderColor: authTheme.border,
    borderRadius: 16,
    borderWidth: 1,
    gap: 7,
    padding: 15,
  },
  summaryText: {
    color: authTheme.primaryText,
    fontSize: 16,
    fontWeight: "700",
    lineHeight: 23,
  },
  title: {
    color: authTheme.accent,
    fontSize: 28,
    fontWeight: "800",
  },
});
