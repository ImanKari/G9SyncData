// Twins of G9SyncData.Abstractions/G9SyncErrorCodes.cs and G9CSyncException.cs. A code is a public API: never
// repurpose one, only add — and add it on both sides in the same change.

/** Stable, support-facing error codes. The client classifies failures by these codes, never by message text. */
export const G9SyncErrorCodes = {
  NetworkFailure: 'G9S-NET-FAILURE',
  ServerError: 'G9S-SRV-ERROR',
  Canceled: 'G9S-CANCELED',

  Unauthorized: 'G9S-AUTH-401',
  UpgradeRequired: 'G9S-VER-UPGRADE',
  ProtocolMismatch: 'G9S-VER-PROTOCOL',

  ModelInvalid: 'G9S-MODEL-INVALID',
  SchemaUpgradeRequired: 'G9S-SCH-UPGRADE',
  UnknownTable: 'G9S-SCH-UNKNOWN-TABLE',
  /** A column declared with a type the server's column does not have (ADR-0061): it syncs in neither direction. */
  ModelTypeMismatch: 'G9S-MODEL-TYPE-MISMATCH',
  /** The server is at its limit of concurrent pulls or pushes (ADR-0062). Retryable. */
  ServerBusy: 'G9S-SRV-BUSY',

  UnknownSubscription: 'G9S-SUB-UNKNOWN',
  InvalidParameters: 'G9S-SUB-PARAM',
  SubscriptionForbidden: 'G9S-SUB-FORBIDDEN',

  ResyncEpoch: 'G9S-RSY-EPOCH',
  ResyncHorizon: 'G9S-RSY-HORIZON',
  ResyncAuthChanged: 'G9S-RSY-AUTH',
  TokenInvalid: 'G9S-TOKEN-INVALID',
  TokenExpired: 'G9S-TOKEN-EXPIRED',

  RejectValidation: 'G9S-REJ-VALIDATION',
  RejectConstraint: 'G9S-REJ-CONSTRAINT',
  /** No subscription of the caller authorises uploads to the table: passes once the missing subscription is held. */
  RejectPermission: 'G9S-REJ-PERMISSION',
  /** The server's row is outside every subscription the caller may write to (no longer theirs). Since 1.5.9. */
  RejectOutsideScope: 'G9S-REJ-OUTSIDE-SCOPE',
  /** After the push the row would be outside every subscription the caller may write to. Since 1.5.9. */
  RejectLeavesScope: 'G9S-REJ-LEAVES-SCOPE',
  RejectDirection: 'G9S-REJ-DIRECTION',
  RejectTooMany: 'G9S-REJ-TOO-MANY',
  /** One row is larger than the server accepts in a push: rejected on the device, every other row still uploads. */
  RejectTooLarge: 'G9S-REJ-TOO-LARGE',
  RejectMalformed: 'G9S-REJ-MALFORMED',

  CommandUnknown: 'G9S-CMD-UNKNOWN',
  CommandRejected: 'G9S-CMD-REJECTED',

  ConflictServerWins: 'G9S-CON-SERVER-WINS',
  ConflictColumns: 'G9S-CON-COLUMNS',
  ConflictDeleted: 'G9S-CON-DELETED',
  ConflictResolverRejected: 'G9S-CON-RESOLVER',

  /** The server could not build or serve a planned snapshot file (ADR-0072); the client replans the pull as rows. */
  SnapshotFileUnavailable: 'G9S-FILE-UNAVAILABLE',
  /** A downloaded snapshot file failed the device's checks; nothing of it was applied and the pull is replanned as rows. */
  SnapshotFileCorrupt: 'G9S-FILE-CORRUPT',
  LocalStoreError: 'G9S-LOCAL-STORE',
  /** The device database is damaged or not a database (ADR-0068). Export what is readable and rebuild from the server. */
  LocalStoreCorrupt: 'G9S-LOCAL-CORRUPT',
  /** The device ran out of storage while the store was writing. */
  LocalStoreDiskFull: 'G9S-LOCAL-DISK-FULL',
  /** Another connection held the write lock longer than the busy timeout. Retryable. */
  LocalStoreBusy: 'G9S-LOCAL-BUSY',

  Unknown: 'G9S-UNKNOWN',
} as const;

const RETRYABLE: { [code: string]: true } = {
  'G9S-NET-FAILURE': true,
  'G9S-SRV-ERROR': true,
  'G9S-SRV-BUSY': true,
  'G9S-TOKEN-EXPIRED': true,
  'G9S-RSY-EPOCH': true,
  'G9S-RSY-HORIZON': true,
  'G9S-RSY-AUTH': true,
  'G9S-LOCAL-BUSY': true,
};

/** True for failures a later attempt may succeed at without any change to data or code (`G9SyncErrorCodes.IsRetryable`). */
export function isRetryableCode(code: string): boolean {
  return RETRYABLE[code] === true;
}

/** Options of {@link G9SyncException}. */
export interface G9SyncExceptionOptions {
  /** Overrides the default retryability of the code. */
  retryable?: boolean;
  /** Structured detail (JSON or text) for logs. Never shown to end users. */
  details?: string | null;
  /** The underlying failure. */
  cause?: unknown;
}

/**
 * The single error type G9SyncData throws for expected failures (twin of `G9CSyncException`). Always carries a stable
 * {@link code}; callers switch on the code, never on the message.
 */
export class G9SyncException extends Error {
  /** Stable error code (see {@link G9SyncErrorCodes}). */
  readonly code: string;
  /** True when retrying without changes may succeed. */
  readonly retryable: boolean;
  /** Optional structured detail (JSON or text) for logs. */
  readonly details: string | null;

  constructor(code: string, message: string, options?: G9SyncExceptionOptions) {
    super(message);
    this.name = 'G9SyncException';
    this.code = code;
    this.retryable = options?.retryable ?? isRetryableCode(code);
    this.details = options?.details ?? null;
    if (options && 'cause' in options) {
      Object.defineProperty(this, 'cause', { value: options.cause, enumerable: false, writable: true, configurable: true });
    }
    // ES2019 output: keep instanceof working for subclasses compiled to functions.
    Object.setPrototypeOf(this, new.target.prototype);
  }
}

/** True for a {@link G9SyncException} (also across bundles that each carry a copy of the class). */
export function isSyncException(error: unknown): error is G9SyncException {
  return (
    error instanceof G9SyncException ||
    (typeof error === 'object' &&
      error !== null &&
      (error as { name?: unknown }).name === 'G9SyncException' &&
      typeof (error as { code?: unknown }).code === 'string')
  );
}
