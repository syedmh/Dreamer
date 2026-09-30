import type {
  RosterResponse,
  SignupResponse,
  T14Api,
} from "../../core/api/auth-api";

export async function loadPendingRoster(
  loadPage: (
    cursor: string | undefined,
    signal?: AbortSignal,
  ) => Promise<RosterResponse>,
  signal?: AbortSignal,
): Promise<SignupResponse[]> {
  const byId = new Map<string, SignupResponse>();
  const seenCursors = new Set<string>();
  let cursor: string | undefined;

  do {
    const page = await loadPage(cursor, signal);
    for (const signup of page.items) {
      if (signup.status === "pending") {
        byId.set(signup.id, signup);
      }
    }

    const nextCursor = page.nextCursor ?? undefined;
    if (!nextCursor) {
      break;
    }
    if (seenCursors.has(nextCursor)) {
      throw new Error("The roster cursor repeated.");
    }

    seenCursors.add(nextCursor);
    cursor = nextCursor;
  } while (!signal?.aborted);

  return Array.from(byId.values());
}

export async function loadPendingRosterWithApi(
  api: T14Api,
  accessToken: string,
  dateId: string,
  signal?: AbortSignal,
): Promise<SignupResponse[]> {
  return loadPendingRoster(
    (cursor, pageSignal) =>
      api.getManagedRoster(
        accessToken,
        dateId,
        { cursor, pageSize: 100 },
        pageSignal,
      ),
    signal,
  );
}
