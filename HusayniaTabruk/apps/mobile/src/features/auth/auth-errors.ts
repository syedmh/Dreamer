import { ApiClientError, MobileApiConfigurationError } from "../../core/api/auth-api";
import { isAbortError } from "../../core/security/auth-session-controller";

export interface AuthViewError {
  readonly code?: string;
  readonly detail: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
}

const fallbackMessage = "We could not reach the Tabruk service. Please try again.";

export function toAuthViewError(error: unknown): AuthViewError | null {
  if (isAbortError(error)) {
    return null;
  }

  if (error instanceof ApiClientError) {
    return {
      code: error.problem?.code,
      detail: error.problem?.detail ?? error.message,
      fieldErrors: normalizeFieldErrors(error.problem?.fieldErrors),
    };
  }

  if (error instanceof MobileApiConfigurationError) {
    return {
      detail: error.message,
      fieldErrors: {},
    };
  }

  if (error instanceof Error) {
    return {
      detail: error.message || fallbackMessage,
      fieldErrors: {},
    };
  }

  return {
    detail: fallbackMessage,
    fieldErrors: {},
  };
}

export function toAccessibleErrorLines(error: AuthViewError | null): readonly string[] {
  if (!error) {
    return [];
  }

  const fieldMessages = Object.values(error.fieldErrors).flatMap((messages) => [...messages]);
  return [error.detail, ...fieldMessages];
}

function normalizeFieldErrors(
  value: Record<string, string[]> | undefined,
): Readonly<Record<string, readonly string[]>> {
  if (!value) {
    return {};
  }

  return Object.fromEntries(
    Object.entries(value).map(([field, messages]) => [field, [...messages]]),
  );
}
