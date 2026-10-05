// Twins of G9SyncData.Client/Progress/G9DtSyncProgress.cs and G9CSyncProgressTracker.cs
// (AiGuides/09-Progress-Model.md): monotonic progress from phase transitions and row/byte counts.

/** Phases of a sync run, in execution order (`G9ESyncPhase`). */
export const G9ESyncPhase = {
  Queued: 0,
  Connecting: 1,
  Pushing: 2,
  Reconciling: 3,
  Planning: 4,
  Downloading: 5,
  Applying: 6,
  PostStep: 7,
  Completed: 8,
  Canceled: 9,
  Failed: 10,
} as const;
export type G9ESyncPhase = (typeof G9ESyncPhase)[keyof typeof G9ESyncPhase];

const PHASE_NAMES = ['Queued', 'Connecting', 'Pushing', 'Reconciling', 'Planning', 'Downloading', 'Applying', 'PostStep', 'Completed', 'Canceled', 'Failed'];

/** The .NET enum member name of a phase (the statistics and PhaseMs keys), e.g. `Downloading`. */
export function phaseName(phase: number): string {
  return PHASE_NAMES[phase] ?? String(phase);
}

/**
 * One progress sample. `overall` is monotonic within a run and reaches 1 only when the run completed. The library emits
 * numbers and phases; localising them is the app's job.
 */
export interface G9DtSyncProgress {
  runId: string;
  phase: G9ESyncPhase;
  /** 0..1, monotonic. */
  overall: number;
  /** 0..1 inside the phase. */
  phaseFraction: number;
  rowsDone: number;
  rowsTotal: number;
  bytesDone: number;
  bytesTotal: number;
  subscriptionKey: string | null;
  table: string | null;
  rowsPerSecond: number;
  /** Estimated milliseconds to completion of the run, or null while unknown. */
  etaMs: number | null;
  queuedBehind: number;
  postStepName: string | null;
  cancelPending: boolean;
}

interface PlanEntry {
  phase: G9ESyncPhase;
  name: string | null;
  expectedMs: number;
}

/**
 * Turns phase transitions and counts into monotonic progress. Each phase entered gets a span of the REMAINING bar in
 * proportion to its expected share of the remaining work, so monotonicity holds by construction; a phase without a
 * real signal creeps toward 90% of its span along an exponential curve scaled by its expected duration.
 */
export class G9SyncProgressTracker {
  private readonly _runId: string;
  private readonly _publish: (progress: G9DtSyncProgress) => void;
  private readonly _queuedBehind: () => number;
  private readonly _minIntervalMs: number;
  private readonly _plan: PlanEntry[] = [];
  private readonly _durations: { [key: string]: number } = {};
  private readonly _runStart = Date.now();
  private _phaseStart = Date.now();
  private _timer: ReturnType<typeof setInterval> | null = null;

  private _phase: G9ESyncPhase = G9ESyncPhase.Queued;
  private _postStep: string | null = null;
  private _table: string | null = null;
  private _subscription: string | null = null;
  private _base = 0;
  private _span = 0;
  private _overall = 0;
  private _fraction = 0;
  private _phaseExpectedMs = 0;
  private _rowsDone = 0;
  private _rowsTotal = 0;
  private _bytesDone = 0;
  private _bytesTotal = 0;
  private _lastPublish = 0;
  private _realSignal = false;
  private _terminal = false;
  private _cancelPending = false;
  private _disposed = false;
  private _lastPublished: G9DtSyncProgress | null = null;

  constructor(runId: string, publish: (progress: G9DtSyncProgress) => void, queuedBehind: () => number, maxHz: number) {
    this._runId = runId;
    this._publish = publish;
    this._queuedBehind = queuedBehind;
    this._minIntervalMs = maxHz <= 0 ? 0 : 1000 / maxHz;
  }

  get overall(): number {
    return this._overall;
  }

  /** Measured milliseconds per phase (post steps keyed `PostStep:name`). */
  get durations(): { [key: string]: number } {
    const out: { [key: string]: number } = {};
    for (const key of Object.keys(this._durations)) out[key] = this._durations[key] as number;
    return out;
  }

  /** Appends a phase to the plan with its expected duration. */
  plan(phase: G9ESyncPhase, expectedMs: number, name: string | null = null): void {
    if (!this._terminal) this._plan.push({ phase, name, expectedMs: Math.max(0, expectedMs) });
  }

  /** Updates the expected duration of a planned phase that has not started yet. */
  expect(phase: G9ESyncPhase, expectedMs: number, name: string | null = null): void {
    for (const entry of this._plan) {
      if (entry.phase === phase && entry.name === name) {
        entry.expectedMs = Math.max(0, expectedMs);
        return;
      }
    }
  }

  enter(phase: G9ESyncPhase, rowsTotal = 0, bytesTotal = 0, name: string | null = null): void {
    if (this._terminal || this._disposed) return;
    this._close();
    let index = -1;
    for (let i = 0; i < this._plan.length; i++) {
      const entry = this._plan[i] as PlanEntry;
      if (entry.phase === phase && entry.name === name) {
        index = i;
        break;
      }
    }
    let expected = 500;
    if (index >= 0) {
      expected = (this._plan[index] as PlanEntry).expectedMs;
      this._plan.splice(0, index + 1);
    }
    expected = Math.max(1, expected);
    let remaining = 0;
    for (const entry of this._plan) remaining += entry.expectedMs;
    this._base = this._overall;
    this._span = ((1 - this._base) * expected) / (expected + remaining);
    this._phase = phase;
    this._postStep = name;
    this._rowsDone = 0;
    this._rowsTotal = Math.max(0, rowsTotal);
    this._bytesDone = 0;
    this._bytesTotal = Math.max(0, bytesTotal);
    this._fraction = 0;
    this._realSignal = false;
    this._phaseExpectedMs = expected;
    this._phaseStart = Date.now();
    if (this._timer === null) this._timer = setInterval(() => this._creep(), 200);
    this._emit(this._sample(true));
  }

  /** Reports absolute counts for the current phase. */
  report(rowsDone: number, bytesDone = -1, table: string | null = null, subscriptionKey: string | null = null): void {
    if (this._terminal || this._disposed) return;
    this._rowsDone = Math.max(0, rowsDone);
    if (bytesDone >= 0) this._bytesDone = bytesDone;
    if (table !== null) this._table = table;
    if (subscriptionKey !== null) this._subscription = subscriptionKey;
    const fraction = this._rowsTotal > 0 ? this._rowsDone / this._rowsTotal : this._bytesTotal > 0 ? this._bytesDone / this._bytesTotal : this._fraction;
    this._realSignal = this._rowsTotal > 0 || this._bytesTotal > 0;
    this._advance(fraction);
    this._emit(this._sample(false));
  }

  /** Reports a 0..1 fraction of the current phase (post steps). */
  reportFraction(fraction: number): void {
    if (this._terminal || this._disposed) return;
    this._realSignal = true;
    this._advance(fraction);
    this._emit(this._sample(false));
  }

  setCancelPending(): void {
    if (this._terminal || this._disposed) return;
    this._cancelPending = true;
    this._emit(this._sample(true));
  }

  complete(terminal: G9ESyncPhase): void {
    if (this._terminal || this._disposed) return;
    this._close();
    this._terminal = true;
    this._stopTimer();
    if (terminal === G9ESyncPhase.Completed) {
      this._overall = 1;
      this._fraction = 1;
    }
    this._phase = terminal;
    this._emit(this._sample(true));
  }

  dispose(): void {
    if (this._disposed) return;
    this._disposed = true;
    this._stopTimer();
  }

  private _stopTimer(): void {
    if (this._timer !== null) {
      clearInterval(this._timer);
      this._timer = null;
    }
  }

  private _creep(): void {
    if (this._terminal || this._disposed || this._realSignal || this._phase === G9ESyncPhase.Queued) return;
    const elapsed = Date.now() - this._phaseStart;
    this._advance(0.9 * (1 - Math.exp(-elapsed / Math.max(100, this._phaseExpectedMs))));
    this._emit(this._sample(false));
  }

  private _advance(fraction: number): void {
    const clamped = Math.min(Math.max(fraction, 0), 0.995);
    if (clamped > this._fraction) this._fraction = clamped;
    const overall = Math.min(0.999, this._base + this._span * this._fraction);
    if (overall > this._overall) this._overall = overall;
  }

  private _close(): void {
    const phase = this._phase;
    if (phase === G9ESyncPhase.Queued || phase === G9ESyncPhase.Completed || phase === G9ESyncPhase.Canceled || phase === G9ESyncPhase.Failed) return;
    const key = phase === G9ESyncPhase.PostStep ? 'PostStep:' + (this._postStep ?? '') : phaseName(phase);
    this._durations[key] = (this._durations[key] ?? 0) + (Date.now() - this._phaseStart);
    this._overall = Math.max(this._overall, Math.min(0.999, this._base + this._span));
  }

  private _sample(force: boolean): G9DtSyncProgress | null {
    const now = Date.now();
    if (!force && now - this._lastPublish < this._minIntervalMs) return null;
    this._lastPublish = now;
    const phaseSeconds = Math.max(0.001, (now - this._phaseStart) / 1000);
    let eta: number | null = null;
    if (this._terminal) eta = 0;
    else if (this._overall > 0.02) eta = ((now - this._runStart) * (1 - this._overall)) / this._overall;
    let queued = 0;
    try {
      queued = this._queuedBehind();
    } catch {
      queued = 0;
    }
    return {
      runId: this._runId,
      phase: this._phase,
      overall: this._overall,
      phaseFraction: this._fraction,
      rowsDone: this._rowsDone,
      rowsTotal: this._rowsTotal,
      bytesDone: this._bytesDone,
      bytesTotal: this._bytesTotal,
      subscriptionKey: this._subscription,
      table: this._table,
      rowsPerSecond: this._rowsDone / phaseSeconds,
      etaMs: eta,
      queuedBehind: queued,
      postStepName: this._postStep,
      cancelPending: this._cancelPending,
    };
  }

  /** Publishes a sample unless it would go backwards (lower overall or an earlier phase). */
  private _emit(sample: G9DtSyncProgress | null): void {
    if (sample === null) return;
    const last = this._lastPublished;
    if (last !== null && (sample.overall < last.overall || sample.phase < last.phase)) return;
    this._lastPublished = sample;
    try {
      this._publish(sample);
    } catch {
      // A progress consumer must never break the sync.
    }
  }
}
