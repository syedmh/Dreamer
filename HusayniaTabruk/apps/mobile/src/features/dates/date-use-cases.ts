import type {
  CursorPageOfServiceDateResponse,
  ServiceDateResponse,
  T14Api,
} from "../../core/api/auth-api";
import type { HelpCategory } from "../../core/security/pending-signup-storage";

export type DateCategoryFilter = "all" | HelpCategory;

export async function loadAllOpenServiceDates(
  loadPage: (
    cursor: string | undefined,
    signal?: AbortSignal,
  ) => Promise<CursorPageOfServiceDateResponse>,
  signal?: AbortSignal,
): Promise<ServiceDateResponse[]> {
  const byId = new Map<string, ServiceDateResponse>();
  const seenCursors = new Set<string>();
  let cursor: string | undefined;

  do {
    const page = await loadPage(cursor, signal);
    for (const serviceDate of page.items) {
      byId.set(serviceDate.id, serviceDate);
    }

    const nextCursor = page.nextCursor ?? undefined;
    if (!nextCursor) {
      break;
    }

    if (seenCursors.has(nextCursor)) {
      throw new Error("The service-date cursor repeated.");
    }

    seenCursors.add(nextCursor);
    cursor = nextCursor;
  } while (!signal?.aborted);

  return Array.from(byId.values());
}

export function filterOpenServiceDates(
  dates: readonly ServiceDateResponse[],
  category: DateCategoryFilter,
): ServiceDateResponse[] {
  if (category === "all") {
    return [...dates];
  }

  return dates.filter((serviceDate) =>
    serviceDate.helpNeeds.some(
      (need) => need.status === "open" && need.category === category,
    ),
  );
}

export async function loadOpenDatesWithApi(
  api: T14Api,
  accessToken: string,
  signal?: AbortSignal,
): Promise<ServiceDateResponse[]> {
  return loadAllOpenServiceDates(
    (cursor, pageSignal) =>
      api.listOpenServiceDates(
        accessToken,
        { cursor, pageSize: 100 },
        pageSignal,
      ),
    signal,
  );
}

export class OperationGeneration {
  private value = 0;
  public readonly scopeKey: string;

  public constructor(scopeKey = "") {
    this.scopeKey = scopeKey;
  }

  public begin(): number {
    this.value += 1;
    return this.value;
  }

  public isCurrent(generation: number): boolean {
    return this.value === generation;
  }

  public supersede(): void {
    this.value += 1;
  }
}
