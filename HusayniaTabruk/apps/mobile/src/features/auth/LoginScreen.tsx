import { useRef, useState } from "react";
import {
  KeyboardAvoidingView,
  Platform,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { useAuth } from "./useAuth";
import { authTheme } from "./auth-theme";
import { toAccessibleErrorLines } from "./auth-errors";

export function LoginScreen() {
  const { clearError, error, isSigningIn, signIn } = useAuth();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const passwordInputRef = useRef<TextInput | null>(null);
  const errorLines = toAccessibleErrorLines(error);

  const submit = (): void => {
    void signIn({
      email,
      password,
    });
  };

  const updateEmail = (value: string): void => {
    if (error) {
      clearError();
    }

    setEmail(value);
  };

  const updatePassword = (value: string): void => {
    if (error) {
      clearError();
    }

    setPassword(value);
  };

  return (
    <SafeAreaView style={styles.safeArea}>
      <KeyboardAvoidingView
        behavior={Platform.OS === "ios" ? "padding" : undefined}
        style={styles.flex}
      >
        <ScrollView
          contentContainerStyle={styles.scrollContent}
          keyboardShouldPersistTaps="handled"
        >
          <View style={styles.card}>
            <View style={styles.hero}>
              <Text accessibilityRole="header" style={styles.title}>
                Husaynia Tabruk
              </Text>
              <Text style={styles.subtitle}>
                Sign in to the secure member shell before viewing service dates and signup work.
              </Text>
            </View>

            {errorLines.length > 0 ? (
              <View
                accessibilityLiveRegion="assertive"
                accessibilityRole="alert"
                accessible
                style={styles.errorBanner}
              >
                {errorLines.map((line) => (
                  <Text key={line} style={styles.errorText}>
                    {line}
                  </Text>
                ))}
              </View>
            ) : null}

            <View style={styles.fieldGroup}>
              <Text style={styles.label}>Email address</Text>
              <TextInput
                accessibilityLabel="Email address"
                autoCapitalize="none"
                autoCorrect={false}
                editable={!isSigningIn}
                keyboardType="email-address"
                onChangeText={updateEmail}
                onSubmitEditing={() => passwordInputRef.current?.focus()}
                placeholder="member@example.test"
                returnKeyType="next"
                style={styles.input}
                textContentType="username"
                value={email}
              />
            </View>

            <View style={styles.fieldGroup}>
              <Text style={styles.label}>Password</Text>
              <TextInput
                accessibilityLabel="Password"
                editable={!isSigningIn}
                onChangeText={updatePassword}
                onSubmitEditing={submit}
                placeholder="Enter your password"
                ref={passwordInputRef}
                returnKeyType="go"
                secureTextEntry
                style={styles.input}
                textContentType="password"
                value={password}
              />
            </View>

            <Pressable
              accessibilityLabel="Sign in securely"
              accessibilityRole="button"
              accessibilityState={{
                busy: isSigningIn,
                disabled: isSigningIn,
              }}
              disabled={isSigningIn}
              onPress={submit}
              style={({ pressed }) => [
                styles.primaryAction,
                pressed && !isSigningIn ? styles.primaryActionPressed : null,
                isSigningIn ? styles.primaryActionDisabled : null,
              ]}
            >
              <Text style={styles.primaryActionText}>
                {isSigningIn ? "Signing in..." : "Sign in securely"}
              </Text>
            </Pressable>
          </View>
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: authTheme.panel,
    borderColor: authTheme.border,
    borderRadius: 24,
    borderWidth: 1,
    gap: 18,
    padding: 24,
    shadowColor: "#000000",
    shadowOffset: { height: 6, width: 0 },
    shadowOpacity: 0.08,
    shadowRadius: 12,
  },
  errorBanner: {
    backgroundColor: authTheme.errorBackground,
    borderColor: authTheme.errorBorder,
    borderRadius: 16,
    borderWidth: 1,
    gap: 6,
    padding: 14,
  },
  errorText: {
    color: authTheme.errorText,
    fontSize: 14,
    lineHeight: 20,
  },
  fieldGroup: {
    gap: 8,
  },
  flex: {
    flex: 1,
  },
  hero: {
    gap: 8,
  },
  input: {
    backgroundColor: authTheme.panelAlt,
    borderColor: authTheme.border,
    borderRadius: 14,
    borderWidth: 1,
    color: authTheme.primaryText,
    fontSize: 16,
    paddingHorizontal: 16,
    paddingVertical: 14,
  },
  label: {
    color: authTheme.primaryText,
    fontSize: 14,
    fontWeight: "600",
  },
  primaryAction: {
    alignItems: "center",
    backgroundColor: authTheme.accent,
    borderRadius: 16,
    justifyContent: "center",
    minHeight: 52,
    paddingHorizontal: 20,
  },
  primaryActionDisabled: {
    opacity: 0.7,
  },
  primaryActionPressed: {
    backgroundColor: authTheme.accentPressed,
  },
  primaryActionText: {
    color: authTheme.primaryTextOnAccent,
    fontSize: 16,
    fontWeight: "700",
  },
  safeArea: {
    backgroundColor: authTheme.background,
    flex: 1,
  },
  scrollContent: {
    flexGrow: 1,
    justifyContent: "center",
    padding: 24,
  },
  subtitle: {
    color: authTheme.mutedText,
    fontSize: 15,
    lineHeight: 22,
  },
  title: {
    color: authTheme.accent,
    fontSize: 28,
    fontWeight: "800",
  },
});
