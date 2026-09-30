import { ApiClientError } from "../../core/api/auth-api";
import { createInstallationId } from "../../core/security/auth-session-controller";

export type ManagementErrorKind = "rejected" | "stale" | "unknown";

export interface ManagementError {
  readonly kind: ManagementErrorKind;
  readonly message: string;
}

export class IdempotencyKeyStore {
  private readonly keys = new Map<string, string>();

  public constructor(
    private readonly createKey: () => string = createInstallationId,
  ) {}

  public get(operation: string): string {
    const current = this.keys.get(operation);
    if (current) {
      return current;
    }

    const created = this.createKey();
    this.keys.set(operation, created);
    return created;
  }

  public confirm(operation: string): void {
    this.keys.delete(operation);
  }

  public clear(): void {
    this.keys.clear();
  }
}

export interface PendingIdempotentCommand<TRequest> {
  readonly key: string;
  readonly request: TRequest;
}

export class IdempotentCommandStore<TRequest> {
  private readonly commands = new Map<
    string,
    PendingIdempotentCommand<TRequest>
  >();

  public constructor(
    private readonly createKey: () => string = createInstallationId,
  ) {}

  public get(
    operation: string,
    createRequest: () => TRequest,
  ): PendingIdempotentCommand<TRequest> {
    const current = this.commands.get(operation);
    if (current) {
      return current;
    }

    const created = {
      key: this.createKey(),
      request: createRequest(),
    };
    this.commands.set(operation, created);
    return created;
  }

  public confirm(operation: string): void {
    this.commands.delete(operation);
  }

  public confirmMatching(predicate: (request: TRequest) => boolean): void {
    for (const [operation, command] of this.commands) {
      if (predicate(command.request)) {
        this.commands.delete(operation);
      }
    }
  }

  public clear(): void {
    this.commands.clear();
  }
}

export function toManagementError(
  error: unknown,
  resourceLabel: string,
): ManagementError {
  if (
    error instanceof ApiClientError &&
    (error.status === 412 || error.problem?.code === "stale_version")
  ) {
    return {
      kind: "stale",
      message: `This ${resourceLabel} changed on the server. Refresh status, review the latest values, and retry.`,
    };
  }

  if (
    error instanceof ApiClientError &&
    error.problem?.code === "cancellation_deadline_passed"
  ) {
    return {
      kind: "rejected",
      message:
        "The self-cancellation deadline has passed. Contact the Food Incharge.",
    };
  }

  if (isUnknownOutcome(error)) {
    return {
      kind: "unknown",
      message: `The ${resourceLabel} outcome is unknown. Refresh status before retrying.`,
    };
  }

  if (error instanceof ApiClientError) {
    return {
      kind: "rejected",
      message:
        error.problem?.detail ??
        `The ${resourceLabel} change was not accepted.`,
    };
  }

  return {
    kind: "rejected",
    message:
      error instanceof Error && error.message
        ? error.message
        : `The ${resourceLabel} change could not be completed.`,
  };
}

function isUnknownOutcome(error: unknown): boolean {
  if (!(error instanceof Error)) {
    return false;
  }

  return (
    error.name === "AbortError" ||
    error.name === "TimeoutError" ||
    error instanceof TypeError
  );
}
