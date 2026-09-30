import { useState } from "react";
import { Pressable, StyleSheet, Text, View } from "react-native";

import type { SignupResponse } from "../../../core/api/auth-api";
import { authTheme } from "../../auth/auth-theme";

export interface SignupCancellationPanelProps {
  readonly busy: boolean;
  readonly cancellationDeadlineAt: string;
  readonly currentMembershipId: string;
  readonly error: string | null;
  readonly now?: Date;
  readonly onWithdraw: () => void;
  readonly onRefresh?: () => void;
  readonly pendingOutcome: string | null;
  readonly signup: SignupResponse;
}

export function SignupCancellationPanel({
  busy,
  cancellationDeadlineAt,
  currentMembershipId,
  error,
  now = new Date(),
  onWithdraw,
  onRefresh,
  pendingOutcome,
  signup,
}: SignupCancellationPanelProps) {
  const [confirming, setConfirming] = useState(false);
  const isPrimary =
    signup.primaryContact.membershipId === currentMembershipId;
  const isApproved = signup.status === "approved";
  const beforeDeadline =
    now.getTime() < new Date(cancellationDeadlineAt).getTime();

  if (!isPrimary || !isApproved) {
    return null;
  }

  return (
    <View style={styles.container}>
      <Text style={styles.deadline}>
        Self-cancellation deadline: {cancellationDeadlineAt}
      </Text>
      {beforeDeadline ? (
        <Pressable
          accessibilityLabel="Cancel my approved signup before the deadline"
          accessibilityRole="button"
          accessibilityState={{ busy, disabled: busy }}
          disabled={busy}
          onPress={() => setConfirming(true)}
          style={styles.button}
        >
          <Text style={styles.buttonText}>Cancel my signup</Text>
        </Pressable>
      ) : (
        <Text accessibilityLiveRegion="polite" style={styles.message}>
          The self-cancellation deadline has passed. Contact the Food Incharge.
        </Text>
      )}
      {confirming ? (
        <View
          accessibilityLabel="Confirm signup cancellation"
          accessibilityViewIsModal
          style={styles.dialog}
        >
          <Text accessibilityRole="header" style={styles.dialogTitle}>
            Confirm cancellation
          </Text>
          <Text style={styles.message}>
            This immediately withdraws your approved signup.
          </Text>
          <Pressable
            accessibilityLabel="Confirm cancel my signup"
            accessibilityRole="button"
            accessibilityState={{ busy, disabled: busy }}
            disabled={busy}
            onPress={onWithdraw}
            style={styles.button}
          >
            <Text style={styles.buttonText}>Confirm cancellation</Text>
          </Pressable>
          <Pressable
            accessibilityLabel="Keep my signup"
            accessibilityRole="button"
            disabled={busy}
            onPress={() => setConfirming(false)}
            style={styles.secondaryButton}
          >
            <Text style={styles.secondaryText}>Keep my signup</Text>
          </Pressable>
        </View>
      ) : null}
      {error ? (
        <Text accessibilityLiveRegion="assertive" style={styles.error}>
          {error}
        </Text>
      ) : null}
      {pendingOutcome ? (
        <>
          <Text accessibilityLiveRegion="assertive" style={styles.message}>
            Pending confirmation: {pendingOutcome}
          </Text>
          {onRefresh ? (
            <Pressable
              accessibilityLabel="Refresh signup status"
              accessibilityRole="button"
              disabled={busy}
              onPress={onRefresh}
              style={styles.secondaryButton}
            >
              <Text style={styles.secondaryText}>Refresh signup status</Text>
            </Pressable>
          ) : null}
        </>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  button: {
    alignItems: "center",
    backgroundColor: authTheme.errorText,
    borderRadius: 12,
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
  container: {
    borderTopColor: authTheme.border,
    borderTopWidth: 1,
    gap: 10,
    marginTop: 8,
    paddingTop: 12,
  },
  deadline: {
    color: authTheme.primaryText,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 22,
  },
  dialog: {
    backgroundColor: authTheme.panelAlt,
    borderColor: authTheme.accent,
    borderRadius: 14,
    borderWidth: 1,
    gap: 10,
    padding: 14,
  },
  dialogTitle: {
    color: authTheme.accent,
    fontSize: 18,
    fontWeight: "800",
  },
  error: {
    color: authTheme.errorText,
    fontSize: 15,
    lineHeight: 22,
  },
  message: {
    color: authTheme.primaryText,
    fontSize: 15,
    lineHeight: 22,
  },
  secondaryButton: {
    alignItems: "center",
    backgroundColor: authTheme.panel,
    borderColor: authTheme.accent,
    borderRadius: 12,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: 46,
    paddingHorizontal: 14,
    paddingVertical: 11,
  },
  secondaryText: {
    color: authTheme.accent,
    fontSize: 15,
    fontWeight: "800",
  },
});
