import { describe, expect, it, jest } from "@jest/globals";

import {
  ApiClientError,
  GeneratedApiClient,
} from "../../../src/core/api/generated/api-contract-client";
import { GeneratedAuthApi } from "../../../src/core/api/auth-api";

const validDate = {
  cancellationDeadlineAt: "2026-08-20T10:00:00.000Z",
  endsAt: "2026-08-21T12:00:00.000Z",
  helpNeeds: [],
  id: "date/with space",
  instructions: "Arrive early",
  managerMembershipId: "manager-1",
  startsAt: "2026-08-21T10:00:00.000Z",
  status: "open" as const,
  title: "Service day",
  version: 1,
};

const validSignup = {
  category: "serving" as const,
  helpNeedId: "need-1",
  id: "signup-1",
  kind: "individual" as const,
  label: null,
  lastTransitionAt: null,
  memberParticipants: [],
  primaryContact: {
    displayName: "Primary Member",
    membershipId: "member-1",
  },
  serviceDateId: "date-1",
  signupVersion: 1,
  status: "pending" as const,
  submittedAt: "2026-08-17T10:00:00.000Z",
  totalParticipantCount: 1,
  unnamedParticipantCount: 0,
  version: 1,
  waitlistOrder: null,
};

describe("GeneratedApiClient", () => {
  it("generates auth, coordination, and T19 thread methods", () => {
    const methods = Object.getOwnPropertyNames(GeneratedApiClient.prototype);

    expect(methods).toEqual([
      "constructor",
      "login",
      "refreshSession",
      "logoutSession",
      "getCurrentActor",
      "listOpenServiceDates",
      "getServiceDate",
      "editServiceDate",
      "openServiceDate",
      "listEligibleSignupParticipants",
      "submitSignup",
      "listMySignups",
      "getManagedRoster",
      "closeServiceDate",
      "cancelServiceDate",
      "editHelpNeed",
      "withdrawSignup",
      "approveSignup",
      "declineSignup",
      "waitlistSignup",
      "overrideSignupCancellation",
      "reassignWaitlistedSignup",
      "listThreadMessages",
      "postThreadMessage",
      "reportThreadMessage",
      "hideThreadMessage",
      "lockThread",
      "readPrivilegedThreadMessages",
    ]);
  });

  it("encodes paths and query values and propagates AbortSignal", async () => {
    const fetcher = jest.fn<typeof fetch>();
    fetcher
      .mockResolvedValueOnce(jsonResponse(200, validDate))
      .mockResolvedValueOnce(jsonResponse(200, { items: [], nextCursor: null }));
    const client = new GeneratedApiClient({
      baseUrl: "https://api.example.test",
      fetch: fetcher,
    });
    const controller = new AbortController();

    await client.getServiceDate("date/with space", {
      signal: controller.signal,
    });
    await client.listMySignups({
      cursor: "cursor/with space",
      pageSize: 25,
    });

    expect(fetcher.mock.calls[0]?.[0]).toBe(
      "https://api.example.test/api/v1/dates/date%2Fwith%20space",
    );
    expect(fetcher.mock.calls[0]?.[1]?.signal).toBe(controller.signal);
    expect(fetcher.mock.calls[1]?.[0]).toBe(
      "https://api.example.test/api/v1/signups/mine?cursor=cursor%2Fwith%20space&pageSize=25",
    );
  });

  it("emits the required idempotency header and validates referenced schemas", async () => {
    const fetcher = jest
      .fn<typeof fetch>()
      .mockResolvedValueOnce(jsonResponse(201, validSignup));
    const client = new GeneratedApiClient({
      baseUrl: "https://api.example.test",
      fetch: fetcher,
    });

    await expect(
      client.submitSignup(
        "need/one",
        "11111111-1111-4111-8111-111111111111",
        {
          kind: "individual",
          label: null,
          memberParticipantIds: [],
          unnamedParticipantCount: 0,
        },
      ),
    ).resolves.toEqual(validSignup);

    expect(fetcher.mock.calls[0]?.[0]).toBe(
      "https://api.example.test/api/v1/needs/need%2Fone/signups",
    );
    expect(fetcher.mock.calls[0]?.[1]?.headers).toMatchObject({
      "Idempotency-Key": "11111111-1111-4111-8111-111111111111",
      "content-type": "application/json",
    });
  });

  it("generates T16/T17 paths, optimistic headers, and request bodies", async () => {
    const fetcher = jest
      .fn<typeof fetch>()
      .mockResolvedValueOnce(jsonResponse(200, validDate))
      .mockResolvedValueOnce(jsonResponse(200, validDate))
      .mockResolvedValueOnce(jsonResponse(200, validDate))
      .mockResolvedValueOnce(jsonResponse(200, validDate))
      .mockResolvedValueOnce(
        jsonResponse(200, {
          availability: 3,
          category: "serving",
          id: "need-1",
          instructions: "Serve food",
          status: "open",
          version: 2,
        }),
      )
      .mockResolvedValue(jsonResponse(200, validSignup));
    const client = new GeneratedApiClient({
      baseUrl: "https://api.example.test",
      fetch: fetcher,
    });
    const key = "11111111-1111-4111-8111-111111111111";

    await client.editServiceDate("date/one", 6, {
      title: "Service day",
      instructions: "Arrive early",
      startsAt: "2026-08-21T10:00:00.000Z",
      endsAt: "2026-08-21T12:00:00.000Z",
      cancellationDeadlineAt: "2026-08-20T10:00:00.000Z",
    });
    await client.openServiceDate("date/publish", key);
    await client.closeServiceDate("date/two", key, 7, {
      reason: "Coordination complete",
    });
    await client.cancelServiceDate("date/three", key, 8, {
      reason: "Weather",
    });
    await client.editHelpNeed("need/zero", 9, {
      instructions: "Serve food",
      capacity: 3,
      status: "open",
    });
    await client.withdrawSignup("signup/one", key, 7, {});
    await client.approveSignup("signup/approve", key, 10, {
      reason: null,
    });
    await client.declineSignup("signup/decline", key, 11, {
      reason: "Unable to place this request",
    });
    await client.waitlistSignup("signup/waitlist", key, 12, {
      reason: "Capacity is currently full",
    });
    await client.overrideSignupCancellation("signup/two", key, 8, {
      targetState: "cancelled",
      reason: "Member contacted the managing Food Incharge",
    });
    await client.reassignWaitlistedSignup("need/one", key, 9, {
      signupId: "22222222-2222-4222-8222-222222222222",
      reason: "Selected for capacity",
    });

    expect(fetcher.mock.calls.map((call) => call[0])).toEqual([
      "https://api.example.test/api/v1/dates/date%2Fone",
      "https://api.example.test/api/v1/dates/date%2Fpublish/open",
      "https://api.example.test/api/v1/dates/date%2Ftwo/close",
      "https://api.example.test/api/v1/dates/date%2Fthree/cancel",
      "https://api.example.test/api/v1/needs/need%2Fzero",
      "https://api.example.test/api/v1/signups/signup%2Fone/withdraw",
      "https://api.example.test/api/v1/signups/signup%2Fapprove/approve",
      "https://api.example.test/api/v1/signups/signup%2Fdecline/decline",
      "https://api.example.test/api/v1/signups/signup%2Fwaitlist/waitlist",
      "https://api.example.test/api/v1/signups/signup%2Ftwo/override",
      "https://api.example.test/api/v1/needs/need%2Fone/reassign",
    ]);
    expect(fetcher.mock.calls.map((call) => call[1]?.headers)).toEqual([
      expect.objectContaining({ "If-Match": "\"6\"" }),
      expect.objectContaining({ "Idempotency-Key": key }),
      expect.objectContaining({ "Idempotency-Key": key, "If-Match": "\"7\"" }),
      expect.objectContaining({ "Idempotency-Key": key, "If-Match": "\"8\"" }),
      expect.objectContaining({ "If-Match": "\"9\"" }),
      expect.objectContaining({ "Idempotency-Key": key, "If-Match": "\"7\"" }),
      expect.objectContaining({ "Idempotency-Key": key, "If-Match": "\"10\"" }),
      expect.objectContaining({ "Idempotency-Key": key, "If-Match": "\"11\"" }),
      expect.objectContaining({ "Idempotency-Key": key, "If-Match": "\"12\"" }),
      expect.objectContaining({ "Idempotency-Key": key, "If-Match": "\"8\"" }),
      expect.objectContaining({ "Idempotency-Key": key, "If-Match": "\"9\"" }),
    ]);
  });

  it("rejects malformed successful responses instead of casting them", async () => {
    const fetcher = jest
      .fn<typeof fetch>()
      .mockResolvedValueOnce(jsonResponse(200, { id: "date-1" }));
    const client = new GeneratedApiClient({
      baseUrl: "https://api.example.test",
      fetch: fetcher,
    });

    await expect(client.getServiceDate("date-1")).rejects.toMatchObject({
      message: "The server returned a malformed successful response.",
      status: 200,
    });
  });

  it("preserves RFC 9457 details and Retry-After", async () => {
    const problem = {
      code: "rate_limited",
      detail: "Try later.",
      status: 429,
      title: "Request failed",
      traceId: "trace-1",
      type: "https://httpstatuses.com/429",
    };
    const fetcher = jest
      .fn<typeof fetch>()
      .mockResolvedValueOnce(
        jsonResponse(429, problem, { "retry-after": "12" }),
      );
    const client = new GeneratedApiClient({
      baseUrl: "https://api.example.test",
      fetch: fetcher,
    });

    const error = await client.listMySignups().catch((value: unknown) => value);

    expect(error).toBeInstanceOf(ApiClientError);
    expect(error).toMatchObject({
      problem,
      retryAfterSeconds: 12,
      status: 429,
    });
  });

  it("surfaces T19 ETags without adding versions to response bodies", async () => {
    const message = {
      body: "Coordination update",
      createdAt: "2026-08-19T10:00:00.000Z",
      id: "message-1",
      senderDisplayName: "Member One",
      visibility: "visible" as const,
    };
    const page = {
      items: [message],
      lockedAt: null,
      nextCursor: null,
      serviceDateId: "date-1",
      status: "open" as const,
    };
    const state = {
      lockedAt: "2026-08-19T11:00:00.000Z",
      serviceDateId: "date-1",
      status: "locked" as const,
    };
    const fetcher = jest
      .fn<typeof fetch>()
      .mockResolvedValueOnce(jsonResponse(200, page, { etag: '"4"' }))
      .mockResolvedValueOnce(jsonResponse(201, message, { etag: '"5"' }))
      .mockResolvedValueOnce(emptyResponse(202, { etag: '"6"' }))
      .mockResolvedValueOnce(jsonResponse(200, message, { etag: '"7"' }))
      .mockResolvedValueOnce(jsonResponse(200, state, { etag: '"8"' }));
    const client = new GeneratedApiClient({
      baseUrl: "https://api.example.test",
      fetch: fetcher,
    });
    const key = "11111111-1111-4111-8111-111111111111";

    await expect(
      client.listThreadMessages("date/one", { cursor: "next page", pageSize: 25 }),
    ).resolves.toEqual({ data: page, etag: '"4"', version: 4 });
    await expect(
      client.postThreadMessage("date/one", key, 4, {
        body: "Coordination update",
      }),
    ).resolves.toEqual({ data: message, etag: '"5"', version: 5 });
    await expect(
      client.reportThreadMessage("date/one", "message/one", 5, {
        reason: "spam",
        comment: null,
      }),
    ).resolves.toEqual({ data: undefined, etag: '"6"', version: 6 });
    await expect(
      client.hideThreadMessage("date/one", "message/one", 6, {
        reason: "Moderation",
      }),
    ).resolves.toEqual({ data: message, etag: '"7"', version: 7 });
    await expect(
      client.lockThread("date/one", 7, { reason: "Date closed" }),
    ).resolves.toEqual({ data: state, etag: '"8"', version: 8 });

    expect(page).not.toHaveProperty("version");
    expect(message).not.toHaveProperty("version");
    expect(state).not.toHaveProperty("version");
    expect(fetcher.mock.calls.map((call) => call[0])).toEqual([
      "https://api.example.test/api/v1/dates/date%2Fone/thread/messages?cursor=next%20page&pageSize=25",
      "https://api.example.test/api/v1/dates/date%2Fone/thread/messages",
      "https://api.example.test/api/v1/dates/date%2Fone/thread/messages/message%2Fone/report",
      "https://api.example.test/api/v1/dates/date%2Fone/thread/messages/message%2Fone/hide",
      "https://api.example.test/api/v1/dates/date%2Fone/thread/lock",
    ]);
    expect(fetcher.mock.calls.map((call) => call[1]?.headers)).toEqual([
      expect.any(Object),
      expect.objectContaining({ "Idempotency-Key": key, "If-Match": '"4"' }),
      expect.objectContaining({ "If-Match": '"5"' }),
      expect.objectContaining({ "If-Match": '"6"' }),
      expect.objectContaining({ "If-Match": '"7"' }),
    ]);
  });

  it("rejects an ordinary thread success without a valid ETag", async () => {
    const fetcher = jest.fn<typeof fetch>().mockResolvedValueOnce(
      jsonResponse(200, {
        items: [],
        lockedAt: null,
        nextCursor: null,
        serviceDateId: "date-1",
        status: "open",
      }),
    );
    const client = new GeneratedApiClient({
      baseUrl: "https://api.example.test",
      fetch: fetcher,
    });

    await expect(client.listThreadMessages("date-1")).rejects.toMatchObject({
      message: "The server returned a malformed successful response.",
      status: 200,
    });
  });

  it("sends the privileged thread step-up header through the authenticated boundary", async () => {
    const privilegedPage = {
      items: [],
      lockedAt: null,
      nextCursor: null,
      serviceDateId: "date-1",
      status: "open" as const,
      threadId: "thread-1",
    };
    const fetcher = jest
      .fn<typeof fetch>()
      .mockResolvedValueOnce(jsonResponse(200, privilegedPage));
    const api = new GeneratedAuthApi("https://api.example.test", fetcher);

    await expect(
      api.readPrivilegedThreadMessages("access-token", "step-up-token", {
        serviceDateId: "date-1",
        reason: "Review reported content",
        purpose: "moderation",
        caseId: "CASE-19",
        cursor: null,
        pageSize: 50,
      }),
    ).resolves.toEqual(privilegedPage);

    expect(fetcher.mock.calls[0]?.[0]).toBe(
      "https://api.example.test/api/v1/admin/moderation/thread-reads",
    );
    expect(fetcher.mock.calls[0]?.[1]?.headers).toMatchObject({
      authorization: "Bearer access-token",
      "X-Step-Up-Token": "step-up-token",
    });
  });

  it("keeps bearer construction inside the API wrapper", async () => {
    const fetcher = jest
      .fn<typeof fetch>()
      .mockResolvedValueOnce(jsonResponse(200, { items: [], nextCursor: null }));
    const api = new GeneratedAuthApi("https://api.example.test", fetcher);

    await api.listOpenServiceDates("access-token");

    expect(fetcher.mock.calls[0]?.[1]?.headers).toMatchObject({
      authorization: ["Bearer", "access-token"].join(" "),
    });
  });
});

function jsonResponse(
  status: number,
  body: unknown,
  extraHeaders: Record<string, string> = {},
): Response {
  const headers = new Map<string, string>([
    ["content-type", "application/json"],
    ...Object.entries(extraHeaders),
  ]);

  return {
    headers: {
      get: (name: string) => headers.get(name.toLowerCase()) ?? null,
    },
    ok: status >= 200 && status < 300,
    status,
    text: async () => JSON.stringify(body),
  } as unknown as Response;
}

function emptyResponse(
  status: number,
  extraHeaders: Record<string, string> = {},
): Response {
  const headers = new Map<string, string>(Object.entries(extraHeaders));

  return {
    headers: {
      get: (name: string) => headers.get(name.toLowerCase()) ?? null,
    },
    ok: status >= 200 && status < 300,
    status,
    text: async () => "",
  } as unknown as Response;
}
