import { useFocusEffect } from "expo-router";
import { useCallback, useEffect, useRef, useState } from "react";

import {
  getMobileApi,
  type SignupResponse,
  type T18Api,
} from "../../../core/api/auth-api";
import { publishFeatureRefresh } from "../../../core/api/feature-refresh-bus";
import { createRequestScope } from "../../../core/api/operation-runtime";
import {
  IdempotentCommandStore,
  toManagementError,
} from "../../admin/coordination-management";
import { useAuth } from "../../auth/useAuth";

interface WithdrawalCommand {
  readonly expectedVersion: number;
  readonly signupId: string;
}

export interface SignupCancellationManagementState {
  readonly busySignupId: string | null;
  readonly cancellationDeadlines: Readonly<Record<string, string>>;
  readonly confirmedWithdrawalIds: readonly string[];
  readonly error: string | null;
  readonly pendingOutcome: string | null;
  readonly refresh: () => Promise<void>;
  readonly withdraw: (signup: SignupResponse) => Promise<void>;
}

export function useSignupCancellationManagement(
  signups: readonly SignupResponse[],
  api: T18Api = getMobileApi(),
): SignupCancellationManagementState {
  const { runAuthenticatedRequest } = useAuth();
  const [cancellationDeadlines, setCancellationDeadlines] = useState<
    Readonly<Record<string, string>>
  >({});
  const [busySignupId, setBusySignupId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pendingOutcome, setPendingOutcome] = useState<string | null>(null);
  const [confirmedWithdrawalIds, setConfirmedWithdrawalIds] = useState<
    readonly string[]
  >([]);
  const confirmedWithdrawals = useRef(new Set<string>());
  const busyRef = useRef(false);
  const controller = useRef<AbortController | null>(null);
  const commandStore = useRef(
    new IdempotentCommandStore<WithdrawalCommand>(),
  );

  const refresh = useCallback(async () => {
    const dateIds = Array.from(
      new Set(
        signups
          .filter((signup) => signup.status === "approved")
          .map((signup) => signup.serviceDateId),
      ),
    );
    if (dateIds.length === 0) {
      setCancellationDeadlines({});
      return;
    }

    controller.current?.abort();
    const nextController = new AbortController();
    controller.current = nextController;
    const scope = createRequestScope(nextController.signal);
    try {
      const dates = await runAuthenticatedRequest(
        (accessToken) =>
          Promise.all(
            dateIds.map((dateId) =>
              api.getServiceDate(accessToken, dateId, scope.signal),
            ),
          ),
        scope.signal,
      );
      if (!nextController.signal.aborted) {
        setCancellationDeadlines(
          Object.fromEntries(
            dates.map((date) => [date.id, date.cancellationDeadlineAt]),
          ),
        );
        setPendingOutcome(null);
      }
    } catch (loadError) {
      if (!nextController.signal.aborted) {
        setError(toManagementError(loadError, "signup status").message);
      }
    } finally {
      scope.dispose();
    }
  }, [api, runAuthenticatedRequest, signups]);

  const withdraw = useCallback(
    async (signup: SignupResponse) => {
      if (
        busyRef.current ||
        confirmedWithdrawals.current.has(signup.id)
      ) {
        return;
      }
      busyRef.current = true;
      const action = `withdraw:${signup.id}`;
      const command = commandStore.current.get(action, () => ({
        expectedVersion: signup.signupVersion,
        signupId: signup.id,
      }));
      const scope = createRequestScope();
      setBusySignupId(signup.id);
      setError(null);
      setPendingOutcome(null);
      try {
        const updated = await runAuthenticatedRequest(
          (accessToken) =>
            api.withdrawSignup(
              accessToken,
              command.request.signupId,
              command.key,
              command.request.expectedVersion,
              {},
              scope.signal,
            ),
          scope.signal,
        );
        if (updated.status === "withdrawn") {
          confirmedWithdrawals.current.add(updated.id);
          setConfirmedWithdrawalIds(
            Array.from(confirmedWithdrawals.current),
          );
        }
        commandStore.current.confirm(action);
        publishFeatureRefresh("roster-changed");
        publishFeatureRefresh("signups-changed");
      } catch (withdrawError) {
        const viewError = toManagementError(withdrawError, "signup");
        if (viewError.kind === "unknown") {
          setPendingOutcome(viewError.message);
        } else {
          commandStore.current.confirm(action);
          setError(viewError.message);
        }
      } finally {
        scope.dispose();
        busyRef.current = false;
        setBusySignupId(null);
      }
    },
    [api, runAuthenticatedRequest],
  );

  useEffect(() => {
    commandStore.current.confirmMatching((command) =>
      signups.some(
        (signup) =>
          signup.id === command.signupId &&
          signup.status === "withdrawn",
      ),
    );
  }, [signups]);

  useFocusEffect(
    useCallback(() => {
      void refresh();
      return () => controller.current?.abort();
    }, [refresh]),
  );

  return {
    busySignupId,
    cancellationDeadlines,
    confirmedWithdrawalIds,
    error,
    pendingOutcome,
    refresh,
    withdraw,
  };
}
