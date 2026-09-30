import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import {
  getMobileApi,
  type SignupResponse,
  type SubmitSignupRequest,
  type T14Api,
} from "../../core/api/auth-api";
import {
  publishFeatureRefresh,
  subscribeFeatureRefresh,
} from "../../core/api/feature-refresh-bus";
import { createRequestScope } from "../../core/api/operation-runtime";
import {
  getPendingSignupStorage,
  type HelpCategory,
  type PendingSignupCommand,
  type PendingSignupStorage,
} from "../../core/security/pending-signup-storage";
import { useAuth } from "../auth/useAuth";
import { OperationGeneration } from "../dates/date-use-cases";
import {
  SignupSyncCoordinator,
  type SignupSyncResult,
  type SignupSyncSignals,
} from "./signup-sync-coordinator";

export interface SignupSubmissionState {
  readonly isBusy: boolean;
  readonly pendingCommand: PendingSignupCommand | null;
  readonly result: SignupSyncResult | null;
  readonly retry: () => Promise<void>;
  readonly submit: (body: SubmitSignupRequest) => Promise<void>;
}

export interface UseSignupSubmissionInput {
  readonly category: HelpCategory | null;
  readonly helpNeedId: string;
  readonly serviceDateId: string;
}

export function useSignupSubmission(
  input: UseSignupSubmissionInput,
  api: T14Api = getMobileApi(),
  storage: PendingSignupStorage = getPendingSignupStorage(),
): SignupSubmissionState {
  const { runAuthenticatedRequest, session } = useAuth();
  const organizationId = session?.actor.organization.id ?? "";
  const primaryMembershipId = session?.actor.membership.id ?? "";
  const identity = `${organizationId}:${primaryMembershipId}`;
  const operationScope = `${identity}:${input.serviceDateId}:${input.helpNeedId}`;
  const [isBusy, setIsBusy] = useState(false);
  const [pendingCommand, setPendingCommand] =
    useState<PendingSignupCommand | null>(null);
  const [result, setResult] = useState<SignupSyncResult | null>(null);
  const generation = useMemo(
    () => new OperationGeneration(operationScope),
    [operationScope],
  );
  const activeController = useRef<AbortController | null>(null);

  const coordinator = useMemo(
    () =>
      new SignupSyncCoordinator({
        storage,
        send: (command, signal) =>
          runAuthenticatedRequest(
            (accessToken) =>
              api.submitSignup(
                accessToken,
                command.helpNeedId,
                command.idempotencyKey,
                command.body,
                signal,
              ),
            signal,
          ),
        reconcile: async (command, signal) => {
          const signups = await runAuthenticatedRequest(
            (accessToken) =>
              loadAllMySignups(api, accessToken, signal),
            signal,
          );
          return (
            signups.find(
              (signup) =>
                signup.helpNeedId === command.helpNeedId &&
                signup.serviceDateId === command.serviceDateId &&
                signup.primaryContact.membershipId ===
                  command.primaryMembershipId,
            ) ?? null
          );
        },
      }),
    [api, runAuthenticatedRequest, storage],
  );

  const applyResult = useCallback((nextResult: SignupSyncResult | null): void => {
    setResult(nextResult);
    setPendingCommand(
      nextResult?.type === "pendingSync" ? nextResult.command : null,
    );

    if (nextResult && nextResult.type !== "pendingSync") {
      publishFeatureRefresh("signups-changed");
    }
  }, []);

  const execute = useCallback(
    async (
      operation: (
        signals: SignupSyncSignals,
      ) => Promise<SignupSyncResult | null>,
    ): Promise<void> => {
      activeController.current?.abort();
      const controller = new AbortController();
      activeController.current = controller;
      const currentGeneration = generation.begin();
      const scope = createRequestScope(controller.signal);
      setIsBusy(true);

      try {
        const nextResult = await operation({
          operationSignal: controller.signal,
          requestSignal: scope.signal,
        });
        if (
          generation.isCurrent(currentGeneration) &&
          !controller.signal.aborted
        ) {
          applyResult(nextResult);
        }
      } catch (submissionError) {
        if (
          generation.isCurrent(currentGeneration) &&
          !controller.signal.aborted
        ) {
          applyResult({
            message:
              submissionError instanceof Error
                ? submissionError.message
                : "The signup could not be prepared securely.",
            type: "permanentError",
          });
        }
      } finally {
        scope.dispose();
        if (generation.isCurrent(currentGeneration)) {
          setIsBusy(false);
        }
      }
    },
    [applyResult, generation],
  );

  const retry = useCallback(async (): Promise<void> => {
    if (!organizationId || !primaryMembershipId || isBusy) {
      return;
    }

    await execute((signals) =>
      coordinator.retry(
        organizationId,
        primaryMembershipId,
        signals,
      ),
    );
  }, [
    coordinator,
    execute,
    isBusy,
    organizationId,
    primaryMembershipId,
  ]);

  const submit = useCallback(
    async (body: SubmitSignupRequest): Promise<void> => {
      if (
        !organizationId ||
        !primaryMembershipId ||
        !input.category ||
        isBusy ||
        pendingCommand
      ) {
        return;
      }

      await execute((signals) =>
        coordinator.submit(
          {
            body,
            category: input.category!,
            helpNeedId: input.helpNeedId,
            organizationId,
            primaryMembershipId,
            serviceDateId: input.serviceDateId,
          },
          signals,
        ),
      );
    },
    [
      coordinator,
      execute,
      input.category,
      input.helpNeedId,
      input.serviceDateId,
      isBusy,
      organizationId,
      pendingCommand,
      primaryMembershipId,
    ],
  );

  useEffect(() => {
    let active = true;

    void (async () => {
      await Promise.resolve();
      if (!active) {
        return;
      }

      setIsBusy(false);
      setPendingCommand(null);
      setResult(null);
      if (!organizationId || !primaryMembershipId) {
        return;
      }

      try {
        const slot = await storage.loadForIdentity(
          organizationId,
          primaryMembershipId,
        );
        if (!active) {
          return;
        }

        setPendingCommand(slot?.command ?? null);
        if (
          slot &&
          slot.command.serviceDateId === input.serviceDateId &&
          slot.command.helpNeedId === input.helpNeedId
        ) {
          setResult({
            command: slot.command,
            message: "Signup is Pending sync.",
            type: "pendingSync",
          });
        }
      } catch (loadError) {
        if (
          active &&
          (!(loadError instanceof Error) ||
            loadError.name !== "AbortError")
        ) {
          setResult({
            message: "The pending signup could not be loaded securely.",
            type: "permanentError",
          });
        }
      }
    })();

    return () => {
      active = false;
      activeController.current?.abort();
      generation.supersede();
    };
  }, [
    generation,
    identity,
    input.helpNeedId,
    input.serviceDateId,
    organizationId,
    primaryMembershipId,
    storage,
  ]);

  useEffect(() => {
    let active = true;
    const unsubscribe = subscribeFeatureRefresh((event) => {
      if (event === "session-cleared") {
        activeController.current?.abort();
        generation.supersede();
        setPendingCommand(null);
        setResult(null);
        setIsBusy(false);
      } else if (
        event === "signups-changed" &&
        organizationId &&
        primaryMembershipId
      ) {
        void storage
          .loadForIdentity(organizationId, primaryMembershipId)
          .then((slot) => {
            if (!active) {
              return;
            }
            setPendingCommand(slot?.command ?? null);
            if (!slot) {
              setResult((current) =>
                current?.type === "pendingSync" ? null : current,
              );
            }
          })
          .catch(() => undefined);
      }
    });

    return () => {
      active = false;
      unsubscribe();
    };
  }, [
    generation,
    identity,
    organizationId,
    primaryMembershipId,
    storage,
  ]);

  return { isBusy, pendingCommand, result, retry, submit };
}

export async function loadAllMySignups(
  api: T14Api,
  accessToken: string,
  signal?: AbortSignal,
): Promise<SignupResponse[]> {
  const byId = new Map<string, SignupResponse>();
  const seenCursors = new Set<string>();
  let cursor: string | undefined;

  do {
    const page = await api.listMySignups(
      accessToken,
      { cursor, pageSize: 100 },
      signal,
    );
    for (const signup of page.items) {
      byId.set(signup.id, signup);
    }

    const nextCursor = page.nextCursor ?? undefined;
    if (!nextCursor) {
      break;
    }
    if (seenCursors.has(nextCursor)) {
      throw new Error("The personal-signup cursor repeated.");
    }

    seenCursors.add(nextCursor);
    cursor = nextCursor;
  } while (!signal?.aborted);

  return Array.from(byId.values());
}
