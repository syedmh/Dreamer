import { beforeEach, describe, expect, it, jest } from "@jest/globals";
import type { ReactNode } from "react";
import {
  act,
  create,
  type ReactTestRenderer,
} from "react-test-renderer";

import {
  ApiClientError,
  type ServiceDateResponse,
  type SignupResponse,
  type SubmitSignupRequest,
  type T14Api,
} from "../../../src/core/api/auth-api";
import { createRequestScope } from "../../../src/core/api/operation-runtime";
import type { AuthSessionCoordinator } from "../../../src/core/security/auth-session-controller";
import type {
  PendingSignupAdmission,
  PendingSignupCommand,
  PendingSignupSlot,
  PendingSignupStorage,
} from "../../../src/core/security/pending-signup-storage";
import type { AuthSession } from "../../../src/core/security/auth-session-types";
import {
  AuthSessionProvider,
  type AuthContextValue,
} from "../../../src/features/auth/AuthSessionProvider";
import { useAuth } from "../../../src/features/auth/useAuth";
import { DateDetailView } from "../../../src/features/dates/DateDetailScreen";
import {
  useServiceDate,
  type ServiceDateState,
} from "../../../src/features/dates/useServiceDate";
import { SignupComposerView } from "../../../src/features/signups/SignupComposerScreen";
import {
  useSignupSubmission,
  type SignupSubmissionState,
} from "../../../src/features/signups/useSignupSubmission";

let mockFocusCallback: (() => void | (() => void)) | null = null;
let mockFocusCleanup: (() => void) | undefined;

jest.mock("expo-router", () => {
  const React = jest.requireActual<typeof import("react")>("react");
  return {
    Link: (props: Record<string, unknown>) =>
      React.createElement("Link", props, props.children as ReactNode),
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
    useLocalSearchParams: () => ({}),
  };
});

const sampleSession: AuthSession = {
  accessToken: "access-token-value",
  accessTokenExpiresAt: "2026-08-17T12:00:00.000Z",
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
  installationId: "ee0c3f79-6738-4417-9d68-a3b08b708fba",
  refreshToken: "refresh-token-value",
  refreshTokenExpiresAt: "2026-09-16T12:00:00.000Z",
};

const freshSession: AuthSession = {
  ...sampleSession,
  accessToken: "fresh-access-token",
  accessTokenExpiresAt: "2026-08-17T12:10:00.000Z",
  refreshToken: "fresh-refresh-token",
  refreshTokenExpiresAt: "2026-09-16T12:10:00.000Z",
};

const signupBody: SubmitSignupRequest = {
  kind: "individual",
  label: null,
  memberParticipantIds: [],
  unnamedParticipantCount: 0,
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
    displayName: "Member",
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

const serviceDate: ServiceDateResponse = {
  cancellationDeadlineAt: "2026-08-20T10:00:00.000Z",
  endsAt: "2026-08-21T12:00:00.000Z",
  helpNeeds: [],
  id: "date-1",
  instructions: "Instructions",
  managerMembershipId: "membership-1",
  startsAt: "2026-08-21T10:00:00.000Z",
  status: "open",
  title: "Late date",
  version: 1,
};

describe("authenticated feature request deadlines", () => {
  beforeEach(() => {
    mockFocusCallback = null;
    mockFocusCleanup = undefined;
    jest.clearAllMocks();
  });

  it("settles a non-cooperative initial authenticated request at 10 seconds and suppresses its late rejection", async () => {
    jest.useFakeTimers();
    const lateRequest = createDeferred<string>();
    const scope = createRequestScope();
    const pendingStorage = createStatefulStorage();
    let auth: AuthContextValue | null = null;
    let renderer: ReactTestRenderer | undefined;
    let operation: Promise<string> | undefined;
    let outcome: PromiseSettledResult<string> | undefined;

    try {
      renderer = await renderWithProvider(
        createController(),
        pendingStorage.storage,
        <CaptureAuth onCapture={(value) => { auth = value; }} />,
      );

      await act(async () => {
        operation = auth!.runAuthenticatedRequest(
          () => lateRequest.promise,
          scope.signal,
        );
        void operation.then(
          (value) => {
            outcome = { status: "fulfilled", value };
          },
          (reason: unknown) => {
            outcome = { reason, status: "rejected" };
          },
        );
        await Promise.resolve();
      });

      await advanceProductionDeadline();

      expect(outcome).toMatchObject({
        reason: expect.objectContaining({ name: "TimeoutError" }),
        status: "rejected",
      });

      lateRequest.reject(new Error("late transport rejection"));
      await flush();

      expect(outcome).toMatchObject({
        reason: expect.objectContaining({ name: "TimeoutError" }),
        status: "rejected",
      });
    } finally {
      lateRequest.reject(new Error("cleanup rejection"));
      scope.dispose();
      if (operation) {
        await Promise.allSettled([operation]);
      }
      if (renderer) {
        await unmount(renderer);
      }
      jest.useRealTimers();
    }
  });

  it("settles a non-cooperative post-refresh retry at 10 seconds and suppresses its late rejection", async () => {
    jest.useFakeTimers();
    const lateRetry = createDeferred<string>();
    const synchronize = jest
      .fn<AuthSessionCoordinator["synchronize"]>()
      .mockResolvedValue(sampleSession);
    const controller = createController({ synchronize });
    const pendingStorage = createStatefulStorage();
    const unauthorized = new ApiClientError({ status: 401 });
    const request = jest
      .fn<(accessToken: string) => Promise<string>>()
      .mockRejectedValueOnce(unauthorized)
      .mockImplementationOnce(() => lateRetry.promise);
    const scope = createRequestScope();
    let auth: AuthContextValue | null = null;
    let renderer: ReactTestRenderer | undefined;
    let operation: Promise<string> | undefined;
    let outcome: PromiseSettledResult<string> | undefined;

    try {
      renderer = await renderWithProvider(
        controller,
        pendingStorage.storage,
        <CaptureAuth onCapture={(value) => { auth = value; }} />,
      );
      synchronize.mockClear();
      synchronize.mockResolvedValueOnce(freshSession);

      await act(async () => {
        operation = auth!.runAuthenticatedRequest(request, scope.signal);
        void operation.then(
          (value) => {
            outcome = { status: "fulfilled", value };
          },
          (reason: unknown) => {
            outcome = { reason, status: "rejected" };
          },
        );
        await waitForCallCount(request, 2);
      });

      expect(request).toHaveBeenNthCalledWith(1, sampleSession.accessToken);
      expect(request).toHaveBeenNthCalledWith(2, freshSession.accessToken);

      await advanceProductionDeadline();

      expect(outcome).toMatchObject({
        reason: expect.objectContaining({ name: "TimeoutError" }),
        status: "rejected",
      });
      expect(synchronize).toHaveBeenCalledTimes(1);
      expect(controller.signOut).not.toHaveBeenCalled();

      lateRetry.reject(new Error("late retry rejection"));
      await flush();

      expect(outcome).toMatchObject({
        reason: expect.objectContaining({ name: "TimeoutError" }),
        status: "rejected",
      });
      expect(auth!.status).toBe("authenticated");
    } finally {
      lateRetry.reject(new Error("cleanup rejection"));
      scope.dispose();
      if (operation) {
        await Promise.allSettled([operation]);
      }
      if (renderer) {
        await unmount(renderer);
      }
      jest.useRealTimers();
    }
  });

  it("returns Pending sync and clears signup busy state when submitSignup ignores abort", async () => {
    jest.useFakeTimers();
    const lateSignup = createDeferred<SignupResponse>();
    const submitSignup = jest
      .fn<T14Api["submitSignup"]>()
      .mockReturnValue(lateSignup.promise);
    const api = {
      submitSignup,
    } as unknown as T14Api;
    const pendingStorage = createStatefulStorage();
    let latest!: SignupSubmissionState;
    let renderer: ReactTestRenderer | undefined;
    let submission: Promise<void> | undefined;
    let outcome: PromiseSettledResult<void> | undefined;

    try {
      renderer = await renderWithProvider(
        createController(),
        pendingStorage.storage,
        <SignupSubmissionProbe
          api={api}
          onState={(state) => { latest = state; }}
          storage={pendingStorage.storage}
        />,
      );

      act(() => {
        submission = latest.submit(signupBody);
        void submission.then(
          (value) => {
            outcome = { status: "fulfilled", value };
          },
          (reason: unknown) => {
            outcome = { reason, status: "rejected" };
          },
        );
      });
      await waitForCallCount(submitSignup, 1);

      const submittedKey = submitSignup.mock.calls[0]?.[2];
      expect(latest.isBusy).toBe(true);
      expect(pendingStorage.current()?.command.idempotencyKey).toBe(
        submittedKey,
      );

      await advanceProductionDeadline();

      expect(outcome).toEqual({ status: "fulfilled", value: undefined });
      expect(latest.isBusy).toBe(false);
      expect(latest.result).toMatchObject({
        command: expect.objectContaining({ idempotencyKey: submittedKey }),
        message: "Signup is Pending sync.",
        type: "pendingSync",
      });
      expect(pendingStorage.current()?.command.idempotencyKey).toBe(
        submittedKey,
      );
      expect(
        renderer.root.findByProps({
          accessibilityLabel: "Retry pending signup sync",
        }).props.accessibilityState,
      ).toMatchObject({ busy: false, disabled: false });

      lateSignup.resolve(signup);
      await flush();

      expect(latest.result).toMatchObject({
        command: expect.objectContaining({ idempotencyKey: submittedKey }),
        type: "pendingSync",
      });
      expect(pendingStorage.current()?.command.idempotencyKey).toBe(
        submittedKey,
      );
      expect(pendingStorage.storage.clearIfCurrent).not.toHaveBeenCalled();
    } finally {
      await act(async () => {
        lateSignup.resolve(signup);
        if (submission) {
          await Promise.allSettled([submission]);
        }
      });
      if (renderer) {
        await unmount(renderer);
      }
      jest.useRealTimers();
    }
  });

  it("does not bypass an unexpired Retry-After on manual retry", async () => {
    const now = jest
      .spyOn(Date, "now")
      .mockReturnValue(Date.parse("2026-08-17T10:00:00.000Z"));
    const pendingStorage = createStatefulStorage();
    const scheduledCommand: PendingSignupCommand = {
      body: signupBody,
      category: "serving",
      createdAt: "2026-08-17T09:59:00.000Z",
      helpNeedId: "need-1",
      idempotencyKey: "11111111-1111-4111-8111-111111111111",
      nextAttemptAt: "2026-08-17T10:05:00.000Z",
      organizationId: "organization-1",
      primaryMembershipId: "membership-1",
      serviceDateId: "date-1",
      version: 1,
    };
    await pendingStorage.storage.admit(scheduledCommand);
    const submitSignup = jest.fn<T14Api["submitSignup"]>();
    const api = {
      listMySignups: jest
        .fn<T14Api["listMySignups"]>()
        .mockResolvedValue({ items: [], nextCursor: null }),
      submitSignup,
    } as unknown as T14Api;
    let latest!: SignupSubmissionState;
    const renderer = await renderWithProvider(
      createController(),
      pendingStorage.storage,
      <SignupSubmissionProbe
        api={api}
        onState={(state) => { latest = state; }}
        storage={pendingStorage.storage}
      />,
    );

    await act(async () => {
      await latest.retry();
    });
    await flush();

    expect(api.listMySignups).toHaveBeenCalledTimes(1);
    expect(submitSignup).not.toHaveBeenCalled();
    expect(pendingStorage.current()?.command.nextAttemptAt).toBe(
      scheduledCommand.nextAttemptAt,
    );
    expect(latest.result).toMatchObject({
      command: expect.objectContaining({
        idempotencyKey: scheduledCommand.idempotencyKey,
      }),
      type: "pendingSync",
    });

    await unmount(renderer);
    now.mockRestore();
  });

  it("aborts and suppresses a signup completion after route parameters change", async () => {
    const lateSignup = createDeferred<SignupResponse>();
    const submitSignup = jest
      .fn<T14Api["submitSignup"]>()
      .mockReturnValue(lateSignup.promise);
    const api = { submitSignup } as unknown as T14Api;
    const pendingStorage = createStatefulStorage();
    const controller = createController();
    let latest!: SignupSubmissionState;
    let renderer = await renderWithProvider(
      controller,
      pendingStorage.storage,
      <SignupSubmissionProbe
        api={api}
        helpNeedId="need-1"
        onState={(state) => { latest = state; }}
        serviceDateId="date-1"
        storage={pendingStorage.storage}
      />,
    );

    try {
      act(() => {
        void latest.submit(signupBody);
      });
      await waitForCallCount(submitSignup, 1);
      const firstSignal = submitSignup.mock.calls[0]?.[4];

      await act(async () => {
        renderer.update(
          <AuthSessionProvider
            controller={controller}
            pendingSignupStorage={pendingStorage.storage}
          >
            <SignupSubmissionProbe
              api={api}
              helpNeedId="need-2"
              onState={(state) => { latest = state; }}
              serviceDateId="date-2"
              storage={pendingStorage.storage}
            />
          </AuthSessionProvider>,
        );
        await Promise.resolve();
        await Promise.resolve();
      });
      await flush();

      expect(firstSignal?.aborted).toBe(true);
      expect(latest.isBusy).toBe(false);
      expect(latest.result).toBeNull();
      expect(latest.pendingCommand).toMatchObject({
        helpNeedId: "need-1",
        serviceDateId: "date-1",
      });

      lateSignup.resolve(signup);
      await flush();

      expect(latest.result).toBeNull();
      expect(latest.pendingCommand).toMatchObject({
        helpNeedId: "need-1",
        serviceDateId: "date-1",
      });
    } finally {
      lateSignup.resolve(signup);
      await unmount(renderer);
    }
  });

  it("leaves query loading at the deadline, shows retry, and ignores a late result", async () => {
    jest.useFakeTimers();
    const lateDate = createDeferred<ServiceDateResponse>();
    const api = {
      getServiceDate: jest
        .fn<T14Api["getServiceDate"]>()
        .mockReturnValue(lateDate.promise),
    } as unknown as T14Api;
    const pendingStorage = createStatefulStorage();
    let latest!: ServiceDateState;
    let renderer: ReactTestRenderer | undefined;

    try {
      renderer = await renderWithProvider(
        createController(),
        pendingStorage.storage,
        <ServiceDateProbe
          api={api}
          onState={(state) => { latest = state; }}
        />,
      );

      await focus();
      expect(api.getServiceDate).toHaveBeenCalledTimes(1);
      expect(latest.isLoading).toBe(true);

      await advanceProductionDeadline();

      expect(latest.isLoading).toBe(false);
      expect(latest.error).toBe("The request timed out.");
      expect(latest.date).toBeNull();
      expect(readText(renderer.root)).toContain("The request timed out.");
      expect(
        renderer.root.findByProps({
          accessibilityLabel: "Retry loading date details",
        }).props.accessibilityRole,
      ).toBe("button");

      lateDate.resolve(serviceDate);
      await flush();

      expect(latest.isLoading).toBe(false);
      expect(latest.error).toBe("The request timed out.");
      expect(latest.date).toBeNull();
      expect(readText(renderer.root)).not.toContain(serviceDate.title);
    } finally {
      await act(async () => {
        lateDate.resolve(serviceDate);
        await Promise.resolve();
        await Promise.resolve();
      });
      if (renderer) {
        await unmount(renderer);
      }
      jest.useRealTimers();
    }
  });
});

function CaptureAuth({
  onCapture,
}: {
  readonly onCapture: (value: AuthContextValue) => void;
}) {
  onCapture(useAuth());
  return null;
}

function SignupSubmissionProbe({
  api,
  helpNeedId = "need-1",
  onState,
  serviceDateId = "date-1",
  storage,
}: {
  readonly api: T14Api;
  readonly helpNeedId?: string;
  readonly onState: (state: SignupSubmissionState) => void;
  readonly serviceDateId?: string;
  readonly storage: PendingSignupStorage;
}) {
  const state = useSignupSubmission(
    {
      category: "serving",
      helpNeedId,
      serviceDateId,
    },
    api,
    storage,
  );
  onState(state);

  return (
    <SignupComposerView
      category="serving"
      eligibleError={null}
      eligibleLoading={false}
      isBusy={state.isBusy}
      onRetryEligible={() => undefined}
      onRetryPending={() => {
        void state.retry();
      }}
      onSubmit={(body) => {
        void state.submit(body);
      }}
      participants={[]}
      pendingCommand={state.pendingCommand}
      primaryMembershipId="membership-1"
      result={state.result}
    />
  );
}

function ServiceDateProbe({
  api,
  onState,
}: {
  readonly api: T14Api;
  readonly onState: (state: ServiceDateState) => void;
}) {
  const state = useServiceDate("date-1", api);
  onState(state);

  return (
    <DateDetailView
      date={state.date}
      error={state.error}
      isFoodIncharge={false}
      isLoading={state.isLoading}
      membershipId="membership-1"
      onRetry={() => {
        void state.refresh();
      }}
    />
  );
}

async function renderWithProvider(
  controller: AuthSessionCoordinator,
  pendingSignupStorage: PendingSignupStorage,
  element: ReactNode,
): Promise<ReactTestRenderer> {
  let renderer!: ReactTestRenderer;
  await act(async () => {
    renderer = create(
      <AuthSessionProvider
        controller={controller}
        pendingSignupStorage={pendingSignupStorage}
      >
        {element}
      </AuthSessionProvider>,
    );
  });
  await flush();
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
    await Promise.resolve();
  });
}

async function advanceProductionDeadline(): Promise<void> {
  await act(async () => {
    await jest.advanceTimersByTimeAsync(10_000);
    await Promise.resolve();
    await Promise.resolve();
  });
  await flush();
}

async function flush(): Promise<void> {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

async function waitForCallCount(
  mock: { readonly mock: { readonly calls: readonly unknown[][] } },
  count: number,
): Promise<void> {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    if (mock.mock.calls.length >= count) {
      return;
    }
    await Promise.resolve();
  }
  throw new Error(`Expected ${count} calls, received ${mock.mock.calls.length}.`);
}

function createController(
  overrides: Partial<{
    restore: jest.MockedFunction<AuthSessionCoordinator["restore"]>;
    signIn: jest.MockedFunction<AuthSessionCoordinator["signIn"]>;
    signOut: jest.MockedFunction<AuthSessionCoordinator["signOut"]>;
    synchronize: jest.MockedFunction<AuthSessionCoordinator["synchronize"]>;
  }> = {},
): AuthSessionCoordinator {
  return {
    restore:
      overrides.restore ??
      jest
        .fn<AuthSessionCoordinator["restore"]>()
        .mockResolvedValue(sampleSession),
    signIn:
      overrides.signIn ??
      jest
        .fn<AuthSessionCoordinator["signIn"]>()
        .mockResolvedValue(sampleSession),
    signOut:
      overrides.signOut ??
      jest
        .fn<AuthSessionCoordinator["signOut"]>()
        .mockResolvedValue(undefined),
    synchronize:
      overrides.synchronize ??
      jest
        .fn<AuthSessionCoordinator["synchronize"]>()
        .mockResolvedValue(sampleSession),
  };
}

function createStatefulStorage(): {
  readonly current: () => PendingSignupSlot | null;
  readonly storage: jest.Mocked<PendingSignupStorage>;
} {
  let current: PendingSignupSlot | null = null;
  let nextEpoch = 0;
  const storage: jest.Mocked<PendingSignupStorage> = {
    admit: jest
      .fn<PendingSignupStorage["admit"]>()
      .mockImplementation(async (command): Promise<PendingSignupAdmission> => {
        if (current) {
          return { slot: current, type: "existing" };
        }

        current = { command, epoch: nextEpoch };
        nextEpoch += 1;
        return { slot: current, type: "admitted" };
      }),
    clearIfCurrent: jest
      .fn<PendingSignupStorage["clearIfCurrent"]>()
      .mockImplementation(async (slot) => {
        if (
          current?.epoch !== slot.epoch ||
          current.command.idempotencyKey !== slot.command.idempotencyKey
        ) {
          return false;
        }
        current = null;
        return true;
      }),
    load: jest
      .fn<PendingSignupStorage["load"]>()
      .mockImplementation(async () => current),
    loadForIdentity: jest
      .fn<PendingSignupStorage["loadForIdentity"]>()
      .mockImplementation(async (organizationId, membershipId) =>
        current?.command.organizationId === organizationId &&
        current.command.primaryMembershipId === membershipId
          ? current
          : null,
      ),
    purge: jest
      .fn<PendingSignupStorage["purge"]>()
      .mockImplementation(async () => {
        current = null;
      }),
    replaceIfCurrent: jest
      .fn<PendingSignupStorage["replaceIfCurrent"]>()
      .mockImplementation(async (slot, command) => {
        if (
          current?.epoch !== slot.epoch ||
          current.command.idempotencyKey !== slot.command.idempotencyKey
        ) {
          return null;
        }
        current = { command, epoch: slot.epoch };
        return current;
      }),
  };

  return {
    current: () => current,
    storage,
  };
}

function createDeferred<T>(): {
  readonly promise: Promise<T>;
  readonly reject: (reason?: unknown) => void;
  readonly resolve: (value: T | PromiseLike<T>) => void;
} {
  let reject!: (reason?: unknown) => void;
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
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
