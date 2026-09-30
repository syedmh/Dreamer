import { useFocusEffect } from "expo-router";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import {
  getMobileApi,
  type ServiceDateResponse,
  type T14Api,
} from "../../core/api/auth-api";
import { subscribeFeatureRefresh } from "../../core/api/feature-refresh-bus";
import { createRequestScope } from "../../core/api/operation-runtime";
import { useAuth } from "../auth/useAuth";
import {
  loadOpenDatesWithApi,
  OperationGeneration,
} from "./date-use-cases";

export interface OpenDatesState {
  readonly dates: readonly ServiceDateResponse[];
  readonly error: string | null;
  readonly isLoading: boolean;
  readonly refresh: () => Promise<void>;
}

export function useOpenDates(api: T14Api = getMobileApi()): OpenDatesState {
  const { runAuthenticatedRequest, session } = useAuth();
  const identity = session
    ? `${session.actor.organization.id}:${session.actor.membership.id}`
    : "";
  const [dates, setDates] = useState<readonly ServiceDateResponse[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const generation = useMemo(
    () => new OperationGeneration(identity),
    [identity],
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
      const nextDates = await runAuthenticatedRequest(
        (accessToken) => loadOpenDatesWithApi(api, accessToken, scope.signal),
        scope.signal,
      );
      if (
        generation.isCurrent(currentGeneration) &&
        !controller.signal.aborted
      ) {
        setDates(nextDates);
      }
    } catch (loadError) {
      if (
        generation.isCurrent(currentGeneration) &&
        !controller.signal.aborted
      ) {
        setError(toMessage(loadError));
      }
    } finally {
      scope.dispose();
      if (generation.isCurrent(currentGeneration)) {
        setIsLoading(false);
      }
    }
  }, [api, generation, runAuthenticatedRequest]);

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
        setDates([]);
      }
    });

    return () => {
      unsubscribe();
      activeController.current?.abort();
      generation.supersede();
    };
  }, [generation, identity, refresh]);

  return {
    dates,
    error,
    isLoading,
    refresh,
  };
}

function toMessage(error: unknown): string {
  return error instanceof Error ? error.message : "Open dates could not be loaded.";
}
