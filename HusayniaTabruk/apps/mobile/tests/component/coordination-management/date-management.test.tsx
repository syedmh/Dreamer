import { describe, expect, it, jest } from "@jest/globals";
import { ScrollView, TextInput } from "react-native";
import { act, create } from "react-test-renderer";

import type { ServiceDateResponse } from "../../../src/core/api/auth-api";
import { DateManagementView } from "../../../src/features/admin/DateManagementScreen";

const date: ServiceDateResponse = {
  cancellationDeadlineAt: "2026-08-20T10:00:00.000Z",
  endsAt: "2026-08-21T12:00:00.000Z",
  helpNeeds: [
    {
      availability: 3,
      category: "serving",
      id: "need-1",
      instructions: "Serve food",
      status: "open",
      version: 2,
    },
  ],
  id: "date-1",
  instructions: "Arrive early",
  managerMembershipId: "manager-1",
  startsAt: "2026-08-21T10:00:00.000Z",
  status: "draft",
  title: "Service day",
  version: 4,
};

describe("date coordination management", () => {
  it("renders publish/edit/close/cancel controls with exact cancellation deadline fields", async () => {
    const callbacks = createCallbacks();
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <DateManagementView
          confirmation={null}
          date={date}
          error={null}
          isBusy={false}
          onChange={callbacks.onChange}
          onConfirm={callbacks.onConfirm}
          onEditNeed={callbacks.onEditNeed}
          onRefresh={callbacks.onRefresh}
          onRequestConfirmation={callbacks.onRequestConfirmation}
          onSave={callbacks.onSave}
          onPublish={callbacks.onPublish}
          pendingOutcome={null}
          values={{
            cancellationDeadlineAt: date.cancellationDeadlineAt,
            endsAt: date.endsAt,
            instructions: date.instructions,
            startsAt: date.startsAt,
            title: date.title,
          }}
        />,
      );
    });

    expect(renderer.root.findAllByType(ScrollView)).toHaveLength(1);
    expect(
      renderer.root.findByProps({
        accessibilityLabel: "Cancellation deadline in UTC",
      }).props.value,
    ).toBe("2026-08-20T10:00:00.000Z");
    expect(
      renderer.root.findByProps({ accessibilityLabel: "Publish service date" }),
    ).toBeDefined();
    expect(
      renderer.root.findByProps({ accessibilityLabel: "Save date changes" }),
    ).toBeDefined();
    expect(
      renderer.root.findByProps({ accessibilityLabel: "Close service date" }),
    ).toBeDefined();
    expect(
      renderer.root.findByProps({ accessibilityLabel: "Cancel service date" }),
    ).toBeDefined();
    expect(renderer.root.findAllByType(TextInput).length).toBeGreaterThan(4);
  });

  it("requires an explicit accessible confirmation and never reports timeout success", async () => {
    const callbacks = createCallbacks();
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <DateManagementView
          confirmation="cancel"
          date={{ ...date, status: "open" }}
          error={null}
          isBusy
          onChange={callbacks.onChange}
          onConfirm={callbacks.onConfirm}
          onEditNeed={callbacks.onEditNeed}
          onRefresh={callbacks.onRefresh}
          onRequestConfirmation={callbacks.onRequestConfirmation}
          onSave={callbacks.onSave}
          onPublish={callbacks.onPublish}
          pendingOutcome="The cancellation outcome is unknown. Refresh status before retrying."
          values={{
            cancellationDeadlineAt: date.cancellationDeadlineAt,
            endsAt: date.endsAt,
            instructions: date.instructions,
            startsAt: date.startsAt,
            title: date.title,
          }}
        />,
      );
    });

    const confirm = renderer.root.findByProps({
      accessibilityLabel: "Confirm cancel service date",
    });
    expect(confirm.props.accessibilityRole).toBe("button");
    expect(confirm.props.accessibilityState).toMatchObject({
      busy: true,
      disabled: true,
    });
    expect(readText(renderer.root)).toContain(
      "The cancellation outcome is unknown. Refresh status before retrying.",
    );
    expect(readText(renderer.root)).not.toContain("Cancellation succeeded");
  });

  it("surfaces stale edits as refresh-and-retry instead of blind overwrite", async () => {
    const callbacks = createCallbacks();
    let renderer!: ReturnType<typeof create>;
    await act(async () => {
      renderer = create(
        <DateManagementView
          confirmation={null}
          date={{ ...date, status: "open" }}
          error="This service date changed on the server. Refresh status, review the latest values, and retry."
          isBusy={false}
          onChange={callbacks.onChange}
          onConfirm={callbacks.onConfirm}
          onEditNeed={callbacks.onEditNeed}
          onRefresh={callbacks.onRefresh}
          onRequestConfirmation={callbacks.onRequestConfirmation}
          onSave={callbacks.onSave}
          onPublish={callbacks.onPublish}
          pendingOutcome={null}
          values={{
            cancellationDeadlineAt: date.cancellationDeadlineAt,
            endsAt: date.endsAt,
            instructions: date.instructions,
            startsAt: date.startsAt,
            title: date.title,
          }}
        />,
      );
    });

    const refresh = renderer.root.findByProps({
      accessibilityLabel: "Refresh service date status",
    });
    expect(refresh.props.accessibilityRole).toBe("button");
    expect(readText(renderer.root)).toContain("review the latest values");
  });
});

function createCallbacks() {
  return {
    onChange: jest.fn(),
    onConfirm: jest.fn(),
    onEditNeed: jest.fn(),
    onPublish: jest.fn(),
    onRefresh: jest.fn(),
    onRequestConfirmation: jest.fn(),
    onSave: jest.fn(),
  };
}

function readText(node: { children?: readonly unknown[] }): string {
  return (node.children ?? [])
    .flatMap((child) => {
      if (typeof child === "string") {
        return [child];
      }
      if (typeof child === "object" && child !== null && "children" in child) {
        return [readText(child as { children?: readonly unknown[] })];
      }
      return [];
    })
    .join(" ")
    .replace(/\s+/g, " ")
    .trim();
}
