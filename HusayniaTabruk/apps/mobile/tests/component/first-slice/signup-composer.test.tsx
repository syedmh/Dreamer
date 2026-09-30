import { describe, expect, it, jest } from "@jest/globals";
import { TextInput } from "react-native";
import { act, create } from "react-test-renderer";

import type { SignupResponse } from "../../../src/core/api/auth-api";
import type { PendingSignupCommand } from "../../../src/core/security/pending-signup-storage";
import { SignupComposerView } from "../../../src/features/signups/SignupComposerScreen";
import { loadAllEligibleParticipants } from "../../../src/features/signups/useEligibleParticipants";

jest.mock("expo-router", () => ({
  useFocusEffect: jest.fn(),
  useLocalSearchParams: () => ({}),
}));

const pendingCommand: PendingSignupCommand = {
  body: {
    kind: "team",
    label: "Team 1",
    memberParticipantIds: ["membership-2"],
    unnamedParticipantCount: 0,
  },
  category: "serving",
  createdAt: "2026-08-17T10:00:00.000Z",
  helpNeedId: "need-1",
  idempotencyKey: "11111111-1111-4111-8111-111111111111",
  organizationId: "organization-1",
  primaryMembershipId: "membership-1",
  serviceDateId: "date-1",
  version: 1,
};

const signup: SignupResponse = {
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

describe("signup composer", () => {
  it("submits the individual composition and exposes no arbitrary text input", async () => {
    const onSubmit = jest.fn();
    const renderer = await createComposer({ onSubmit });

    expect(renderer.root.findAllByType(TextInput)).toHaveLength(0);

    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Submit signup request" })
        .props.onPress();
    });

    expect(onSubmit).toHaveBeenCalledWith({
      kind: "individual",
      label: null,
      memberParticipantIds: [],
      unnamedParticipantCount: 0,
    });
  });

  it("supports household member selection, count bounds, and preset labels", async () => {
    const onSubmit = jest.fn();
    const renderer = await createComposer({ onSubmit });

    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Household signup" })
        .props.onPress();
    });
    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Include Eligible Member" })
        .props.onPress();
    });
    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Generic label Household" })
        .props.onPress();
    });
    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Increase generic label number" })
        .props.onPress();
    });

    const checkbox = renderer.root.findByProps({
      accessibilityLabel: "Include Eligible Member",
    });
    expect(checkbox.props.accessibilityState.checked).toBe(true);

    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Submit signup request" })
        .props.onPress();
    });

    expect(onSubmit).toHaveBeenCalledWith({
      kind: "household",
      label: "Household 1",
      memberParticipantIds: ["membership-2"],
      unnamedParticipantCount: 0,
    });
  });

  it("distinguishes authoritative Pending from Pending sync", async () => {
    const pendingRenderer = await createComposer({
      pendingCommand,
      result: {
        command: pendingCommand,
        message: "Signup is Pending sync.",
        type: "pendingSync",
      },
    });
    expect(readText(pendingRenderer.root)).toContain("Status: Pending sync");

    const confirmedRenderer = await createComposer({
      result: { signup, type: "confirmed" },
    });
    expect(readText(confirmedRenderer.root)).toContain("Pending");
    expect(readText(confirmedRenderer.root)).not.toContain("Pending sync");
  });

  it("exercises team composition with an unnamed participant", async () => {
    const onSubmit = jest.fn();
    const renderer = await createComposer({ onSubmit });

    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Team signup" })
        .props.onPress();
    });
    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Increase unnamed participants" })
        .props.onPress();
    });
    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Submit signup request" })
        .props.onPress();
    });

    expect(onSubmit).toHaveBeenCalledWith({
      kind: "team",
      label: null,
      memberParticipantIds: [],
      unnamedParticipantCount: 1,
    });
  });

  it("operates at the 25-person UI maximum without allowing a 21st member", async () => {
    const onSubmit = jest.fn();
    const participants = Array.from({ length: 21 }, (_, index) => ({
      displayName: `Eligible Member ${index + 1}`,
      membershipId: `membership-${index + 2}`,
    }));
    const renderer = await createComposer({ onSubmit, participants });

    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Team signup" })
        .props.onPress();
    });
    await act(async () => {
      for (const participant of participants.slice(0, 20)) {
        renderer.root
          .findByProps({
            accessibilityLabel: `Include ${participant.displayName}`,
          })
          .props.onPress();
      }
    });

    expect(
      renderer.root.findByProps({
        accessibilityLabel: "Include Eligible Member 21",
      }).props.accessibilityState,
    ).toMatchObject({ checked: false, disabled: true });

    await act(async () => {
      const increment = renderer.root.findByProps({
        accessibilityLabel: "Increase unnamed participants",
      });
      increment.props.onPress();
      increment.props.onPress();
      increment.props.onPress();
      increment.props.onPress();
    });

    expect(readText(renderer.root)).toContain("Total participants: 25");
    expect(
      renderer.root.findByProps({
        accessibilityLabel: "Increase unnamed participants",
      }).props.accessibilityState.disabled,
    ).toBe(true);

    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Submit signup request" })
        .props.onPress();
    });

    expect(onSubmit).toHaveBeenCalledWith({
      kind: "team",
      label: null,
      memberParticipantIds: participants
        .slice(0, 20)
        .map((participant) => participant.membershipId),
      unnamedParticipantCount: 4,
    });
  });

  it("fully paginates and deduplicates eligible members", async () => {
    const loadPage = jest
      .fn<
        (
          cursor: string | undefined,
          signal?: AbortSignal,
        ) => Promise<{
          items: {
            displayName: string;
            membershipId: string;
          }[];
          nextCursor?: string | null;
        }>
      >()
      .mockResolvedValueOnce({
        items: [
          { displayName: "Member A", membershipId: "membership-a" },
        ],
        nextCursor: "next",
      })
      .mockResolvedValueOnce({
        items: [
          { displayName: "Member A updated", membershipId: "membership-a" },
          { displayName: "Member B", membershipId: "membership-b" },
        ],
        nextCursor: null,
      });

    await expect(loadAllEligibleParticipants(loadPage)).resolves.toEqual([
      { displayName: "Member A updated", membershipId: "membership-a" },
      { displayName: "Member B", membershipId: "membership-b" },
    ]);
  });
});

async function createComposer(
  overrides: Partial<React.ComponentProps<typeof SignupComposerView>> = {},
): Promise<ReturnType<typeof create>> {
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
        {...overrides}
      />,
    );
  });
  return renderer;
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
