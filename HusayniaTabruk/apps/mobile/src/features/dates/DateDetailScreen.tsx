import { Link, useLocalSearchParams } from "expo-router";
import {
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import type { ServiceDateResponse } from "../../core/api/auth-api";
import { authTheme } from "../auth/auth-theme";
import { useAuth } from "../auth/useAuth";
import { formatTimestamp } from "./OpenDatesScreen";
import { useServiceDate } from "./useServiceDate";

export interface DateDetailViewProps {
  readonly date: ServiceDateResponse | null;
  readonly error: string | null;
  readonly isFoodIncharge: boolean;
  readonly isLoading: boolean;
  readonly membershipId: string;
  readonly onRetry: () => void;
}

export function DateDetailScreen() {
  const params = useLocalSearchParams<{ dateId?: string | string[] }>();
  const dateId = firstParam(params.dateId);
  const { session } = useAuth();
  const state = useServiceDate(dateId);

  return (
    <DateDetailView
      date={state.date}
      error={state.error}
      isFoodIncharge={
        session?.actor.roles.includes("FoodIncharge") ?? false
      }
      isLoading={state.isLoading}
      membershipId={session?.actor.membership.id ?? ""}
      onRetry={() => {
        void state.refresh();
      }}
    />
  );
}

export function DateDetailView({
  date,
  error,
  isFoodIncharge,
  isLoading,
  membershipId,
  onRetry,
}: DateDetailViewProps) {
  if (isLoading && !date) {
    return <CenteredMessage message="Loading date details..." />;
  }

  if (error && !date) {
    return (
      <CenteredMessage
        actionLabel="Retry loading date details"
        message={error}
        onAction={onRetry}
      />
    );
  }

  if (!date) {
    return <CenteredMessage message="The service date is unavailable." />;
  }

  const canViewRoster =
    isFoodIncharge && date.managerMembershipId === membershipId;

  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView
        contentContainerStyle={styles.container}
        contentInsetAdjustmentBehavior="automatic"
      >
        <Text accessibilityRole="header" style={styles.title}>
          {date.title}
        </Text>
        <Text style={styles.body}>{date.instructions}</Text>
        <Text style={styles.meta}>
          Starts: {formatTimestamp(date.startsAt)}
        </Text>
        <Text style={styles.meta}>Ends: {formatTimestamp(date.endsAt)}</Text>
        <Text style={styles.meta}>
          Cancellation deadline: {formatTimestamp(date.cancellationDeadlineAt)}
        </Text>
        <Text style={styles.status}>Date status: {formatWord(date.status)}</Text>

        {canViewRoster ? (
          <>
            <Link
              accessibilityLabel={`Manage service date ${date.title}`}
              accessibilityRole="link"
              href={{
                pathname: "/(incharge)/manage/dates/[dateId]",
                params: { dateId: date.id },
              }}
              style={styles.link}
            >
              Manage service date
            </Link>
            <Link
              accessibilityLabel={`View pending roster for ${date.title}`}
              accessibilityRole="link"
              href={{
                pathname: "/(incharge)/dates/[dateId]/roster",
                params: { dateId: date.id },
              }}
              style={styles.link}
            >
              View pending roster
            </Link>
          </>
        ) : null}

        <Text accessibilityRole="header" style={styles.sectionTitle}>
          Help requested
        </Text>
        {date.helpNeeds.map((need) => (
          <View key={need.id} style={styles.card}>
            <Text accessibilityRole="header" style={styles.cardTitle}>
              {formatCategory(need.category)}
            </Text>
            <Text style={styles.body}>{need.instructions}</Text>
            <Text style={styles.meta}>
              Availability:{" "}
              {need.availability === null
                ? "Not capped"
                : `${need.availability} places`}
            </Text>
            <Text style={styles.status}>
              Help status: {formatWord(need.status)}
            </Text>
            {date.status === "open" && need.status === "open" ? (
              <Link
                accessibilityLabel={`Sign up for ${formatCategory(need.category)}`}
                accessibilityRole="link"
                href={{
                  pathname:
                    "/(member)/dates/[dateId]/needs/[needId]/signup",
                  params: { dateId: date.id, needId: need.id },
                }}
                style={styles.link}
              >
                Compose signup
              </Link>
            ) : null}
          </View>
        ))}
      </ScrollView>
    </SafeAreaView>
  );
}

export function formatCategory(
  category: ServiceDateResponse["helpNeeds"][number]["category"],
): string {
  if (category === "foodPreparation") {
    return "Food preparation";
  }
  return category === "serving" ? "Serving" : "Cleanup";
}

function formatWord(value: string): string {
  return `${value.charAt(0).toUpperCase()}${value.slice(1)}`;
}

function firstParam(value: string | string[] | undefined): string {
  return Array.isArray(value) ? value[0] ?? "" : value ?? "";
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
          <Pressable
            accessibilityLabel={actionLabel}
            accessibilityRole="button"
            onPress={onAction}
            style={styles.button}
          >
            <Text style={styles.buttonText}>Retry</Text>
          </Pressable>
        ) : null}
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  body: {
    color: authTheme.primaryText,
    fontSize: 16,
    lineHeight: 24,
  },
  button: {
    borderColor: authTheme.accent,
    borderRadius: 14,
    borderWidth: 1,
    minHeight: 44,
    paddingHorizontal: 16,
    paddingVertical: 10,
  },
  buttonText: {
    color: authTheme.accent,
    fontSize: 16,
    fontWeight: "700",
  },
  card: {
    backgroundColor: authTheme.panel,
    borderColor: authTheme.border,
    borderRadius: 20,
    borderWidth: 1,
    gap: 9,
    padding: 18,
  },
  cardTitle: {
    color: authTheme.accent,
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
  link: {
    color: authTheme.accent,
    fontSize: 16,
    fontWeight: "800",
    minHeight: 44,
    paddingVertical: 10,
  },
  meta: {
    color: authTheme.mutedText,
    fontSize: 15,
    lineHeight: 22,
  },
  safeArea: {
    backgroundColor: authTheme.background,
    flex: 1,
  },
  sectionTitle: {
    color: authTheme.primaryText,
    fontSize: 22,
    fontWeight: "800",
    marginTop: 8,
  },
  status: {
    color: authTheme.primaryText,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 22,
  },
  title: {
    color: authTheme.accent,
    fontSize: 28,
    fontWeight: "800",
  },
});
