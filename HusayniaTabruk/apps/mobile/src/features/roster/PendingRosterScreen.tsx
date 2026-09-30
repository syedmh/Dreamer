import { useLocalSearchParams } from "expo-router";
import {
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import type { SignupResponse } from "../../core/api/auth-api";
import { authTheme } from "../auth/auth-theme";
import { SignupPresentation } from "../signups/SignupPresentation";
import { usePendingRoster } from "./usePendingRoster";

export interface PendingRosterViewProps {
  readonly error: string | null;
  readonly isLoading: boolean;
  readonly onRetry: () => void;
  readonly signups: readonly SignupResponse[];
}

export function PendingRosterScreen() {
  const params = useLocalSearchParams<{ dateId?: string | string[] }>();
  const dateId = Array.isArray(params.dateId)
    ? params.dateId[0] ?? ""
    : params.dateId ?? "";
  const state = usePendingRoster(dateId);

  return (
    <PendingRosterView
      error={state.error}
      isLoading={state.isLoading}
      onRetry={() => {
        void state.refresh();
      }}
      signups={state.signups}
    />
  );
}

export function PendingRosterView({
  error,
  isLoading,
  onRetry,
  signups,
}: PendingRosterViewProps) {
  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView
        contentContainerStyle={styles.container}
        contentInsetAdjustmentBehavior="automatic"
      >
        <Text accessibilityRole="header" style={styles.title}>
          Pending roster
        </Text>
        <Text style={styles.subtitle}>
          Only current pending signup requests are shown. Direct contact details
          are not included.
        </Text>

        {isLoading && signups.length === 0 ? (
          <Text accessibilityLiveRegion="polite" style={styles.message}>
            Loading pending signup requests...
          </Text>
        ) : null}

        {error ? (
          <View style={styles.errorCard}>
            <Text accessibilityLiveRegion="assertive" style={styles.errorText}>
              {error}
            </Text>
            <Pressable
              accessibilityLabel="Retry loading pending roster"
              accessibilityRole="button"
              onPress={onRetry}
              style={styles.retry}
            >
              <Text style={styles.retryText}>Retry</Text>
            </Pressable>
          </View>
        ) : null}

        {!isLoading && !error && signups.length === 0 ? (
          <Text style={styles.message}>No pending signup requests</Text>
        ) : null}

        {signups.map((signup) => (
          <SignupPresentation key={signup.id} signup={signup} />
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
