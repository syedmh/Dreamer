import { describe, expect, it, jest } from "@jest/globals";
import type { ReactNode } from "react";
import { Pressable, ScrollView, StyleSheet, Text } from "react-native";
import { act, create } from "react-test-renderer";

import type { SignupResponse } from "../../../src/core/api/auth-api";
import { OpenDatesView } from "../../../src/features/dates/OpenDatesScreen";
import { PendingRosterView } from "../../../src/features/roster/PendingRosterScreen";
import { SignupComposerView } from "../../../src/features/signups/SignupComposerScreen";

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  return {
    Link: (props: Record<string, unknown>) =>
      React.createElement("Link", props, props.children as ReactNode),
    useFocusEffect: jest.fn(),
    useLocalSearchParams: () => ({}),
  };
});

describe("first-slice accessibility", () => {
  it("exposes labeled checked, disabled, and busy control state", async () => {
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <SignupComposerView
          category="serving"
          eligibleError={null}
          eligibleLoading={false}
          isBusy
          onRetryEligible={jest.fn()}
          onRetryPending={jest.fn()}
          onSubmit={jest.fn()}
          participants={[
            {
              displayName: "Eligible Member",
              membershipId: "membership-2",
            },
          ]}
          pendingCommand={null}
          primaryMembershipId="membership-1"
          result={null}
        />,
      );
    });

    const individual = renderer.root.findByProps({
      accessibilityLabel: "Individual signup",
    });
    const submit = renderer.root.findByProps({
      accessibilityLabel: "Submit signup request",
    });

    expect(individual.props.accessibilityRole).toBe("radio");
    expect(individual.props.accessibilityState.checked).toBe(true);
    expect(submit.props.accessibilityRole).toBe("button");
    expect(submit.props.accessibilityState).toMatchObject({
      busy: true,
      disabled: true,
    });
  });

  it("uses flexible minimum sizes rather than fixed control heights", async () => {
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <SignupComposerView
          category="serving"
          eligibleError={null}
          eligibleLoading={false}
          isBusy={false}
          onRetryEligible={jest.fn()}
          onRetryPending={jest.fn()}
          onSubmit={jest.fn()}
          participants={[]}
          pendingCommand={null}
          primaryMembershipId="membership-1"
          result={null}
        />,
      );
    });
    const button = renderer.root.findByProps({
      accessibilityLabel: "Submit signup request",
    });
    const style = StyleSheet.flatten(button.props.style);

    expect(style.minHeight).toBeGreaterThanOrEqual(44);
    expect(style.height).toBeUndefined();
  });

  it("keeps text scaling enabled and content scrollable for enlarged text", async () => {
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <SignupComposerView
          category="serving"
          eligibleError={null}
          eligibleLoading={false}
          isBusy={false}
          onRetryEligible={jest.fn()}
          onRetryPending={jest.fn()}
          onSubmit={jest.fn()}
          participants={[
            {
              displayName: "Eligible Member",
              membershipId: "membership-2",
            },
          ]}
          pendingCommand={null}
          primaryMembershipId="membership-1"
          result={null}
        />,
      );
    });

    expect(renderer.root.findAllByType(ScrollView)).toHaveLength(1);
    for (const text of renderer.root.findAllByType(Text)) {
      expect(text.props.allowFontScaling).not.toBe(false);
      expect(text.props.maxFontSizeMultiplier).toBeUndefined();
    }

    const kindGroup = renderer.root.findByProps({
      accessibilityRole: "radiogroup",
    });
    expect(StyleSheet.flatten(kindGroup.props.style).flexWrap).toBe("wrap");
  });

  it("keeps status textual in personal and roster presentation", async () => {
    const signup = createSignup();
    let roster!: ReturnType<typeof create>;
    await act(async () => {
      roster = create(
        <PendingRosterView
          error={null}
          isLoading={false}
          onRetry={jest.fn()}
          signups={[signup]}
        />,
      );
    });
    expect(readText(roster.root)).toContain("Status: Pending");

    let dates!: ReturnType<typeof create>;
    await act(async () => {
      dates = create(
        <OpenDatesView
          dates={[]}
          error={null}
          isLoading={false}
          onRetry={jest.fn()}
        />,
      );
    });
    const controls = dates.root.findAllByType(Pressable);
    expect(
      controls.every(
        (control) =>
          typeof control.props.accessibilityLabel === "string" &&
          typeof control.props.accessibilityRole === "string",
      ),
    ).toBe(true);
  });
});

function createSignup(): SignupResponse {
  return {
    category: "serving",
    helpNeedId: "need-1",
    id: "signup-1",
    kind: "individual",
    label: null,
    lastTransitionAt: null,
    memberParticipants: [],
    primaryContact: {
      displayName: "Primary Member",
      membershipId: "membership-1",
    },
    serviceDateId: "date-1",
    signupVersion: 1,
    status: "pending",
    submittedAt: "2026-08-17T10:00:00.000Z",
    totalParticipantCount: 1,
    unnamedParticipantCount: 0,
    version: 1,
    waitlistOrder: null,
  };
}

function readText(node: { children?: readonly unknown[] }): string {
  return (node.children ?? [])
    .flatMap((child) => {
      if (typeof child === "string") {
        return [child];
      }
      if (typeof child === "object" && child !== null && "children" in child) {
        return [readText(child as { children?: readonly unknown[] })];
      }
      return [];
    })
    .join(" ")
    .replace(/\s+/g, " ")
    .trim();
}
