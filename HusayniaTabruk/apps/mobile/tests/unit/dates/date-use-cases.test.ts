import { describe, expect, it, jest } from "@jest/globals";

import type {
  CursorPageOfServiceDateResponse,
  ServiceDateResponse,
} from "../../../src/core/api/auth-api";
import {
  filterOpenServiceDates,
  loadAllOpenServiceDates,
  OperationGeneration,
} from "../../../src/features/dates/date-use-cases";

const servingDate = createDate("date-1", "serving");
const cleanupDate = createDate("date-2", "cleanup");

describe("date use cases", () => {
  it("fully paginates and deduplicates by service-date ID", async () => {
    const loadPage = jest
      .fn<
        (
          cursor: string | undefined,
          signal?: AbortSignal,
        ) => Promise<CursorPageOfServiceDateResponse>
      >()
      .mockResolvedValueOnce({
        items: [servingDate],
        nextCursor: "next",
      })
      .mockResolvedValueOnce({
        items: [{ ...servingDate, title: "Updated" }, cleanupDate],
        nextCursor: null,
      });

    const result = await loadAllOpenServiceDates(loadPage);

    expect(result).toEqual([
      { ...servingDate, title: "Updated" },
      cleanupDate,
    ]);
    expect(loadPage).toHaveBeenNthCalledWith(1, undefined, undefined);
    expect(loadPage).toHaveBeenNthCalledWith(2, "next", undefined);
  });

  it("filters locally by open help category", () => {
    expect(
      filterOpenServiceDates([servingDate, cleanupDate], "serving"),
    ).toEqual([servingDate]);
    expect(
      filterOpenServiceDates([servingDate, cleanupDate], "foodPreparation"),
    ).toEqual([]);
  });

  it("rejects stale operation generations", () => {
    const gate = new OperationGeneration("dates");
    const first = gate.begin();
    const second = gate.begin();

    expect(gate.isCurrent(first)).toBe(false);
    expect(gate.isCurrent(second)).toBe(true);
    gate.supersede();
    expect(gate.isCurrent(second)).toBe(false);
  });
});

function createDate(
  id: string,
  category: "foodPreparation" | "serving" | "cleanup",
): ServiceDateResponse {
  return {
    cancellationDeadlineAt: "2026-08-20T10:00:00.000Z",
    endsAt: "2026-08-21T12:00:00.000Z",
    helpNeeds: [
      {
        availability: 4,
        category,
        id: `need-${id}`,
        instructions: "Please help",
        status: "open",
        version: 1,
      },
    ],
    id,
    instructions: "Date instructions",
    managerMembershipId: "manager-1",
    startsAt: "2026-08-21T10:00:00.000Z",
    status: "open",
    title: id,
    version: 1,
  };
}
