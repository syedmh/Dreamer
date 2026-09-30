import type {
  RosterResponse,
  SignupResponse,
  T18Api,
} from "../../../core/api/auth-api";

export async function loadManagedRoster(
  api: T18Api,
  accessToken: string,
  dateId: string,
  signal?: AbortSignal,
): Promise<SignupResponse[]> {
  const signups = new Map<string, SignupResponse>();
  const seenCursors = new Set<string>();
  let cursor: string | undefined;

  do {
    const page: RosterResponse = await api.getManagedRoster(
      accessToken,
      dateId,
      { cursor, pageSize: 100 },
      signal,
    );
    for (const signup of page.items) {
      signups.set(signup.id, signup);
    }

    const next = page.nextCursor ?? undefined;
    if (!next) {
      break;
    }
    if (seenCursors.has(next)) {
      throw new Error("The managed roster cursor repeated.");
    }
    seenCursors.add(next);
    cursor = next;
  } while (!signal?.aborted);

  return Array.from(signups.values()).sort(compareSignups);
}

function compareSignups(left: SignupResponse, right: SignupResponse): number {
  if (left.status === "waitlisted" && right.status === "waitlisted") {
    return (
      (left.waitlistOrder ?? Number.MAX_SAFE_INTEGER) -
        (right.waitlistOrder ?? Number.MAX_SAFE_INTEGER) ||
      left.id.localeCompare(right.id)
    );
  }

  return left.submittedAt.localeCompare(right.submittedAt) ||
    left.id.localeCompare(right.id);
}
