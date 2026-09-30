import { describe, expect, it, jest } from "@jest/globals";
import type { ReactNode } from "react";
import { act, create } from "react-test-renderer";

import InchargeLayout from "../../../app/(incharge)/_layout";
import type {
  RosterResponse,
  SignupResponse,
} from "../../../src/core/api/auth-api";
import {
  AuthContext,
  type AuthContextValue,
} from "../../../src/features/auth/AuthSessionProvider";
import { PendingRosterView } from "../../../src/features/roster/PendingRosterScreen";
import { loadPendingRoster } from "../../../src/features/roster/roster-use-cases";

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  const Stack = (props: Record<string, unknown>) =>
    React.createElement("Stack", props, props.children as ReactNode);
  return {
    Redirect: (props: Record<string, unknown>) =>
      React.createElement("Redirect", props),
    Stack,
    useFocusEffect: jest.fn(),
    useLocalSearchParams: () => ({}),
  };
});

const pending = createSignup("signup-pending", "pending");
const approved = createSignup("signup-approved", "approved");

describe("routing and pending roster", () => {
  it("gates unauthenticated and non-Food-Incharge navigation", async () => {
    const unauthenticated = await renderLayout({
      session: null,
      status: "unauthenticated",
    });
    expect(unauthenticated.root.findByProps({ href: "/(auth)" })).toBeDefined();

    const member = await renderLayout({
      session: {
        actor: {
          membership: {
            displayName: "Member",
            eligibleAsNamedParticipant: true,
            id: "membership-1",
          },
          organization: {
            id: "organization-1",
            name: "Husaynia",
            timeZone: "America/Los_Angeles",
          },
          roles: ["Member"],
        },
      },
      status: "authenticated",
    });
    expect(member.root.findByProps({ href: "/(member)" })).toBeDefined();
  });

  it("allows the Food Incharge route while leaving server authorization authoritative", async () => {
    const renderer = await renderLayout({
      session: {
        actor: {
          membership: {
            displayName: "Incharge",
            eligibleAsNamedParticipant: true,
            id: "membership-1",
          },
          organization: {
            id: "organization-1",
            name: "Husaynia",
            timeZone: "America/Los_Angeles",
          },
          roles: ["Member", "FoodIncharge"],
        },
      },
      status: "authenticated",
    });

    expect(
      renderer.root.find((node) => String(node.type) === "Stack"),
    ).toBeDefined();
  });

  it("fully paginates, deduplicates, and keeps only pending roster items", async () => {
    const loadPage = jest
      .fn<
        (
          cursor: string | undefined,
          signal?: AbortSignal,
        ) => Promise<RosterResponse>
      >()
      .mockResolvedValueOnce({
        items: [pending, approved],
        nextCursor: "next",
        serviceDateId: "date-1",
      })
      .mockResolvedValueOnce({
        items: [{ ...pending, primaryContact: { ...pending.primaryContact, displayName: "Updated" } }],
        nextCursor: null,
        serviceDateId: "date-1",
      });

    await expect(loadPendingRoster(loadPage)).resolves.toEqual([
      {
        ...pending,
        primaryContact: {
          ...pending.primaryContact,
          displayName: "Updated",
        },
      },
    ]);
  });

  it("uses the exact empty state and renders no contact data", async () => {
    let empty!: ReturnType<typeof create>;
    await act(async () => {
      empty = create(
        <PendingRosterView
          error={null}
          isLoading={false}
          onRetry={jest.fn()}
          signups={[]}
        />,
      );
    });
    expect(readText(empty.root)).toContain("No pending signup requests");

    const privacyNegativeFixture = {
      ...pending,
      email: "private@example.test",
      phone: "+1-555-0100",
      memberParticipants: [
        {
          displayName: "Eligible Adult",
          email: "adult@example.test",
          membershipId: "membership-2",
          phone: "+1-555-0101",
        },
      ],
      primaryContact: {
        ...pending.primaryContact,
        email: "primary@example.test",
        phone: "+1-555-0102",
      },
    } as unknown as SignupResponse;
    let populated!: ReturnType<typeof create>;
    await act(async () => {
      populated = create(
        <PendingRosterView
          error={null}
          isLoading={false}
          onRetry={jest.fn()}
          signups={[privacyNegativeFixture]}
        />,
      );
    });
    const text = readText(populated.root);
    expect(text).toContain("Status: Pending");
    expect(text).not.toContain("private@example.test");
    expect(text).not.toContain("adult@example.test");
    expect(text).not.toContain("primary@example.test");
    expect(text).not.toContain("+1-555-0100");
    expect(text).not.toContain("+1-555-0101");
    expect(text).not.toContain("+1-555-0102");
  });
});

async function renderLayout(
  overrides: Pick<AuthContextValue, "session" | "status">,
): Promise<ReturnType<typeof create>> {
  const value: AuthContextValue = {
    clearError: jest.fn(),
    error: null,
    isSigningIn: false,
    isSigningOut: false,
    runAuthenticatedRequest: async <T,>(
      request: (accessToken: string) => Promise<T>,
    ) => request("access-token"),
    signIn: async () => undefined,
    signOut: async () => undefined,
    ...overrides,
  };

  let renderer!: ReturnType<typeof create>;
  await act(async () => {
    renderer = create(
      <AuthContext.Provider value={value}>
        <InchargeLayout />
      </AuthContext.Provider>,
    );
  });
  return renderer;
}

function createSignup(
  id: string,
  status: SignupResponse["status"],
): SignupResponse {
  return {
    category: "serving",
    helpNeedId: "need-1",
    id,
    kind: "team",
    label: "Team",
    lastTransitionAt: null,
    memberParticipants: [],
    primaryContact: {
      displayName: "Primary Member",
      membershipId: "membership-1",
    },
    serviceDateId: "date-1",
    signupVersion: 1,
    status,
    submittedAt: "2026-08-17T10:00:00.000Z",
    totalParticipantCount: 2,
    unnamedParticipantCount: 1,
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
