import { describe, expect, it, jest } from "@jest/globals";
import { act, create } from "react-test-renderer";

import type {
  HelpNeedResponse,
  RosterResponse,
  ServiceDateResponse,
  SignupResponse,
  T18Api,
} from "../../../src/core/api/auth-api";
import {
  AuthContext,
  type AuthContextValue,
} from "../../../src/features/auth/AuthSessionProvider";
import { useDateManagement } from "../../../src/features/admin/DateManagementScreen";
import { useRosterManagement } from "../../../src/features/roster/management/RosterManagementScreen";
import { useSignupCancellationManagement } from "../../../src/features/signups/management/useSignupCancellationManagement";

jest.mock("expo-router", () => ({
  Link: "Link",
  useFocusEffect: jest.fn(),
  useLocalSearchParams: () => ({ dateId: "date-1" }),
}));

describe("coordination management hooks", () => {
  it("executes date publish/edit/need/close/cancel workflows and blocks rapid duplicates", async () => {
    const draft = createDate("draft", 4);
    const edited = { ...draft, title: "Edited", version: 5 };
    const opened = createDate("open", 6);
    const closed = createDate("closed", 7);
    const cancelled = createDate("cancelled", 8);
    const editedNeed = { ...draft.helpNeeds[0]!, version: 3 };
    const getServiceDate = jest
      .fn<(...args: unknown[]) => Promise<ServiceDateResponse>>()
      .mockResolvedValueOnce(draft)
      .mockResolvedValueOnce(edited)
      .mockResolvedValueOnce(opened)
      .mockResolvedValueOnce(opened)
      .mockResolvedValueOnce(closed)
      .mockResolvedValue(cancelled);
    let resolvePublish!: (value: ServiceDateResponse) => void;
    const api = createApi({
      cancelServiceDate: resolved(cancelled),
      closeServiceDate: resolved(closed),
      editHelpNeed: resolved(editedNeed),
      editServiceDate: resolved(edited),
      getServiceDate,
      openServiceDate: jest.fn(
        () =>
          new Promise<ServiceDateResponse>((resolve) => {
            resolvePublish = resolve;
          }),
      ),
    });
    let state!: ReturnType<typeof useDateManagement>;
    const renderer = await renderHook(() => {
      state = useDateManagement("date-1", api);
    });

    await act(async () => {
      await state.refresh();
    });
    act(() => state.setValue("title", "Edited"));
    await act(async () => {
      await state.save();
    });
    expect(api.editServiceDate).toHaveBeenCalledWith(
      "access-token",
      "date-1",
      4,
      expect.objectContaining({ title: "Edited" }),
      expect.any(AbortSignal),
    );

    let firstPublish!: Promise<void>;
    await act(async () => {
      firstPublish = state.publish();
      await state.publish();
    });
    expect(api.openServiceDate).toHaveBeenCalledTimes(1);
    resolvePublish(opened);
    await act(async () => {
      await firstPublish;
    });

    await act(async () => {
      await state.editNeed(draft.helpNeeds[0]!, {
        capacity: 2,
        instructions: "Updated need",
        status: "closed",
      });
    });
    expect(api.editHelpNeed).toHaveBeenCalledWith(
      "access-token",
      "need-1",
      2,
      {
        capacity: 2,
        instructions: "Updated need",
        status: "closed",
      },
      expect.any(AbortSignal),
    );

    act(() => state.setConfirmation("close"));
    await act(async () => {
      await state.confirm("Coordination complete");
    });
    expect(api.closeServiceDate).toHaveBeenCalledWith(
      "access-token",
      "date-1",
      expect.any(String),
      6,
      { reason: "Coordination complete" },
      expect.any(AbortSignal),
    );

    act(() => state.setConfirmation("cancel"));
    await act(async () => {
      await state.confirm("Weather");
    });
    expect(api.cancelServiceDate).toHaveBeenCalledWith(
      "access-token",
      "date-1",
      expect.any(String),
      7,
      { reason: "Weather" },
      expect.any(AbortSignal),
    );
    act(() => renderer.unmount());
  });

  it("executes every roster mutation with selected signup versions and reasons", async () => {
    const pending = createSignup("pending-1", "pending", 5);
    const waitlisted = createSignup("waitlisted-1", "waitlisted", 6);
    const approved = createSignup("approved-1", "approved", 7);
    const api = createApi({
      approveSignup: resolved({
        ...pending,
        status: "approved",
      } satisfies SignupResponse),
      declineSignup: resolved({
        ...pending,
        status: "declined",
      } satisfies SignupResponse),
      getManagedRoster: resolved({
        items: [pending, waitlisted, approved],
        nextCursor: null,
        serviceDateId: "date-1",
      } satisfies RosterResponse),
      getServiceDate: resolved(createDate("open", 4)),
      overrideSignupCancellation: resolved({
        ...approved,
        status: "cancelled",
      } satisfies SignupResponse),
      reassignWaitlistedSignup: resolved({
        ...waitlisted,
        status: "approved",
      } satisfies SignupResponse),
      waitlistSignup: resolved({
        ...pending,
        status: "waitlisted",
      } satisfies SignupResponse),
      withdrawSignup: resolved({
        ...approved,
        status: "withdrawn",
      } satisfies SignupResponse),
    });
    let state!: ReturnType<typeof useRosterManagement>;
    const renderer = await renderHook(() => {
      state = useRosterManagement("date-1", api);
    });
    await act(async () => {
      await state.refresh();
    });

    act(() => state.setActionReason("Operational reason"));
    await act(async () => {
      await state.decide(pending, "approve");
      await state.decide(pending, "decline");
      await state.decide(pending, "waitlist");
      await state.reassign(waitlisted);
      await state.override(approved);
      await state.withdraw(approved);
    });

    expect(api.approveSignup).toHaveBeenCalledWith(
      "access-token",
      "pending-1",
      expect.any(String),
      5,
      { reason: "Operational reason" },
      expect.any(AbortSignal),
    );
    expect(api.declineSignup).toHaveBeenCalledWith(
      "access-token",
      "pending-1",
      expect.any(String),
      5,
      { reason: "Operational reason" },
      expect.any(AbortSignal),
    );
    expect(api.waitlistSignup).toHaveBeenCalledWith(
      "access-token",
      "pending-1",
      expect.any(String),
      5,
      { reason: "Operational reason" },
      expect.any(AbortSignal),
    );
    expect(api.reassignWaitlistedSignup).toHaveBeenCalledWith(
      "access-token",
      "need-1",
      expect.any(String),
      6,
      {
        reason: "Operational reason",
        signupId: "waitlisted-1",
      },
      expect.any(AbortSignal),
    );
    expect(api.overrideSignupCancellation).toHaveBeenCalledWith(
      "access-token",
      "approved-1",
      expect.any(String),
      7,
      {
        reason: "Operational reason",
        targetState: "cancelled",
      },
      expect.any(AbortSignal),
    );
    expect(api.withdrawSignup).toHaveBeenCalledWith(
      "access-token",
      "approved-1",
      expect.any(String),
      7,
      {},
      expect.any(AbortSignal),
    );
    act(() => renderer.unmount());
  });

  it("retries an unknown override with the same key, body, and ETag after refresh", async () => {
    const approved = createSignup("approved-1", "approved", 7);
    const timeout = new Error("The request timed out.");
    timeout.name = "TimeoutError";
    const override = jest
      .fn<(...args: unknown[]) => Promise<SignupResponse>>()
      .mockRejectedValueOnce(timeout)
      .mockResolvedValueOnce({
        ...approved,
        status: "cancelled",
      } satisfies SignupResponse);
    const api = createApi({
      getManagedRoster: resolved({
        items: [approved],
        nextCursor: null,
        serviceDateId: "date-1",
      } satisfies RosterResponse),
      getServiceDate: resolved(createDate("open", 4)),
      overrideSignupCancellation: override,
    });
    let state!: ReturnType<typeof useRosterManagement>;
    const renderer = await renderHook(() => {
      state = useRosterManagement("date-1", api);
    });
    await act(async () => {
      await state.refresh();
    });
    act(() => state.setActionReason("Original reason"));
    await act(async () => {
      await state.override(approved);
    });
    await act(async () => {
      await state.refresh();
    });
    act(() => state.setActionReason("Changed reason"));
    await act(async () => {
      await state.override({ ...approved, signupVersion: 8 });
    });

    const first = override.mock.calls[0]!;
    const second = override.mock.calls[1]!;
    expect(second[2]).toBe(first[2]);
    expect(second[3]).toBe(7);
    expect(second[4]).toEqual({
      reason: "Original reason",
      targetState: "cancelled",
    });
    act(() => renderer.unmount());
  });

  it("closes the post-success member withdrawal duplicate window", async () => {
    const approved = createSignup("approved-1", "approved", 7);
    const withdrawSignup = resolved({
      ...approved,
      status: "withdrawn",
    } satisfies SignupResponse);
    const api = createApi({ withdrawSignup });
    let state!: ReturnType<typeof useSignupCancellationManagement>;
    const renderer = await renderHook(() => {
      state = useSignupCancellationManagement([approved], api);
    });

    await act(async () => {
      await state.withdraw(approved);
      await state.withdraw(approved);
    });

    expect(withdrawSignup).toHaveBeenCalledTimes(1);
    expect(state.confirmedWithdrawalIds).toEqual(["approved-1"]);
    act(() => renderer.unmount());
  });
});

async function renderHook(render: () => void): Promise<ReturnType<typeof create>> {
  const context: AuthContextValue = {
    clearError: jest.fn(),
    error: null,
    isSigningIn: false,
    isSigningOut: false,
    runAuthenticatedRequest: async <T,>(
      request: (accessToken: string) => Promise<T>,
    ) => request("access-token"),
    session: {
      actor: {
        membership: {
          displayName: "Manager",
          eligibleAsNamedParticipant: true,
          id: "manager-1",
        },
        organization: {
          id: "organization-1",
          name: "Husaynia",
          timeZone: "America/Los_Angeles",
        },
        roles: ["Member", "FoodIncharge"],
      },
    },
    signIn: async () => undefined,
    signOut: async () => undefined,
    status: "authenticated",
  };
  function Harness() {
    render();
    return null;
  }
  let renderer!: ReturnType<typeof create>;
  await act(async () => {
    renderer = create(
      <AuthContext.Provider value={context}>
        <Harness />
      </AuthContext.Provider>,
    );
  });
  return renderer;
}

function createApi(
  overrides: Partial<Record<keyof T18Api, unknown>>,
): T18Api {
  return {
    approveSignup: jest.fn(),
    cancelServiceDate: jest.fn(),
    closeServiceDate: jest.fn(),
    declineSignup: jest.fn(),
    editHelpNeed: jest.fn(),
    editServiceDate: jest.fn(),
    getManagedRoster: jest.fn(),
    getServiceDate: jest.fn(),
    listEligibleSignupParticipants: jest.fn(),
    listMySignups: jest.fn(),
    listOpenServiceDates: jest.fn(),
    openServiceDate: jest.fn(),
    overrideSignupCancellation: jest.fn(),
    reassignWaitlistedSignup: jest.fn(),
    submitSignup: jest.fn(),
    waitlistSignup: jest.fn(),
    withdrawSignup: jest.fn(),
    ...overrides,
  } as unknown as T18Api;
}

function resolved<T>(value: T) {
  return jest
    .fn<(...args: unknown[]) => Promise<T>>()
    .mockResolvedValue(value);
}

function createDate(
  status: ServiceDateResponse["status"],
  version: number,
): ServiceDateResponse {
  return {
    cancellationDeadlineAt: "2026-08-20T10:00:00.000Z",
    endsAt: "2026-08-21T12:00:00.000Z",
    helpNeeds: [
      {
        availability: 3,
        category: "serving",
        id: "need-1",
        instructions: "Serve food",
        status: "open",
        version: 2,
      } satisfies HelpNeedResponse,
    ],
    id: "date-1",
    instructions: "Arrive early",
    managerMembershipId: "manager-1",
    startsAt: "2026-08-21T10:00:00.000Z",
    status,
    title: "Service day",
    version,
  };
}

function createSignup(
  id: string,
  status: SignupResponse["status"],
  signupVersion: number,
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
    signupVersion,
    status,
    submittedAt: "2026-08-17T10:00:00.000Z",
    totalParticipantCount: 1,
    unnamedParticipantCount: 0,
    version: 1,
    waitlistOrder: status === "waitlisted" ? 1 : null,
  };
}
