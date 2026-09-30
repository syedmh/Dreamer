import { describe, expect, it, jest } from "@jest/globals";
import type { ReactNode } from "react";
import { Pressable, ScrollView, StyleSheet, Text, TextInput } from "react-native";
import { act, create } from "react-test-renderer";

import DateManagementRoute from "../../../app/(incharge)/manage/dates/[dateId]";
import RosterManagementRoute from "../../../app/(incharge)/manage/dates/[dateId]/roster";
import type { ServiceDateResponse } from "../../../src/core/api/auth-api";
import { DateManagementView } from "../../../src/features/admin/DateManagementScreen";

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  return {
    Link: (props: Record<string, unknown>) =>
      React.createElement("Link", props, props.children as ReactNode),
    useFocusEffect: jest.fn(),
    useLocalSearchParams: () => ({ dateId: "date-1" }),
  };
});

const date: ServiceDateResponse = {
  cancellationDeadlineAt: "2026-08-20T10:00:00.000Z",
  endsAt: "2026-08-21T12:00:00.000Z",
  helpNeeds: [],
  id: "date-1",
  instructions: "Arrive early",
  managerMembershipId: "manager-1",
  startsAt: "2026-08-21T10:00:00.000Z",
  status: "open",
  title: "Service day",
  version: 4,
};

describe("coordination management routing and accessibility", () => {
  it("exposes only the Food Incharge management screens through the authorized route group", () => {
    expect(DateManagementRoute.name).toBe("DateManagementScreen");
    expect(RosterManagementRoute.name).toBe("RosterManagementScreen");
  });

  it("keeps management content scrollable, scalable, labeled, and switch-operable", async () => {
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <DateManagementView
          confirmation="close"
          date={date}
          error={null}
          isBusy={false}
          onChange={jest.fn()}
          onConfirm={jest.fn()}
          onEditNeed={jest.fn()}
          onPublish={jest.fn()}
          onRefresh={jest.fn()}
          onRequestConfirmation={jest.fn()}
          onSave={jest.fn()}
          pendingOutcome={null}
          values={{
            cancellationDeadlineAt: date.cancellationDeadlineAt,
            endsAt: date.endsAt,
            instructions: date.instructions,
            startsAt: date.startsAt,
            title: date.title,
          }}
        />,
      );
    });

    expect(renderer.root.findAllByType(ScrollView)).toHaveLength(1);
    for (const text of renderer.root.findAllByType(Text)) {
      expect(text.props.allowFontScaling).not.toBe(false);
      expect(text.props.maxFontSizeMultiplier).toBeUndefined();
    }
    for (const input of renderer.root.findAllByType(TextInput)) {
      expect(input.props.accessibilityLabel).toEqual(expect.any(String));
      expect(StyleSheet.flatten(input.props.style).height).toBeUndefined();
    }
    for (const button of renderer.root.findAllByType(Pressable)) {
      expect(button.props.accessibilityLabel).toEqual(expect.any(String));
      expect(button.props.accessibilityRole).toBe("button");
      expect(StyleSheet.flatten(button.props.style).minHeight).toBeGreaterThanOrEqual(
        44,
      );
    }
    expect(
      renderer.root.findByProps({
        accessibilityLabel: "Close service date confirmation",
      }).props.accessibilityViewIsModal,
    ).toBe(true);
  });
});
