import { describe, expect, it, jest } from "@jest/globals";
import type { ReactNode } from "react";
import { act, create } from "react-test-renderer";

import type { ServiceDateResponse } from "../../../src/core/api/auth-api";
import {
  DateDetailView,
} from "../../../src/features/dates/DateDetailScreen";
import {
  OpenDatesView,
} from "../../../src/features/dates/OpenDatesScreen";

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  return {
    Link: (props: Record<string, unknown>) =>
      React.createElement("Link", props, props.children as ReactNode),
    useFocusEffect: jest.fn(),
    useLocalSearchParams: () => ({}),
  };
});

const serviceDate: ServiceDateResponse = {
  cancellationDeadlineAt: "2026-08-20T10:00:00.000Z",
  endsAt: "2026-08-21T12:00:00.000Z",
  helpNeeds: [
    {
      availability: 3,
      category: "serving",
      id: "need-1",
      instructions: "Serve respectfully",
      status: "open",
      version: 1,
    },
  ],
  id: "date-1",
  instructions: "Use the side entrance",
  managerMembershipId: "membership-1",
  startsAt: "2026-08-21T10:00:00.000Z",
  status: "open",
  title: "Thursday service",
  version: 1,
};

describe("first-slice dates", () => {
  it("distinguishes no published dates from no matching help", async () => {
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <OpenDatesView
          dates={[]}
          error={null}
          isLoading={false}
          onRetry={jest.fn()}
        />,
      );
    });

    expect(readText(renderer.root)).toContain("No published dates");

    await act(async () => {
      renderer.update(
        <OpenDatesView
          dates={[serviceDate]}
          error={null}
          isLoading={false}
          onRetry={jest.fn()}
        />,
      );
    });
    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Filter dates by Cleanup" })
        .props.onPress();
    });

    expect(readText(renderer.root)).toContain("No matching help requested");
    expect(readText(renderer.root)).not.toContain("No published dates");
  });

  it("renders AC-3 detail, signup link, and only the managed-date roster link", async () => {
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <DateDetailView
          date={serviceDate}
          error={null}
          isFoodIncharge
          isLoading={false}
          membershipId="membership-1"
          onRetry={jest.fn()}
        />,
      );
    });
    const text = readText(renderer.root);

    expect(text).toContain("Thursday service");
    expect(text).toContain("Use the side entrance");
    expect(text).toContain("Serving");
    expect(text).toContain("Availability: 3 places");
    expect(text).toContain("Cancellation deadline:");
    expect(
      renderer.root.findByProps({
        accessibilityLabel: "Sign up for Serving",
      }),
    ).toBeDefined();
    expect(
      renderer.root.findByProps({
        accessibilityLabel: "View pending roster for Thursday service",
      }),
    ).toBeDefined();

    let unrelated!: ReturnType<typeof create>;
    await act(async () => {
      unrelated = create(
        <DateDetailView
          date={serviceDate}
          error={null}
          isFoodIncharge
          isLoading={false}
          membershipId="other-membership"
          onRetry={jest.fn()}
        />,
      );
    });
    expect(
      unrelated.root.findAllByProps({
        accessibilityLabel: "View pending roster for Thursday service",
      }),
    ).toHaveLength(0);
  });
});

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
