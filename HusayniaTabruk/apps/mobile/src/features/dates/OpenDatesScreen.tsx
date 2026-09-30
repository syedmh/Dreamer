import { Link } from "expo-router";
import { useMemo, useState } from "react";
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
import {
  filterOpenServiceDates,
  type DateCategoryFilter,
} from "./date-use-cases";
import { useOpenDates } from "./useOpenDates";

export interface OpenDatesViewProps {
  readonly dates: readonly ServiceDateResponse[];
  readonly error: string | null;
  readonly isLoading: boolean;
  readonly onRetry: () => void;
}

export function OpenDatesScreen() {
  const state = useOpenDates();
  return (
    <OpenDatesView
      dates={state.dates}
      error={state.error}
      isLoading={state.isLoading}
      onRetry={() => {
        void state.refresh();
      }}
    />
  );
}

export function OpenDatesView({
  dates,
  error,
  isLoading,
  onRetry,
}: OpenDatesViewProps) {
  const [filter, setFilter] = useState<DateCategoryFilter>("all");
  const visibleDates = useMemo(
    () => filterOpenServiceDates(dates, filter),
    [dates, filter],
  );

  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView
        contentContainerStyle={styles.container}
        contentInsetAdjustmentBehavior="automatic"
      >
        <Text accessibilityRole="header" style={styles.title}>
          Open dates
        </Text>
        <Text style={styles.subtitle}>
          Choose a published date and an open way to help.
        </Text>

        <View accessibilityRole="radiogroup" style={styles.filters}>
          {filterOptions.map((option) => (
            <Pressable
              accessibilityLabel={`Filter dates by ${option.label}`}
              accessibilityRole="radio"
              accessibilityState={{ checked: filter === option.value }}
              key={option.value}
              onPress={() => setFilter(option.value)}
              style={[
                styles.filter,
                filter === option.value ? styles.filterSelected : null,
              ]}
            >
              <Text style={styles.filterText}>{option.label}</Text>
            </Pressable>
          ))}
        </View>

        {isLoading && dates.length === 0 ? (
          <Text accessibilityLiveRegion="polite" style={styles.message}>
            Loading open dates...
          </Text>
        ) : null}

        {error ? (
          <View style={styles.errorCard}>
            <Text accessibilityLiveRegion="assertive" style={styles.errorText}>
              {error}
            </Text>
            <Pressable
              accessibilityLabel="Retry loading open dates"
              accessibilityRole="button"
              onPress={onRetry}
              style={styles.action}
            >
              <Text style={styles.actionText}>Retry</Text>
            </Pressable>
          </View>
        ) : null}

        {!isLoading && !error && dates.length === 0 ? (
          <Text style={styles.message}>No published dates</Text>
        ) : null}

        {!isLoading &&
        !error &&
        dates.length > 0 &&
        visibleDates.length === 0 ? (
          <Text style={styles.message}>No matching help requested</Text>
        ) : null}

        {visibleDates.map((serviceDate) => (
          <View key={serviceDate.id} style={styles.card}>
            <Text accessibilityRole="header" style={styles.cardTitle}>
              {serviceDate.title}
            </Text>
            <Text style={styles.body}>{formatDateRange(serviceDate)}</Text>
            <Text style={styles.body}>{serviceDate.instructions}</Text>
            <Text style={styles.meta}>
              Cancellation deadline:{" "}
              {formatTimestamp(serviceDate.cancellationDeadlineAt)}
            </Text>
            <Link
              accessibilityLabel={`View details for ${serviceDate.title}`}
              accessibilityRole="link"
              href={{
                pathname: "/(member)/dates/[dateId]",
                params: { dateId: serviceDate.id },
              }}
              style={styles.link}
            >
              View date details
            </Link>
          </View>
        ))}
      </ScrollView>
    </SafeAreaView>
  );
}

const filterOptions: readonly {
  readonly label: string;
  readonly value: DateCategoryFilter;
}[] = [
  { label: "All", value: "all" },
  { label: "Food preparation", value: "foodPreparation" },
  { label: "Serving", value: "serving" },
  { label: "Cleanup", value: "cleanup" },
];

export function formatTimestamp(value: string): string {
  const timestamp = new Date(value);
  return Number.isNaN(timestamp.valueOf())
    ? value
    : timestamp.toLocaleString();
}

function formatDateRange(serviceDate: ServiceDateResponse): string {
  return `${formatTimestamp(serviceDate.startsAt)} – ${formatTimestamp(serviceDate.endsAt)}`;
}

const styles = StyleSheet.create({
  action: {
    alignItems: "center",
    alignSelf: "flex-start",
    borderColor: authTheme.accent,
    borderRadius: 14,
    borderWidth: 1,
    minHeight: 44,
    paddingHorizontal: 16,
    paddingVertical: 10,
  },
  actionText: {
    color: authTheme.accent,
    fontSize: 16,
    fontWeight: "700",
  },
  body: {
    color: authTheme.primaryText,
    fontSize: 16,
    lineHeight: 24,
  },
  card: {
    backgroundColor: authTheme.panel,
    borderColor: authTheme.border,
    borderRadius: 20,
    borderWidth: 1,
    gap: 10,
    padding: 18,
  },
  cardTitle: {
    color: authTheme.accent,
    fontSize: 20,
    fontWeight: "800",
  },
  container: {
    gap: 18,
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
  filter: {
    borderColor: authTheme.border,
    borderRadius: 16,
    borderWidth: 1,
    minHeight: 44,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  filterSelected: {
    backgroundColor: authTheme.successTint,
    borderColor: authTheme.accent,
  },
  filterText: {
    color: authTheme.primaryText,
    fontSize: 15,
    fontWeight: "700",
  },
  filters: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  link: {
    color: authTheme.accent,
    fontSize: 16,
    fontWeight: "800",
    minHeight: 44,
    paddingVertical: 10,
  },
  message: {
    color: authTheme.mutedText,
    fontSize: 17,
    lineHeight: 25,
  },
  meta: {
    color: authTheme.mutedText,
    fontSize: 14,
    lineHeight: 21,
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
