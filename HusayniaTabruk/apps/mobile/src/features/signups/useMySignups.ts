import { useFocusEffect } from "expo-router";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import {
  getMobileApi,
  type SignupResponse,
  type T14Api,
} from "../../core/api/auth-api";
import { subscribeFeatureRefresh } from "../../core/api/feature-refresh-bus";
import { createRequestScope } from "../../core/api/operation-runtime";
import {
  getPendingSignupStorage,
  type PendingSignupCommand,
  type PendingSignupSlot,
  type PendingSignupStorage,
} from "../../core/security/pending-signup-storage";
import { useAuth } from "../auth/useAuth";
import { OperationGeneration } from "../dates/date-use-cases";
import { isSignupForPendingCommand } from "./signup-sync-coordinator";
import { loadAllMySignups } from "./useSignupSubmission";

export interface MySignupsState {
  readonly error: string | null;
  readonly isLoading: boolean;
  readonly pendingCommand: PendingSignupCommand | null;
  readonly refresh: () => Promise<void>;
  readonly signups: readonly SignupResponse[];
}

export function useMySignups(
  api: T14Api = getMobileApi(),
  storage: PendingSignupStorage = getPendingSignupStorage(),
): MySignupsState {
  const { runAuthenticatedRequest, session } = useAuth();
  const organizationId = session?.actor.organization.id ?? "";
  const primaryMembershipId = session?.actor.membership.id ?? "";
  const identity = `${organizationId}:${primaryMembershipId}`;
  const [signups, setSignups] = useState<readonly SignupResponse[]>([]);
  const [pendingCommand, setPendingCommand] =
    useState<PendingSignupCommand | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const generation = useMemo(
    () => new OperationGeneration(identity),
    [identity],
  );
  const activeController = useRef<AbortController | null>(null);

  const refresh = useCallback(async (): Promise<void> => {
    if (!organizationId || !primaryMembershipId) {
      return;
    }

    activeController.current?.abort();
    const controller = new AbortController();
    activeController.current = controller;
    const currentGeneration = generation.begin();
    const scope = createRequestScope(controller.signal);
    setIsLoading(true);
    setError(null);
    let nextSlot: PendingSignupSlot | null = null;

    try {
      const loadedSlot = await storage.loadForIdentity(
        organizationId,
        primaryMembershipId,
        controller.signal,
      );
      nextSlot = loadedSlot;
      const nextSignups = await runAuthenticatedRequest(
        (accessToken) => loadAllMySignups(api, accessToken, scope.signal),
        scope.signal,
      );
      const hasAuthoritativeMatch =
        loadedSlot !== null &&
        nextSignups.some((signup) =>
          isSignupForPendingCommand(signup, loadedSlot.command),
        );
      let nextPendingCommand = loadedSlot?.command ?? null;
      if (loadedSlot && hasAuthoritativeMatch) {
        const cleared = await storage.clearIfCurrent(
          loadedSlot,
          controller.signal,
        );
        if (cleared) {
          nextPendingCommand = null;
        } else {
          const currentSlot = await storage.loadForIdentity(
            organizationId,
            primaryMembershipId,
            controller.signal,
          );
          nextPendingCommand =
            currentSlot &&
            !nextSignups.some((signup) =>
              isSignupForPendingCommand(signup, currentSlot.command),
            )
              ? currentSlot.command
              : null;
        }
      }

      if (
        generation.isCurrent(currentGeneration) &&
        !controller.signal.aborted
      ) {
        setSignups(nextSignups);
        setPendingCommand(nextPendingCommand);
      }
    } catch {
      if (
        generation.isCurrent(currentGeneration) &&
        !controller.signal.aborted
      ) {
        setPendingCommand(nextSlot?.command ?? null);
        setError(
          "Personal signups could not be loaded.",
        );
      }
    } finally {
      scope.dispose();
      if (generation.isCurrent(currentGeneration)) {
        setIsLoading(false);
      }
    }
  }, [
    api,
    generation,
    organizationId,
    primaryMembershipId,
    runAuthenticatedRequest,
    storage,
  ]);

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
        setPendingCommand(null);
      }
    });

    return () => {
      unsubscribe();
      activeController.current?.abort();
      generation.supersede();
    };
  }, [generation, identity, refresh]);

  return { error, isLoading, pendingCommand, refresh, signups };
}
