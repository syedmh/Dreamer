import { beforeEach, describe, expect, it, jest } from "@jest/globals";
import type { ReactNode } from "react";
import { Text } from "react-native";
import {
  act,
  create,
  type ReactTestRenderer,
} from "react-test-renderer";

import {
  ApiClientError,
  type ServiceDateResponse,
  type SignupResponse,
  type T14Api,
} from "../../../src/core/api/auth-api";
import { publishFeatureRefresh } from "../../../src/core/api/feature-refresh-bus";
import type {
  PendingSignupCommand,
  PendingSignupStorage,
} from "../../../src/core/security/pending-signup-storage";
import {
  AuthContext,
  type AuthContextValue,
} from "../../../src/features/auth/AuthSessionProvider";
import {
  useServiceDate,
  type ServiceDateState,
} from "../../../src/features/dates/useServiceDate";
import {
  usePendingRoster,
  type PendingRosterState,
} from "../../../src/features/roster/usePendingRoster";
import {
  useMySignups,
  type MySignupsState,
} from "../../../src/features/signups/useMySignups";
import { MySignupsView } from "../../../src/features/signups/MySignupsScreen";

let mockFocusCallback: (() => void | (() => void)) | null = null;
let mockFocusCleanup: (() => void) | undefined;

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  return {
    useFocusEffect: (callback: () => void | (() => void)) => {
      React.useEffect(() => {
        mockFocusCallback = callback;
        return () => {
          mockFocusCleanup?.();
          mockFocusCleanup = undefined;
          if (mockFocusCallback === callback) {
            mockFocusCallback = null;
          }
        };
      }, [callback]);
    },
  };
});

const serviceDate = createDate("date-1", "First");
const refreshedDate = createDate("date-1", "Refreshed");
const signup = createSignup();

describe("first-slice hook lifecycle", () => {
  beforeEach(() => {
    mockFocusCallback = null;
    mockFocusCleanup = undefined;
    jest.clearAllMocks();
  });

  it("aborts a blurred detail load and ignores its stale late result", async () => {
    const firstLoad = createDeferred<ServiceDateResponse>();
    const api = createApi({
      getServiceDate: jest
        .fn<T14Api["getServiceDate"]>()
        .mockReturnValueOnce(firstLoad.promise)
        .mockResolvedValueOnce(refreshedDate),
    });
    let latest!: ServiceDateState;
    const renderer = await render(
      <ServiceDateProbe api={api} onState={(state) => { latest = state; }} />,
    );

    await focus();
    const firstSignal = (
      api.getServiceDate as jest.MockedFunction<T14Api["getServiceDate"]>
    ).mock.calls[0]?.[2];
    await focus();

    expect(firstSignal?.aborted).toBe(true);
    expect(latest.date?.title).toBe("Refreshed");

    await act(async () => {
      firstLoad.resolve(serviceDate);
      await Promise.resolve();
    });

    expect(latest.date?.title).toBe("Refreshed");
    await unmount(renderer);
  });

  it("refetches detail, My Signups, and roster after signups-changed", async () => {
    const detailApi = createApi({
      getServiceDate: jest
        .fn<T14Api["getServiceDate"]>()
        .mockResolvedValue(serviceDate),
    });
    const detailRenderer = await render(
      <ServiceDateProbe api={detailApi} onState={() => undefined} />,
    );
    await focus();
    await publishAndFlush("signups-changed");
    expect(detailApi.getServiceDate).toHaveBeenCalledTimes(2);
    await unmount(detailRenderer);

    const signupApi = createApi({
      listMySignups: jest
        .fn<T14Api["listMySignups"]>()
        .mockResolvedValue({ items: [signup], nextCursor: null }),
    });
    const storage = createStorage();
    const signupsRenderer = await render(
      <MySignupsProbe
        api={signupApi}
        onState={() => undefined}
        storage={storage}
      />,
    );
    await focus();
    await publishAndFlush("signups-changed");
    expect(signupApi.listMySignups).toHaveBeenCalledTimes(2);
    await unmount(signupsRenderer);

    const rosterApi = createApi({
      getManagedRoster: jest
        .fn<T14Api["getManagedRoster"]>()
        .mockResolvedValue({
          items: [signup],
          nextCursor: null,
          serviceDateId: "date-1",
        }),
    });
    const rosterRenderer = await render(
      <RosterProbe api={rosterApi} onState={() => undefined} />,
    );
    await focus();
    await publishAndFlush("signups-changed");
    expect(rosterApi.getManagedRoster).toHaveBeenCalledTimes(2);
    await unmount(rosterRenderer);
  });

  it("retains a durable pending signup when My Signups is offline", async () => {
    const pendingCommand = createPendingCommand();
    const api = createApi({
      listMySignups: jest
        .fn<T14Api["listMySignups"]>()
        .mockRejectedValue(
          new Error("Internal upstream failure must not be exposed."),
        ),
    });
    const storage = createStorage();
    storage.loadForIdentity.mockResolvedValue({
      command: pendingCommand,
      epoch: 0,
    });
    let latest!: MySignupsState;
    const renderer = await render(
      <MySignupsViewProbe
        api={api}
        onState={(state) => {
          latest = state;
        }}
        storage={storage}
      />,
    );

    await focus();
    await flush();

    expect(latest.pendingCommand).toEqual(pendingCommand);
    expect(latest.error).toBe("Personal signups could not be loaded.");
    expect(readText(renderer.root)).toContain("Status: Pending sync");
    expect(readText(renderer.root)).toContain(
      "Personal signups could not be loaded.",
    );
    expect(readText(renderer.root)).not.toContain(
      "Internal upstream failure must not be exposed.",
    );
    expect(
      renderer.root.findByProps({
        accessibilityLabel: "Signup for Serving, Pending sync",
      }),
    ).toBeDefined();
    expect(storage.clearIfCurrent).not.toHaveBeenCalled();

    await unmount(renderer);
  });

  it("purges roster names on concealed 404 and session clear", async () => {
    const api = createApi({
      getManagedRoster: jest
        .fn<T14Api["getManagedRoster"]>()
        .mockResolvedValueOnce({
          items: [signup],
          nextCursor: null,
          serviceDateId: "date-1",
        })
        .mockRejectedValueOnce(new ApiClientError({ status: 404 }))
        .mockResolvedValueOnce({
          items: [signup],
          nextCursor: null,
          serviceDateId: "date-1",
        }),
    });
    let latest!: PendingRosterState;
    const renderer = await render(
      <RosterProbe api={api} onState={(state) => { latest = state; }} />,
    );

    await focus();
    expect(latest.signups).toEqual([signup]);

    await act(async () => {
      await latest.refresh();
    });
    expect(latest.error).toBe("The pending roster is unavailable.");
    expect(latest.signups).toEqual([]);

    await act(async () => {
      await latest.refresh();
    });
    expect(latest.signups).toEqual([signup]);

    await publishAndFlush("session-cleared");
    expect(latest.signups).toEqual([]);
    await unmount(renderer);
  });

  it("aborts an in-flight roster load when the screen unmounts", async () => {
    const pendingRoster = createDeferred<{
      items: SignupResponse[];
      nextCursor: null;
      serviceDateId: string;
    }>();
    const api = createApi({
      getManagedRoster: jest
        .fn<T14Api["getManagedRoster"]>()
        .mockReturnValue(pendingRoster.promise),
    });
    const renderer = await render(
      <RosterProbe api={api} onState={() => undefined} />,
    );

    await focus();
    const signal = (
      api.getManagedRoster as jest.MockedFunction<
        T14Api["getManagedRoster"]
      >
    ).mock.calls[0]?.[3];

    await act(async () => {
      renderer.unmount();
    });

    expect(signal?.aborted).toBe(true);
    pendingRoster.resolve({
      items: [signup],
      nextCursor: null,
      serviceDateId: "date-1",
    });
  });
});

function ServiceDateProbe({
  api,
  onState,
}: {
  readonly api: T14Api;
  readonly onState: (state: ServiceDateState) => void;
}) {
  const state = useServiceDate("date-1", api);
  onState(state);
  return <Text>{state.date?.title ?? state.error ?? "empty"}</Text>;
}

function MySignupsProbe({
  api,
  onState,
  storage,
}: {
  readonly api: T14Api;
  readonly onState: (state: MySignupsState) => void;
  readonly storage: PendingSignupStorage;
}) {
  const state = useMySignups(api, storage);
  onState(state);
  return <Text>{state.signups.length}</Text>;
}

function MySignupsViewProbe({
  api,
  onState,
  storage,
}: {
  readonly api: T14Api;
  readonly onState: (state: MySignupsState) => void;
  readonly storage: PendingSignupStorage;
}) {
  const state = useMySignups(api, storage);
  onState(state);
  return (
    <MySignupsView
      error={state.error}
      isLoading={state.isLoading}
      onRetry={() => {
        void state.refresh();
      }}
      pendingCommand={state.pendingCommand}
      signups={state.signups}
    />
  );
}

function RosterProbe({
  api,
  onState,
}: {
  readonly api: T14Api;
  readonly onState: (state: PendingRosterState) => void;
}) {
  const state = usePendingRoster("date-1", api);
  onState(state);
  return <Text>{state.signups.length}</Text>;
}

async function render(element: ReactNode): Promise<ReactTestRenderer> {
  let renderer!: ReactTestRenderer;
  await act(async () => {
    renderer = create(
      <AuthContext.Provider value={authValue()}>
        {element}
      </AuthContext.Provider>,
    );
  });
  return renderer;
}

async function unmount(renderer: ReactTestRenderer): Promise<void> {
  await act(async () => {
    renderer.unmount();
  });
}

async function focus(): Promise<void> {
  await act(async () => {
    mockFocusCleanup?.();
    mockFocusCleanup = mockFocusCallback?.() ?? undefined;
    await Promise.resolve();
  });
}

async function publishAndFlush(
  event: "session-cleared" | "signups-changed",
): Promise<void> {
  await act(async () => {
    publishFeatureRefresh(event);
    await Promise.resolve();
    await Promise.resolve();
  });
}

function authValue(): AuthContextValue {
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
    signIn: async () => undefined,
    signOut: async () => undefined,
    status: "authenticated",
  };
}

function createApi(overrides: Partial<T14Api>): T14Api {
  return overrides as T14Api;
}

function createStorage(): jest.Mocked<PendingSignupStorage> {
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
  };
}

function createDate(id: string, title: string): ServiceDateResponse {
  return {
    cancellationDeadlineAt: "2026-08-20T10:00:00.000Z",
    endsAt: "2026-08-21T12:00:00.000Z",
    helpNeeds: [],
    id,
    instructions: "Instructions",
    managerMembershipId: "membership-1",
    startsAt: "2026-08-21T10:00:00.000Z",
    status: "open",
    title,
    version: 1,
  };
}

function createSignup(): SignupResponse {
  return {
    category: "serving",
    helpNeedId: "need-1",
    id: "signup-1",
    kind: "individual",
    label: null,
    lastTransitionAt: null,
    memberParticipants: [],
    primaryContact: {
      displayName: "Private Name",
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
}

function createPendingCommand(): PendingSignupCommand {
  return {
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
}

function createDeferred<T>(): {
  readonly promise: Promise<T>;
  readonly resolve: (value: T | PromiseLike<T>) => void;
} {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}
async function flush(): Promise<void> {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
  });
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
