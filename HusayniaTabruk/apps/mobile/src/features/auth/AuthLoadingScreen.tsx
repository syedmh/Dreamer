import {
  ActivityIndicator,
  Pressable,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { authTheme } from "./auth-theme";
import { useAuth } from "./useAuth";

export interface AuthLoadingScreenProps {
  readonly message: string;
}

export function AuthLoadingScreen({ message }: AuthLoadingScreenProps) {
  return (
    <SafeAreaView style={styles.safeArea}>
      <View
        accessibilityLabel={message}
        accessibilityRole="progressbar"
        style={styles.container}
      >
        <ActivityIndicator color={authTheme.accent} size="large" />
        <Text style={styles.message}>{message}</Text>
      </View>
    </SafeAreaView>
  );
}

export function AuthRestoringScreen() {
  const { error, isSigningOut, signOut } = useAuth();

  if (!error) {
    return <AuthLoadingScreen message="Restoring your secure session..." />;
  }

  return (
    <SafeAreaView style={styles.safeArea}>
      <View
        accessibilityLiveRegion="assertive"
        accessibilityRole="alert"
        style={styles.container}
      >
        <Text style={styles.title}>Secure sign-out is incomplete</Text>
        <Text style={styles.message}>{error.detail}</Text>
        <Pressable
          accessibilityLabel="Retry secure sign-out"
          accessibilityRole="button"
          accessibilityState={{
            busy: isSigningOut,
            disabled: isSigningOut,
          }}
          disabled={isSigningOut}
          onPress={() => {
            void signOut();
          }}
          style={({ pressed }) => [
            styles.retryButton,
            pressed && !isSigningOut ? styles.retryButtonPressed : null,
            isSigningOut ? styles.retryButtonDisabled : null,
          ]}
        >
          <Text style={styles.retryText}>
            {isSigningOut ? "Finishing secure sign-out..." : "Retry secure sign-out"}
          </Text>
        </Pressable>
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    alignItems: "center",
    flex: 1,
    gap: 16,
    justifyContent: "center",
    padding: 24,
  },
  message: {
    color: authTheme.primaryText,
    fontSize: 16,
    textAlign: "center",
  },
  retryButton: {
    alignItems: "center",
    backgroundColor: authTheme.accent,
    borderRadius: 12,
    justifyContent: "center",
    minHeight: 44,
    paddingHorizontal: 18,
    paddingVertical: 10,
  },
  retryButtonDisabled: {
    opacity: 0.6,
  },
  retryButtonPressed: {
    opacity: 0.8,
  },
  retryText: {
    color: authTheme.primaryTextOnAccent,
    fontSize: 16,
    fontWeight: "700",
  },
  safeArea: {
    backgroundColor: authTheme.background,
    flex: 1,
  },
  title: {
    color: authTheme.primaryText,
    fontSize: 20,
    fontWeight: "800",
    textAlign: "center",
  },
});
