import { describe, expect, it, jest } from "@jest/globals";
import { act, create } from "react-test-renderer";

import type {
  SignupResponse,
  T14Api,
} from "../../../src/core/api/auth-api";
import type { PendingSignupCommand } from "../../../src/core/security/pending-signup-storage";
import { MySignupsView } from "../../../src/features/signups/MySignupsScreen";
import { loadAllMySignups } from "../../../src/features/signups/useSignupSubmission";

const signup: SignupResponse = {
  category: "cleanup",
  helpNeedId: "need-1",
  id: "signup-1",
  kind: "household",
  label: "Household",
  lastTransitionAt: null,
  memberParticipants: [
    { displayName: "Adult Member", membershipId: "membership-2" },
  ],
  primaryContact: {
    displayName: "Primary Member",
    membershipId: "membership-1",
  },
  serviceDateId: "date-1",
  signupVersion: 1,
  status: "pending",
  submittedAt: "2026-08-17T10:00:00.000Z",
  totalParticipantCount: 3,
  unnamedParticipantCount: 1,
  version: 1,
  waitlistOrder: null,
};

const command: PendingSignupCommand = {
  body: {
    kind: "team",
    label: "Team",
    memberParticipantIds: [],
    unnamedParticipantCount: 1,
  },
  category: "serving",
  createdAt: "2026-08-17T10:00:00.000Z",
  helpNeedId: "need-2",
  idempotencyKey: "11111111-1111-4111-8111-111111111111",
  organizationId: "organization-1",
  primaryMembershipId: "membership-1",
  serviceDateId: "date-1",
  version: 1,
};

describe("My signups", () => {
  it("uses the exact empty state", async () => {
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <MySignupsView
          error={null}
          isLoading={false}
          onRetry={jest.fn()}
          pendingCommand={null}
          signups={[]}
        />,
      );
    });

    expect(readText(renderer.root)).toContain("No personal signups");
  });

  it("renders confirmed and local pending items with shared textual presentation", async () => {
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <MySignupsView
          error={null}
          isLoading={false}
          onRetry={jest.fn()}
          pendingCommand={command}
          signups={[signup]}
        />,
      );
    });
    const text = readText(renderer.root);

    expect(text).toContain("Status: Pending sync");
    expect(text).toContain("Status: Pending");
    expect(text).toContain("Adult Member");
    expect(text).not.toContain(command.idempotencyKey);
    expect(text).not.toContain("membership-2");
  });

  it("does not render Pending sync beside its matching authoritative signup", async () => {
    const matchingCommand: PendingSignupCommand = {
      ...command,
      category: signup.category,
      helpNeedId: signup.helpNeedId,
      serviceDateId: signup.serviceDateId,
    };
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <MySignupsView
          error={null}
          isLoading={false}
          onRetry={jest.fn()}
          pendingCommand={matchingCommand}
          signups={[signup]}
        />,
      );
    });

    const text = readText(renderer.root);
    expect(text).toContain("Status: Pending");
    expect(text).not.toContain("Status: Pending sync");
  });

  it("fully paginates and deduplicates authoritative personal signups", async () => {
    const api = {
      listMySignups: jest
        .fn<T14Api["listMySignups"]>()
        .mockResolvedValueOnce({
          items: [signup],
          nextCursor: "next",
        })
        .mockResolvedValueOnce({
          items: [{ ...signup, label: "Household 2" }],
          nextCursor: null,
        }),
    } as unknown as T14Api;

    await expect(
      loadAllMySignups(api, "access-token"),
    ).resolves.toEqual([{ ...signup, label: "Household 2" }]);
    expect(api.listMySignups).toHaveBeenNthCalledWith(
      2,
      "access-token",
      { cursor: "next", pageSize: 100 },
      undefined,
    );
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
