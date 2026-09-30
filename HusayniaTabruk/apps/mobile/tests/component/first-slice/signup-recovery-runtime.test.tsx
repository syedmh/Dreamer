import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  jest,
} from "@jest/globals";
import { AppState, Pressable, Text } from "react-native";
import { act, create } from "react-test-renderer";

import {
  ApiClientError,
  type ProblemDetails,
  type SignupResponse,
  type T14Api,
} from "../../../src/core/api/auth-api";
import {
  publishFeatureRefresh,
  subscribeFeatureRefresh,
  type FeatureRefreshEvent,
} from "../../../src/core/api/feature-refresh-bus";
import type {
  PendingSignupCommand,
  PendingSignupStorage,
} from "../../../src/core/security/pending-signup-storage";
import {
  AuthContext,
  type AuthContextValue,
} from "../../../src/features/auth/AuthSessionProvider";
import { SignupRecoveryRuntime } from "../../../src/features/signups/SignupRecoveryRuntime";
import {
  useSignupSubmission,
} from "../../../src/features/signups/useSignupSubmission";

const command: PendingSignupCommand = {
  body: {
    kind: "team",
    label: "Team",
    memberParticipantIds: [],
    unnamedParticipantCount: 1,
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
  status: "pending",
  submittedAt: "2026-08-17T10:00:00.000Z",
  totalParticipantCount: 2,
  unnamedParticipantCount: 1,
  version: 1,
  waitlistOrder: null,
};

describe("SignupRecoveryRuntime", () => {
  beforeEach(() => {
    jest
      .spyOn(Date, "now")
      .mockReturnValue(Date.parse("2026-08-17T11:00:00.000Z"));
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  it("reconciles a retained command in the authenticated shell before replay", async () => {
    jest.spyOn(AppState, "addEventListener").mockReturnValue({
      remove: jest.fn(),
    });
    let current: PendingSignupCommand | null = command;
    const storage = createStorage({
      clearIfCurrent: jest.fn(async () => {
        current = null;
        return true;
      }),
      loadForIdentity: jest.fn(async () =>
        current ? { command: current, epoch: 0 } : null,
      ),
    });
    const api = {
      listMySignups: jest
        .fn<T14Api["listMySignups"]>()
        .mockResolvedValue({ items: [signup], nextCursor: null }),
      submitSignup: jest.fn<T14Api["submitSignup"]>(),
    } as unknown as T14Api;
    const events: FeatureRefreshEvent[] = [];
    const unsubscribe = subscribeFeatureRefresh((event) => {
      events.push(event);
    });

    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <AuthContext.Provider value={authValue()}>
          <SignupRecoveryRuntime api={api} storage={storage} />
        </AuthContext.Provider>,
      );
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(storage.loadForIdentity).toHaveBeenCalledWith(
      "organization-1",
      "membership-1",
      expect.any(Object),
    );
    expect(api.listMySignups).toHaveBeenCalledTimes(1);
    expect(api.submitSignup).not.toHaveBeenCalled();
    expect(storage.clearIfCurrent).toHaveBeenCalledTimes(1);
    expect(events).toContain("signups-changed");

    unsubscribe();
    await act(async () => {
      renderer.unmount();
    });
  });

  it("shares one authoritative retry between global recovery and manual retry", async () => {
    jest.spyOn(AppState, "addEventListener").mockReturnValue({
      remove: jest.fn(),
    });
    const sendResult = createDeferred<SignupResponse>();
    let current: PendingSignupCommand | null = command;
    const storage = createStorage({
      clearIfCurrent: jest.fn(async (slot) => {
        if (
          !current ||
          current.idempotencyKey !== slot.command.idempotencyKey
        ) {
          return false;
        }
        current = null;
        return true;
      }),
      loadForIdentity: jest.fn(async () =>
        current ? { command: current, epoch: 0 } : null,
      ),
    });
    const api = {
      listMySignups: jest
        .fn<T14Api["listMySignups"]>()
        .mockResolvedValue({ items: [], nextCursor: null }),
      submitSignup: jest
        .fn<T14Api["submitSignup"]>()
        .mockReturnValue(sendResult.promise),
    } as unknown as T14Api;
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <AuthContext.Provider value={authValue()}>
          <SignupRecoveryRuntime api={api} storage={storage} />
          <ManualRetryHarness
            api={api}
            storage={storage}
          />
        </AuthContext.Provider>,
      );
      await flush();
    });

    expect(api.submitSignup).toHaveBeenCalledTimes(1);

    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Retry pending signup manually" })
        .props.onPress();
      await flush();
    });

    expect(api.submitSignup).toHaveBeenCalledTimes(1);

    await act(async () => {
      sendResult.resolve(signup);
      await flush();
    });

    expect(api.submitSignup).toHaveBeenCalledTimes(1);
    expect(storage.clearIfCurrent).toHaveBeenCalledTimes(1);
    expect(
      readText(
        renderer.root.findByProps({
          accessibilityLabel: "Retry pending signup manually",
        }),
      ),
    ).toContain("confirmed");
    expect(readText(renderer.root)).not.toContain("permanentError");

    await act(async () => {
      renderer.unmount();
    });
  });

  it("surfaces a privacy-safe category-closed recovery alert and supports dismissal", async () => {
    jest.spyOn(AppState, "addEventListener").mockReturnValue({
      remove: jest.fn(),
    });
    let current: PendingSignupCommand | null = command;
    const storage = createStorage({
      clearIfCurrent: jest.fn(async () => {
        current = null;
        return true;
      }),
      loadForIdentity: jest.fn(async () =>
        current ? { command: current, epoch: 0 } : null,
      ),
    });
    const api = {
      listMySignups: jest
        .fn<T14Api["listMySignups"]>()
        .mockResolvedValue({ items: [], nextCursor: null }),
      submitSignup: jest
        .fn<T14Api["submitSignup"]>()
        .mockRejectedValue(
          problemError(
            409,
            "category_closed",
            "Internal category details must not be rendered.",
          ),
        ),
    } as unknown as T14Api;

    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <AuthContext.Provider value={authValue()}>
          <SignupRecoveryRuntime api={api} storage={storage} />
        </AuthContext.Provider>,
      );
      await flush();
    });

    const alert = renderer.root.findByProps({ accessibilityRole: "alert" });
    expect(readText(alert)).toContain(
      "This help request is no longer available.",
    );
    expect(readText(alert)).not.toContain("Internal category details");
    expect(storage.clearIfCurrent).toHaveBeenCalledTimes(1);

    await act(async () => {
      renderer.root
        .findByProps({ accessibilityLabel: "Dismiss signup recovery message" })
        .props.onPress();
    });

    expect(
      renderer.root.findAllByProps({ accessibilityRole: "alert" }),
    ).toHaveLength(0);

    await act(async () => {
      renderer.unmount();
    });
  });

  it("freezes the 23-hour expiry outcome across foreground recovery and clears it on session cleanup", async () => {
    jest
      .spyOn(Date, "now")
      .mockReturnValue(Date.parse("2026-08-17T10:00:00.000Z"));
    let onAppStateChange: ((state: string) => void) | null = null;
    jest.spyOn(AppState, "addEventListener").mockImplementation(
      (_event, listener) => {
        onAppStateChange = listener as (state: string) => void;
        return { remove: jest.fn() };
      },
    );
    let current: PendingSignupCommand | null = {
      ...command,
      createdAt: "2026-08-16T11:00:00.000Z",
    };
    const storage = createStorage({
      clearIfCurrent: jest.fn(async () => {
        current = null;
        return true;
      }),
      loadForIdentity: jest.fn(async () =>
        current ? { command: current, epoch: 0 } : null,
      ),
    });
    const api = {
      listMySignups: jest
        .fn<T14Api["listMySignups"]>()
        .mockResolvedValue({ items: [], nextCursor: null }),
      submitSignup: jest.fn<T14Api["submitSignup"]>(),
    } as unknown as T14Api;

    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <AuthContext.Provider value={authValue()}>
          <SignupRecoveryRuntime api={api} storage={storage} />
        </AuthContext.Provider>,
      );
      await flush();
    });

    const expiryMessage =
      "The pending signup is too old to replay. Review the current date and submit again.";
    expect(readText(renderer.root)).toContain(expiryMessage);
    expect(api.submitSignup).not.toHaveBeenCalled();
    expect(storage.clearIfCurrent).toHaveBeenCalledTimes(1);

    await act(async () => {
      onAppStateChange?.("active");
      await flush();
    });

    expect(readText(renderer.root)).toContain(expiryMessage);

    await act(async () => {
      publishFeatureRefresh("session-cleared");
      await Promise.resolve();
    });

    expect(readText(renderer.root)).not.toContain(expiryMessage);
    expect(renderer.root.findAllByType(Pressable)).toHaveLength(0);

    await act(async () => {
      renderer.unmount();
    });
  });

  it("clears a terminal recovery outcome when the authenticated identity changes", async () => {
    jest.spyOn(AppState, "addEventListener").mockReturnValue({
      remove: jest.fn(),
    });
    let current: PendingSignupCommand | null = command;
    const storage = createStorage({
      clearIfCurrent: jest.fn(async () => {
        current = null;
        return true;
      }),
      loadForIdentity: jest.fn(async (organizationId, membershipId) =>
        current &&
        current.organizationId === organizationId &&
        current.primaryMembershipId === membershipId
          ? { command: current, epoch: 0 }
          : null,
      ),
    });
    const api = {
      listMySignups: jest
        .fn<T14Api["listMySignups"]>()
        .mockResolvedValue({ items: [], nextCursor: null }),
      submitSignup: jest
        .fn<T14Api["submitSignup"]>()
        .mockRejectedValue(
          problemError(409, "category_closed", "Category closed."),
        ),
    } as unknown as T14Api;

    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <AuthContext.Provider value={authValue()}>
          <SignupRecoveryRuntime api={api} storage={storage} />
        </AuthContext.Provider>,
      );
      await flush();
    });
    expect(
      renderer.root.findAllByProps({ accessibilityRole: "alert" }).length,
    ).toBeGreaterThan(0);

    await act(async () => {
      renderer.update(
        <AuthContext.Provider value={authValue("membership-2")}>
          <SignupRecoveryRuntime api={api} storage={storage} />
        </AuthContext.Provider>,
      );
      await flush();
    });

    expect(
      renderer.root.findAllByProps({ accessibilityRole: "alert" }),
    ).toHaveLength(0);
    expect(storage.loadForIdentity).toHaveBeenCalledWith(
      "organization-1",
      "membership-2",
      expect.any(Object),
    );

    await act(async () => {
      renderer.unmount();
    });
  });
});

function authValue(membershipId = "membership-1"): AuthContextValue {
  return {
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
          displayName: "Member",
          eligibleAsNamedParticipant: true,
          id: membershipId,
        },
        organization: {
          id: "organization-1",
          name: "Husaynia",
          timeZone: "America/Los_Angeles",
        },
        roles: ["Member"],
      },
    },
    signIn: async () => undefined,
    signOut: async () => undefined,
    status: "authenticated",
  };
}

function ManualRetryHarness({
  api,
  storage,
}: {
  readonly api: T14Api;
  readonly storage: PendingSignupStorage;
}) {
  const state = useSignupSubmission(
    {
      category: "serving",
      helpNeedId: command.helpNeedId,
      serviceDateId: command.serviceDateId,
    },
    api,
    storage,
  );

  return (
    <Pressable
      accessibilityLabel="Retry pending signup manually"
      accessibilityRole="button"
      onPress={() => {
        void state.retry();
      }}
    >
      <Text>{state.result?.type ?? "idle"}</Text>
    </Pressable>
  );
}

function createStorage(
  overrides: Partial<jest.Mocked<PendingSignupStorage>>,
): jest.Mocked<PendingSignupStorage> {
  return {
    admit: jest.fn<PendingSignupStorage["admit"]>(),
    clearIfCurrent: jest
      .fn<PendingSignupStorage["clearIfCurrent"]>()
      .mockResolvedValue(false),
    load: jest
      .fn<PendingSignupStorage["load"]>()
      .mockResolvedValue(null),
    loadForIdentity: jest
      .fn<PendingSignupStorage["loadForIdentity"]>()
      .mockResolvedValue(null),
    purge: jest
      .fn<PendingSignupStorage["purge"]>()
      .mockResolvedValue(undefined),
    replaceIfCurrent: jest.fn<PendingSignupStorage["replaceIfCurrent"]>(),
    ...overrides,
  };
}

function problemError(
  status: number,
  code: string,
  detail: string,
): ApiClientError {
  const problem: ProblemDetails = {
    code,
    detail,
    status,
    title: "Problem",
    traceId: "trace-id",
    type: `https://httpstatuses.com/${status}`,
  };

  return new ApiClientError({ problem, status });
}

async function flush(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
}

function createDeferred<T>(): {
  readonly promise: Promise<T>;
  readonly reject: (reason?: unknown) => void;
  readonly resolve: (value: T | PromiseLike<T>) => void;
} {
  let reject!: (reason?: unknown) => void;
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((promiseResolve, promiseReject) => {
    resolve = promiseResolve;
    reject = promiseReject;
  });

  return { promise, reject, resolve };
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
