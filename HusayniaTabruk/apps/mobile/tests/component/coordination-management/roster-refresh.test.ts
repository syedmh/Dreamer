import { describe, expect, it, jest } from "@jest/globals";

import type {
  RosterResponse,
  SignupResponse,
  T18Api,
} from "../../../src/core/api/auth-api";
import { loadManagedRoster } from "../../../src/features/roster/management/roster-management-use-cases";

describe("managed roster refresh", () => {
  it("fully paginates, deduplicates updated signup state, and rejects cursor loops", async () => {
    const pending = createSignup("signup-1", "pending");
    const approved = { ...pending, signupVersion: 6, status: "approved" as const };
    const api = {
      getManagedRoster: jest
        .fn<
          (
            accessToken: string,
            dateId: string,
            query?: { cursor?: string; pageSize?: number },
            signal?: AbortSignal,
          ) => Promise<RosterResponse>
        >()
        .mockResolvedValueOnce({
          items: [pending],
          nextCursor: "next",
          serviceDateId: "date-1",
        })
        .mockResolvedValueOnce({
          items: [approved],
          nextCursor: null,
          serviceDateId: "date-1",
        }),
    } as unknown as T18Api;

    await expect(
      loadManagedRoster(api, "access-token", "date-1"),
    ).resolves.toEqual([approved]);

    const loopingApi = {
      getManagedRoster: jest
        .fn<() => Promise<RosterResponse>>()
        .mockResolvedValue({
          items: [],
          nextCursor: "same",
          serviceDateId: "date-1",
        }),
    } as unknown as T18Api;
    await expect(
      loadManagedRoster(loopingApi, "access-token", "date-1"),
    ).rejects.toThrow("The managed roster cursor repeated.");
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
