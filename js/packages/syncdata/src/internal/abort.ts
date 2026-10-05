// Cancellation (the CancellationToken twin) over AbortSignal. Lynx has AbortController on globalThis; a runtime without
// one gets a minimal stand-in with the same surface, so the library never depends on it existing.

type Listener = () => void;

interface MinimalSignal {
  readonly aborted: boolean;
  readonly reason: unknown;
  addEventListener(type: 'abort', listener: Listener, options?: { once?: boolean }): void;
  removeEventListener(type: 'abort', listener: Listener): void;
}

class FallbackSignal implements MinimalSignal {
  aborted = false;
  reason: unknown = undefined;
  onabort: ((this: unknown) => void) | null = null;
  private readonly _listeners: Listener[] = [];

  addEventListener(_type: 'abort', listener: Listener): void {
    this._listeners.push(listener);
  }

  removeEventListener(_type: 'abort', listener: Listener): void {
    const index = this._listeners.indexOf(listener);
    if (index >= 0) this._listeners.splice(index, 1);
  }

  /** @internal */
  fire(reason: unknown): void {
    if (this.aborted) return;
    this.aborted = true;
    this.reason = reason;
    const listeners = this._listeners.splice(0, this._listeners.length);
    for (const listener of listeners) {
      try {
        listener();
      } catch {
        // a listener must not stop the others
      }
    }
    if (this.onabort) {
      try {
        this.onabort.call(this);
      } catch {
        // ignore
      }
    }
  }
}

/** An AbortController (native when the runtime has one). */
export interface G9AbortController {
  readonly signal: AbortSignal;
  abort(reason?: unknown): void;
}

export function createAbortController(): G9AbortController {
  const Native = (globalThis as { AbortController?: new () => G9AbortController }).AbortController;
  if (typeof Native === 'function') return new Native();
  const signal = new FallbackSignal();
  return {
    signal: signal as unknown as AbortSignal,
    abort: (reason?: unknown) => signal.fire(reason === undefined ? cancellationError() : reason),
  };
}

/** Calls `callback` once when `signal` aborts (at once when it already has); returns the unsubscribe function. */
export function onAbort(signal: AbortSignal | null | undefined, callback: Listener): () => void {
  if (!signal) return noop;
  if (signal.aborted) {
    callback();
    return noop;
  }
  let done = false;
  const listener = (): void => {
    if (done) return;
    done = true;
    callback();
  };
  (signal as unknown as MinimalSignal).addEventListener('abort', listener);
  return () => {
    done = true;
    (signal as unknown as MinimalSignal).removeEventListener('abort', listener);
  };
}

function noop(): void {
  // nothing to undo
}

/** A controller that aborts when any of `signals` does (`CancellationTokenSource.CreateLinkedTokenSource`). */
export function linkSignals(...signals: (AbortSignal | null | undefined)[]): G9AbortController & { dispose(): void } {
  const controller = createAbortController();
  const unsubscribes: (() => void)[] = [];
  for (const signal of signals) {
    unsubscribes.push(onAbort(signal, () => controller.abort(signal ? signal.reason : undefined)));
  }
  return {
    signal: controller.signal,
    abort: (reason?: unknown) => controller.abort(reason),
    dispose: () => {
      for (const unsubscribe of unsubscribes) unsubscribe();
    },
  };
}

/** The error a cancelled operation rejects with (name `AbortError`, like DOM's). */
export function cancellationError(message = 'The operation was canceled.'): Error {
  const error = new Error(message);
  error.name = 'AbortError';
  return error;
}

export function throwIfAborted(signal: AbortSignal | null | undefined): void {
  if (signal && signal.aborted) throw cancellationError();
}

/** `Task.Delay(ms, token)`. */
export function delay(ms: number, signal?: AbortSignal | null): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal && signal.aborted) {
      reject(cancellationError());
      return;
    }
    let unsubscribe: () => void = noop;
    const timer = setTimeout(() => {
      unsubscribe();
      resolve();
    }, Math.max(0, ms));
    unsubscribe = onAbort(signal, () => {
      clearTimeout(timer);
      reject(cancellationError());
    });
  });
}

/** Lets the event loop run (Lynx: keeps the background thread responsive during long applies). */
export function yieldToEventLoop(): Promise<void> {
  return new Promise<void>((resolve) => setTimeout(resolve, 0));
}
