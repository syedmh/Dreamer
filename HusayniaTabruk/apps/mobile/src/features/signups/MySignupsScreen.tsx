import {
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import type { SignupResponse } from "../../core/api/auth-api";
import type { PendingSignupCommand } from "../../core/security/pending-signup-storage";
import { authTheme } from "../auth/auth-theme";
import { useAuth } from "../auth/useAuth";
import { isSignupForPendingCommand } from "./signup-sync-coordinator";
import { SignupPresentation } from "./SignupPresentation";
import { useMySignups } from "./useMySignups";
import { SignupCancellationPanel } from "./management/SignupCancellationPanel";
import {
  type SignupCancellationManagementState,
  useSignupCancellationManagement,
} from "./management/useSignupCancellationManagement";

export interface MySignupsViewProps {
  readonly error: string | null;
  readonly isLoading: boolean;
  readonly onRetry: () => void;
  readonly pendingCommand: PendingSignupCommand | null;
  readonly signups: readonly SignupResponse[];
  readonly cancellationManagement?: SignupCancellationManagementState;
  readonly currentMembershipId?: string;
}

export function MySignupsScreen() {
  const state = useMySignups();
  const management = useSignupCancellationManagement(state.signups);
  const { session } = useAuth();
  const currentMembershipId = session?.actor.membership.id ?? "";
  return (
    <MySignupsView
      error={state.error}
      isLoading={state.isLoading}
      onRetry={() => {
        void state.refresh();
      }}
      pendingCommand={state.pendingCommand}
      signups={state.signups}
      cancellationManagement={management}
      currentMembershipId={currentMembershipId}
    />
  );
}

export function MySignupsView({
  error,
  isLoading,
  onRetry,
  pendingCommand,
  signups,
  cancellationManagement,
  currentMembershipId = "",
}: MySignupsViewProps) {
  const visiblePendingCommand =
    pendingCommand &&
    !signups.some((signup) =>
      isSignupForPendingCommand(signup, pendingCommand),
    )
      ? pendingCommand
      : null;

  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView
        contentContainerStyle={styles.container}
        contentInsetAdjustmentBehavior="automatic"
      >
        <Text accessibilityRole="header" style={styles.title}>
          My signups
        </Text>
        <Text style={styles.subtitle}>
          Authoritative requests and any one locally pending sync are shown here.
        </Text>

        {isLoading && signups.length === 0 && !visiblePendingCommand ? (
          <Text accessibilityLiveRegion="polite" style={styles.message}>
            Loading personal signups...
          </Text>
        ) : null}

        {error ? (
          <View style={styles.errorCard}>
            <Text accessibilityLiveRegion="assertive" style={styles.errorText}>
              {error}
            </Text>
            <Pressable
              accessibilityLabel="Retry loading personal signups"
              accessibilityRole="button"
              onPress={onRetry}
              style={styles.retry}
            >
              <Text style={styles.retryText}>Retry</Text>
            </Pressable>
          </View>
        ) : null}

        {!isLoading &&
        !error &&
        signups.length === 0 &&
        !visiblePendingCommand ? (
          <Text style={styles.message}>No personal signups</Text>
        ) : null}

        {visiblePendingCommand ? (
          <SignupPresentation pendingCommand={visiblePendingCommand} />
        ) : null}
        {signups.map((signup) => (
          <View key={signup.id}>
            <SignupPresentation signup={signup} />
            {cancellationManagement &&
            !cancellationManagement.confirmedWithdrawalIds.includes(
              signup.id,
            ) &&
            cancellationManagement.cancellationDeadlines[
              signup.serviceDateId
            ] ? (
              <SignupCancellationPanel
                busy={
                  cancellationManagement.busySignupId === signup.id
                }
                cancellationDeadlineAt={
                  cancellationManagement.cancellationDeadlines[
                    signup.serviceDateId
                  ]!
                }
                currentMembershipId={currentMembershipId}
                error={cancellationManagement.error}
                onRefresh={() => {
                  onRetry();
                  void cancellationManagement.refresh();
                }}
                onWithdraw={() => {
                  void cancellationManagement.withdraw(signup);
                }}
                pendingOutcome={cancellationManagement.pendingOutcome}
                signup={signup}
              />
            ) : null}
          </View>
        ))}
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    gap: 16,
    padding: 20,
  },
  errorCard: {
    backgroundColor: authTheme.errorBackground,
    borderColor: authTheme.errorBorder,
    borderRadius: 16,
    borderWidth: 1,
    gap: 12,
    padding: 16,
  },
  errorText: {
    color: authTheme.errorText,
    fontSize: 16,
    lineHeight: 23,
  },
  message: {
    color: authTheme.mutedText,
    fontSize: 17,
    lineHeight: 25,
  },
  retry: {
    alignSelf: "flex-start",
    borderColor: authTheme.accent,
    borderRadius: 14,
    borderWidth: 1,
    minHeight: 44,
    paddingHorizontal: 16,
    paddingVertical: 10,
  },
  retryText: {
    color: authTheme.accent,
    fontSize: 16,
    fontWeight: "700",
  },
  safeArea: {
    backgroundColor: authTheme.background,
    flex: 1,
  },
  subtitle: {
    color: authTheme.mutedText,
    fontSize: 16,
    lineHeight: 24,
  },
  title: {
    color: authTheme.accent,
    fontSize: 28,
    fontWeight: "800",
  },
});
