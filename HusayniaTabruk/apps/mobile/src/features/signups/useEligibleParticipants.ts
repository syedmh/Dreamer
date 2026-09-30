import { useFocusEffect } from "expo-router";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import {
  getMobileApi,
  type CursorPageOfEligibleSignupParticipantResponse,
  type T14Api,
} from "../../core/api/auth-api";
import { subscribeFeatureRefresh } from "../../core/api/feature-refresh-bus";
import { createRequestScope } from "../../core/api/operation-runtime";
import type { EligibleSignupParticipantResponse } from "../../core/api/generated/api-contract-client";
import { useAuth } from "../auth/useAuth";
import { OperationGeneration } from "../dates/date-use-cases";

export interface EligibleParticipantsState {
  readonly error: string | null;
  readonly isLoading: boolean;
  readonly participants: readonly EligibleSignupParticipantResponse[];
  readonly refresh: () => Promise<void>;
}

export async function loadAllEligibleParticipants(
  loadPage: (
    cursor: string | undefined,
    signal?: AbortSignal,
  ) => Promise<CursorPageOfEligibleSignupParticipantResponse>,
  signal?: AbortSignal,
): Promise<EligibleSignupParticipantResponse[]> {
  const byId = new Map<string, EligibleSignupParticipantResponse>();
  const seenCursors = new Set<string>();
  let cursor: string | undefined;

  do {
    const page = await loadPage(cursor, signal);
    for (const participant of page.items) {
      byId.set(participant.membershipId, participant);
    }

    const nextCursor = page.nextCursor ?? undefined;
    if (!nextCursor) {
      break;
    }
    if (seenCursors.has(nextCursor)) {
      throw new Error("The eligible-participant cursor repeated.");
    }

    seenCursors.add(nextCursor);
    cursor = nextCursor;
  } while (!signal?.aborted);

  return Array.from(byId.values());
}

export function useEligibleParticipants(
  api: T14Api = getMobileApi(),
): EligibleParticipantsState {
  const { runAuthenticatedRequest, session } = useAuth();
  const identity = session
    ? `${session.actor.organization.id}:${session.actor.membership.id}`
    : "";
  const [participants, setParticipants] = useState<
    readonly EligibleSignupParticipantResponse[]
  >([]);
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
      const nextParticipants = await runAuthenticatedRequest(
        (accessToken) =>
          loadAllEligibleParticipants(
            (cursor, signal) =>
              api.listEligibleSignupParticipants(
                accessToken,
                { cursor, pageSize: 100 },
                signal,
              ),
            scope.signal,
          ),
        scope.signal,
      );
      if (
        generation.isCurrent(currentGeneration) &&
        !controller.signal.aborted
      ) {
        setParticipants(nextParticipants);
      }
    } catch (loadError) {
      if (
        generation.isCurrent(currentGeneration) &&
        !controller.signal.aborted
      ) {
        setError(
          loadError instanceof Error
            ? loadError.message
            : "Eligible members could not be loaded.",
        );
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

  useEffect(
    () => {
      const unsubscribe = subscribeFeatureRefresh((event) => {
        if (event === "session-cleared") {
          activeController.current?.abort();
          generation.supersede();
          setParticipants([]);
        }
      });

      return () => {
        unsubscribe();
        activeController.current?.abort();
        generation.supersede();
      };
    },
    [generation, identity],
  );

  return { error, isLoading, participants, refresh };
}
