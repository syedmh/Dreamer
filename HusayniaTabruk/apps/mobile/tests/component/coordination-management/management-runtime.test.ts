import { describe, expect, it, jest } from "@jest/globals";

import { ApiClientError } from "../../../src/core/api/auth-api";
import {
  publishFeatureRefresh,
  subscribeFeatureRefresh,
} from "../../../src/core/api/feature-refresh-bus";
import {
  IdempotentCommandStore,
  IdempotencyKeyStore,
  toManagementError,
} from "../../../src/features/admin/coordination-management";

describe("coordination management runtime", () => {
  it("retains the same idempotency key for an unknown outcome and releases it after confirmation", () => {
    const createKey = jest
      .fn<() => string>()
      .mockReturnValueOnce("key-1")
      .mockReturnValueOnce("key-2");
    const store = new IdempotencyKeyStore(createKey);

    expect(store.get("cancel:date-1")).toBe("key-1");
    expect(store.get("cancel:date-1")).toBe("key-1");
    store.confirm("cancel:date-1");
    expect(store.get("cancel:date-1")).toBe("key-2");
  });

  it("retains the complete unresolved command across refresh and changed form input", () => {
    const store = new IdempotentCommandStore(() => "key-1");
    const original = store.get("override:signup-1", () => ({
      expectedVersion: 5,
      reason: "Original reason",
    }));
    const afterRefresh = store.get("override:signup-1", () => ({
      expectedVersion: 6,
      reason: "Changed reason",
    }));

    expect(afterRefresh).toBe(original);
    expect(afterRefresh).toEqual({
      key: "key-1",
      request: {
        expectedVersion: 5,
        reason: "Original reason",
      },
    });
  });

  it("classifies stale writes as refresh-and-retry and timeouts as unknown outcomes", () => {
    const stale = new ApiClientError({
      message: "stale",
      status: 412,
      problem: {
        code: "stale_version",
        detail: "Version changed.",
        status: 412,
        title: "Stale version",
        traceId: "trace-1",
        type: "about:blank",
      },
    });
    expect(toManagementError(stale, "service date")).toEqual({
      kind: "stale",
      message:
        "This service date changed on the server. Refresh status, review the latest values, and retry.",
    });

    const timeout = new Error("The request timed out.");
    timeout.name = "TimeoutError";
    expect(toManagementError(timeout, "signup")).toEqual({
      kind: "unknown",
      message:
        "The signup outcome is unknown. Refresh status before retrying.",
    });
  });

  it("broadcasts confirmed date, roster, and signup refresh signals", () => {
    const listener = jest.fn();
    const unsubscribe = subscribeFeatureRefresh(listener);

    publishFeatureRefresh("dates-changed");
    publishFeatureRefresh("roster-changed");
    publishFeatureRefresh("signups-changed");
    unsubscribe();
    publishFeatureRefresh("dates-changed");

    expect(listener.mock.calls.map(([event]) => event)).toEqual([
      "dates-changed",
      "roster-changed",
      "signups-changed",
    ]);
  });
});
