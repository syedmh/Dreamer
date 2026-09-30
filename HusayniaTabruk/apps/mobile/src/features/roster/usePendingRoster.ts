import { useFocusEffect } from "expo-router";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import {
  ApiClientError,
  getMobileApi,
  type SignupResponse,
  type T14Api,
} from "../../core/api/auth-api";
import { subscribeFeatureRefresh } from "../../core/api/feature-refresh-bus";
import { createRequestScope } from "../../core/api/operation-runtime";
import { useAuth } from "../auth/useAuth";
import { OperationGeneration } from "../dates/date-use-cases";
import { loadPendingRosterWithApi } from "./roster-use-cases";

export interface PendingRosterState {
  readonly error: string | null;
  readonly isLoading: boolean;
  readonly refresh: () => Promise<void>;
  readonly signups: readonly SignupResponse[];
}

export function usePendingRoster(
  dateId: string,
  api: T14Api = getMobileApi(),
): PendingRosterState {
  const { runAuthenticatedRequest, session } = useAuth();
  const identity = session
    ? `${session.actor.organization.id}:${session.actor.membership.id}`
    : "";
  const [signups, setSignups] = useState<readonly SignupResponse[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const generation = useMemo(
    () => new OperationGeneration(`${identity}:${dateId}`),
    [dateId, identity],
  );
  const activeController = useRef<AbortController | null>(null);

  const refresh = useCallback(async (): Promise<void> => {
    activeController.current?.abort();
    const controller = new AbortController();
    activeController.current = controller;
    const currentGeneration = generation.begin();
    const scope = createRequestScope(controller.signal);
    setIsLoading(true);
    setError(null);

    try {
      const nextSignups = await runAuthenticatedRequest(
        (accessToken) =>
          loadPendingRosterWithApi(
            api,
            accessToken,
            dateId,
            scope.signal,
          ),
        scope.signal,
      );
      if (
        generation.isCurrent(currentGeneration) &&
        !controller.signal.aborted
      ) {
        setSignups(nextSignups);
      }
    } catch (loadError) {
      if (
        generation.isCurrent(currentGeneration) &&
        !controller.signal.aborted
      ) {
        if (loadError instanceof ApiClientError && loadError.status === 404) {
          setSignups([]);
        }
        setError(toRosterMessage(loadError));
      }
    } finally {
      scope.dispose();
      if (generation.isCurrent(currentGeneration)) {
        setIsLoading(false);
      }
    }
  }, [api, dateId, generation, runAuthenticatedRequest]);

  useFocusEffect(
    useCallback(() => {
      void refresh();
      return () => {
        activeController.current?.abort();
        generation.supersede();
      };
    }, [generation, refresh]),
  );

  useEffect(() => {
    const unsubscribe = subscribeFeatureRefresh((event) => {
      if (event === "signups-changed") {
        void refresh();
      } else if (event === "session-cleared") {
        activeController.current?.abort();
        generation.supersede();
        setSignups([]);
      }
    });

    return () => {
      unsubscribe();
      activeController.current?.abort();
      generation.supersede();
    };
  }, [generation, identity, refresh]);

  return { error, isLoading, refresh, signups };
}

function toRosterMessage(error: unknown): string {
  if (error instanceof ApiClientError && error.status === 404) {
    return "The pending roster is unavailable.";
  }

  return error instanceof Error
    ? error.message
    : "The pending roster could not be loaded.";
}
