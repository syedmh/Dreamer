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
import { OperationGeneration } from "./date-use-cases";

export interface ServiceDateState {
  readonly date: ServiceDateResponse | null;
  readonly error: string | null;
  readonly isLoading: boolean;
  readonly refresh: () => Promise<void>;
}

export function useServiceDate(
  dateId: string,
  api: T14Api = getMobileApi(),
): ServiceDateState {
  const { runAuthenticatedRequest, session } = useAuth();
  const identity = session
    ? `${session.actor.organization.id}:${session.actor.membership.id}`
    : "";
  const [date, setDate] = useState<ServiceDateResponse | null>(null);
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
      const nextDate = await runAuthenticatedRequest(
        (accessToken) =>
          api.getServiceDate(accessToken, dateId, scope.signal),
        scope.signal,
      );
      if (
        generation.isCurrent(currentGeneration) &&
        !controller.signal.aborted
      ) {
        setDate(nextDate);
      }
    } catch (loadError) {
      if (
        generation.isCurrent(currentGeneration) &&
        !controller.signal.aborted
      ) {
        setError(
          loadError instanceof Error
            ? loadError.message
            : "The service date could not be loaded.",
        );
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
        setDate(null);
      }
    });

    return () => {
      unsubscribe();
      activeController.current?.abort();
      generation.supersede();
    };
  }, [generation, identity, refresh]);

  return { date, error, isLoading, refresh };
}
