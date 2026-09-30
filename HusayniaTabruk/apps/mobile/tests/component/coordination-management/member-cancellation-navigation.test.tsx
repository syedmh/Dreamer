import { describe, expect, it, jest } from "@jest/globals";
import type { ReactNode } from "react";
import { act, create } from "react-test-renderer";

import type {
  ServiceDateResponse,
  SignupResponse,
} from "../../../src/core/api/auth-api";
import { DateDetailView } from "../../../src/features/dates/DateDetailScreen";
import { MySignupsView } from "../../../src/features/signups/MySignupsScreen";

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  return {
    Link: (props: Record<string, unknown>) =>
      React.createElement("Link", props, props.children as ReactNode),
    useFocusEffect: jest.fn(),
    useLocalSearchParams: () => ({}),
  };
});

describe("member cancellation and management navigation", () => {
  it("integrates pre-deadline withdrawal into the authenticated member signup view", async () => {
    const signup = createSignup();
    const withdraw = jest.fn<() => Promise<void>>().mockResolvedValue();
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <MySignupsView
          cancellationManagement={{
            busySignupId: null,
            cancellationDeadlines: {
              "date-1": "2099-08-20T10:00:00.000Z",
            },
            confirmedWithdrawalIds: [],
            error: null,
            pendingOutcome: null,
            refresh: async () => undefined,
            withdraw,
          }}
          currentMembershipId="member-1"
          error={null}
          isLoading={false}
          onRetry={jest.fn()}
          pendingCommand={null}
          signups={[signup]}
        />,
      );
    });

    const begin = renderer.root.findByProps({
      accessibilityLabel: "Cancel my approved signup before the deadline",
    });
    await act(async () => {
      begin.props.onPress();
    });
    const confirm = renderer.root.findByProps({
      accessibilityLabel: "Confirm cancel my signup",
    });
    await act(async () => {
      confirm.props.onPress();
    });
    expect(withdraw).toHaveBeenCalledTimes(1);
  });

  it("provides an ownership-gated in-app entry to live management routing", async () => {
    const date = createDate();
    let manager!: ReturnType<typeof create>;
    await act(async () => {
      manager = create(
        <DateDetailView
          date={date}
          error={null}
          isFoodIncharge
          isLoading={false}
          membershipId="manager-1"
          onRetry={jest.fn()}
        />,
      );
    });
    expect(
      manager.root.findByProps({
        accessibilityLabel: "Manage service date Service day",
      }).props.href,
    ).toEqual({
      params: { dateId: "date-1" },
      pathname: "/(incharge)/manage/dates/[dateId]",
    });

    let unrelated!: ReturnType<typeof create>;
    await act(async () => {
      unrelated = create(
        <DateDetailView
          date={date}
          error={null}
          isFoodIncharge
          isLoading={false}
          membershipId="other-manager"
          onRetry={jest.fn()}
        />,
      );
    });
    expect(
      unrelated.root.findAllByProps({
        accessibilityLabel: "Manage service date Service day",
      }),
    ).toHaveLength(0);
  });
});

function createSignup(): SignupResponse {
  return {
    category: "serving",
    helpNeedId: "need-1",
    id: "signup-1",
    kind: "individual",
    label: null,
    lastTransitionAt: "2026-08-18T10:00:00.000Z",
    memberParticipants: [],
    primaryContact: {
      displayName: "Primary Member",
      membershipId: "member-1",
    },
    serviceDateId: "date-1",
    signupVersion: 5,
    status: "approved",
    submittedAt: "2026-08-17T10:00:00.000Z",
    totalParticipantCount: 1,
    unnamedParticipantCount: 0,
    version: 1,
    waitlistOrder: null,
  };
}

function createDate(): ServiceDateResponse {
  return {
    cancellationDeadlineAt: "2099-08-20T10:00:00.000Z",
    endsAt: "2099-08-21T12:00:00.000Z",
    helpNeeds: [],
    id: "date-1",
    instructions: "Arrive early",
    managerMembershipId: "manager-1",
    startsAt: "2099-08-21T10:00:00.000Z",
    status: "open",
    title: "Service day",
    version: 4,
  };
}
