const defaultTimeoutMilliseconds = 10_000;

export interface RequestScope {
  readonly signal: AbortSignal;
  dispose(): void;
}

export function createRequestScope(
  parentSignal?: AbortSignal,
  timeoutMilliseconds = defaultTimeoutMilliseconds,
): RequestScope {
  const controller = new AbortController();
  const abortFromParent = () => controller.abort(parentSignal?.reason);

  if (parentSignal?.aborted) {
    abortFromParent();
  } else {
    parentSignal?.addEventListener("abort", abortFromParent, { once: true });
  }

  const timeout = setTimeout(() => {
    const error = new Error("The request timed out.");
    error.name = "TimeoutError";
    controller.abort(error);
  }, timeoutMilliseconds);

  return {
    signal: controller.signal,
    dispose() {
      clearTimeout(timeout);
      parentSignal?.removeEventListener("abort", abortFromParent);
    },
  };
}

export async function runWithRequestScope<T>(
  operation: (signal: AbortSignal) => Promise<T>,
  parentSignal?: AbortSignal,
  timeoutMilliseconds = defaultTimeoutMilliseconds,
): Promise<T> {
  const scope = createRequestScope(parentSignal, timeoutMilliseconds);

  try {
    return await runWithSignal(
      () => operation(scope.signal),
      scope.signal,
      true,
    );
  } finally {
    scope.dispose();
  }
}

export function runWithSignal<T>(
  operation: () => Promise<T>,
  signal?: AbortSignal,
  allowAbortRecovery = false,
): Promise<T> {
  if (signal?.aborted) {
    return Promise.reject(getSignalError(signal));
  }

  let promise: Promise<T>;
  try {
    promise = operation();
  } catch (error) {
    return Promise.reject(error);
  }

  if (!signal) {
    return promise;
  }

  return new Promise<T>((resolve, reject) => {
    let isSettled = false;
    const settle = (continuation: () => void): void => {
      if (isSettled) {
        return;
      }

      isSettled = true;
      signal.removeEventListener("abort", onAbort);
      continuation();
    };
    const onAbort = (): void => {
      if (!allowAbortRecovery) {
        settle(() => reject(getSignalError(signal)));
        return;
      }

      // Provider wrappers allow their controller a bounded microtask window
      // to convert the shared timeout into a safe rotated-session fallback.
      scheduleMicrotasks(8, () => {
        settle(() => reject(getSignalError(signal)));
      });
    };

    signal.addEventListener("abort", onAbort, { once: true });
    promise.then(
      (value) => settle(() => resolve(value)),
      (error: unknown) => settle(() => reject(error)),
    );
  });
}

export function throwIfAborted(signal?: AbortSignal): void {
  if (!signal?.aborted) {
    return;
  }

  const error = new Error("The operation was aborted.");
  error.name = "AbortError";
  throw error;
}

function getSignalError(signal: AbortSignal): Error {
  if (signal.reason instanceof Error) {
    return signal.reason;
  }

  const error = new Error("The operation was aborted.");
  error.name = "AbortError";
  return error;
}

function scheduleMicrotasks(remaining: number, continuation: () => void): void {
  if (remaining <= 0) {
    continuation();
    return;
  }

  queueMicrotask(() => {
    scheduleMicrotasks(remaining - 1, continuation);
  });
}
