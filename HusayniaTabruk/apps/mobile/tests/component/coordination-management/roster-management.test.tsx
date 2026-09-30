import { describe, expect, it, jest } from "@jest/globals";
import { ScrollView } from "react-native";
import { act, create } from "react-test-renderer";

import type {
  ServiceDateResponse,
  SignupResponse,
} from "../../../src/core/api/auth-api";
import { RosterManagementView } from "../../../src/features/roster/management/RosterManagementScreen";
import { SignupCancellationPanel } from "../../../src/features/signups/management/SignupCancellationPanel";

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

describe("roster coordination management", () => {
  it("renders privacy-safe decisions, selected reassignment, and non-color status", async () => {
    const pending = createSignup("pending-1", "pending");
    const waitlisted = {
      ...createSignup("waitlisted-1", "waitlisted"),
      waitlistOrder: 3,
    };
    const privateFixture = {
      ...pending,
      email: "private@example.test",
      phone: "+1-555-0100",
      primaryContact: {
        ...pending.primaryContact,
        email: "primary@example.test",
      },
    } as unknown as SignupResponse;

    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <RosterManagementView
          actionReason="Operational reason"
          busyAction={null}
          date={date}
          error={null}
          onActionReasonChange={jest.fn()}
          onDecision={jest.fn()}
          onOverride={jest.fn()}
          onReassign={jest.fn()}
          onRefresh={jest.fn()}
          now={new Date("2026-08-20T10:00:00.000Z")}
          pendingOutcome={null}
          signups={[privateFixture, waitlisted]}
        />,
      );
    });

    expect(renderer.root.findAllByType(ScrollView)).toHaveLength(1);
    const text = readText(renderer.root);
    expect(text).toContain("Status: Pending");
    expect(text).toContain("Waitlist position: 3");
    expect(text).not.toContain("private@example.test");
    expect(text).not.toContain("primary@example.test");
    expect(text).not.toContain("+1-555-0100");
    expect(
      renderer.root.findByProps({
        accessibilityLabel: "Approve signup for Primary Member",
      }),
    ).toBeDefined();
    expect(
      renderer.root.findByProps({
        accessibilityLabel: "Decline signup for Primary Member",
      }),
    ).toBeDefined();
    expect(
      renderer.root.findByProps({
        accessibilityLabel: "Waitlist signup for Primary Member",
      }),
    ).toBeDefined();
    expect(
      renderer.root.findByProps({
        accessibilityLabel: "Reassign place to Primary Member",
      }),
    ).toBeDefined();
  });

  it("requires an override reason and prevents duplicate taps", async () => {
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <RosterManagementView
          actionReason=""
          busyAction="override:approved-1"
          date={date}
          error={null}
          onActionReasonChange={jest.fn()}
          onDecision={jest.fn()}
          onOverride={jest.fn()}
          onReassign={jest.fn()}
          onRefresh={jest.fn()}
          now={new Date("2026-08-20T10:00:00.000Z")}
          pendingOutcome={null}
          signups={[createSignup("approved-1", "approved")]}
        />,
      );
    });

    const override = renderer.root.findByProps({
      accessibilityLabel: "Override cancellation deadline for Primary Member",
    });
    expect(override.props.accessibilityState).toMatchObject({
      busy: true,
      disabled: true,
    });
    expect(readText(renderer.root)).toContain(
      "Override reason is required and will be recorded.",
    );
  });

  it("shows exact before/after deadline self-cancellation behavior", async () => {
    const signup = createSignup("approved-1", "approved");
    let before!: ReturnType<typeof create>;
    await act(async () => {
      before = create(
        <SignupCancellationPanel
          busy={false}
          cancellationDeadlineAt={date.cancellationDeadlineAt}
          currentMembershipId="member-1"
          error={null}
          now={new Date("2026-08-20T09:59:59.000Z")}
          onWithdraw={jest.fn()}
          pendingOutcome={null}
          signup={signup}
        />,
      );
    });
    expect(
      before.root.findByProps({
        accessibilityLabel: "Cancel my approved signup before the deadline",
      }),
    ).toBeDefined();

    let after!: ReturnType<typeof create>;
    await act(async () => {
      after = create(
        <SignupCancellationPanel
          busy={false}
          cancellationDeadlineAt={date.cancellationDeadlineAt}
          currentMembershipId="member-1"
          error={null}
          now={new Date("2026-08-20T10:00:00.000Z")}
          onWithdraw={jest.fn()}
          pendingOutcome={null}
          signup={signup}
        />,
      );
    });
    expect(readText(after.root)).toContain(
      "The self-cancellation deadline has passed. Contact the Food Incharge.",
    );
  });
});

function createSignup(
  id: string,
  status: SignupResponse["status"],
): SignupResponse {
  return {
    category: "serving",
    helpNeedId: "need-1",
    id,
    kind: "individual",
    label: null,
    lastTransitionAt: null,
    memberParticipants: [],
    primaryContact: {
      displayName: "Primary Member",
      membershipId: "member-1",
    },
    serviceDateId: "date-1",
    signupVersion: 5,
    status,
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
