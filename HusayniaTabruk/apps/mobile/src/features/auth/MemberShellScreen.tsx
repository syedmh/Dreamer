import { Link } from "expo-router";
import { Pressable, StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { useAuth } from "./useAuth";
import { authTheme } from "./auth-theme";

export function MemberShellScreen() {
  const { isSigningOut, session, signOut } = useAuth();

  if (!session) {
    return null;
  }

  const roleSummary =
    session.actor.roles.length > 0
      ? session.actor.roles.map(formatRoleName).join(", ")
      : "Member";

  return (
    <SafeAreaView style={styles.safeArea}>
      <View style={styles.container}>
        <View style={styles.banner}>
          <Text accessibilityRole="header" style={styles.title}>
            Secure member shell
          </Text>
          <Text style={styles.subtitle}>
            Your session is active and stored privately on this device.
          </Text>
        </View>

        <View style={styles.summaryCard}>
          <SummaryRow label="Member" value={session.actor.membership.displayName} />
          <SummaryRow label="Organization" value={session.actor.organization.name} />
          <SummaryRow label="Time zone" value={session.actor.organization.timeZone} />
          <SummaryRow label="Roles" value={roleSummary} />
        </View>

        <View style={styles.navigation}>
          <Link
            accessibilityLabel="Browse open service dates"
            accessibilityRole="link"
            href="/(member)/dates"
            style={styles.navigationLink}
          >
            Open dates
          </Link>
          <Link
            accessibilityLabel="View my signups"
            accessibilityRole="link"
            href="/(member)/signups"
            style={styles.navigationLink}
          >
            My signups
          </Link>
        </View>

        <Pressable
          accessibilityLabel="Sign out securely"
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
            styles.secondaryAction,
            pressed && !isSigningOut ? styles.secondaryActionPressed : null,
            isSigningOut ? styles.secondaryActionDisabled : null,
          ]}
        >
          <Text style={styles.secondaryActionText}>
            {isSigningOut ? "Signing out..." : "Sign out"}
          </Text>
        </Pressable>
      </View>
    </SafeAreaView>
  );
}

function SummaryRow({
  label,
  value,
}: {
  readonly label: string;
  readonly value: string;
}) {
  return (
    <View style={styles.summaryRow}>
      <Text style={styles.summaryLabel}>{label}</Text>
      <Text style={styles.summaryValue}>{value}</Text>
    </View>
  );
}

function formatRoleName(role: string): string {
  if (role === "FoodIncharge") {
    return "Food Incharge";
  }

  return role;
}

const styles = StyleSheet.create({
  banner: {
    gap: 8,
  },
  container: {
    flex: 1,
    gap: 24,
    padding: 24,
  },
  safeArea: {
    backgroundColor: authTheme.background,
    flex: 1,
  },
  navigation: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 12,
  },
  navigationLink: {
    backgroundColor: authTheme.panel,
    borderColor: authTheme.accent,
    borderRadius: 16,
    borderWidth: 1,
    color: authTheme.accent,
    fontSize: 16,
    fontWeight: "800",
    minHeight: 48,
    paddingHorizontal: 18,
    paddingVertical: 13,
  },
  secondaryAction: {
    alignItems: "center",
    alignSelf: "flex-start",
    backgroundColor: authTheme.panel,
    borderColor: authTheme.accent,
    borderRadius: 16,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: 48,
    paddingHorizontal: 18,
  },
  secondaryActionDisabled: {
    opacity: 0.7,
  },
  secondaryActionPressed: {
    backgroundColor: authTheme.successTint,
  },
  secondaryActionText: {
    color: authTheme.accent,
    fontSize: 15,
    fontWeight: "700",
  },
  subtitle: {
    color: authTheme.mutedText,
    fontSize: 15,
    lineHeight: 22,
  },
  summaryCard: {
    backgroundColor: authTheme.panel,
    borderColor: authTheme.border,
    borderRadius: 22,
    borderWidth: 1,
    gap: 16,
    padding: 20,
  },
  summaryLabel: {
    color: authTheme.mutedText,
    fontSize: 13,
    fontWeight: "600",
    textTransform: "uppercase",
  },
  summaryRow: {
    gap: 4,
  },
  summaryValue: {
    color: authTheme.primaryText,
    fontSize: 17,
    fontWeight: "600",
  },
  title: {
    color: authTheme.accent,
    fontSize: 28,
    fontWeight: "800",
  },
});
