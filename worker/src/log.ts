export type LogFields = Record<string, unknown>;

/**
 * Structured logging (specs/07-nfr.md): JSON lines on stdout, which Lambda sends to CloudWatch. Callers pass
 * correlationId, eventId and eventType; never message bodies, email addresses, tokens or token hashes.
 */
export interface Logger {
  info(message: string, fields?: LogFields): void;
  warn(message: string, fields?: LogFields): void;
  error(message: string, fields?: LogFields): void;
}

export function jsonLogger(write: (line: string) => void = (line) => process.stdout.write(line + '\n')): Logger {
  const entry = (level: string) => (message: string, fields: LogFields = {}) =>
    write(JSON.stringify({ time: new Date().toISOString(), level, message, ...fields }));
  return { info: entry('info'), warn: entry('warn'), error: entry('error') };
}

/** A logger that adds the same fields (e.g. an event's IDs) to every entry. */
export function withFields(log: Logger, fields: LogFields): Logger {
  return {
    info: (message, more) => log.info(message, { ...fields, ...more }),
    warn: (message, more) => log.warn(message, { ...fields, ...more }),
    error: (message, more) => log.error(message, { ...fields, ...more }),
  };
}

/** An error's name and message, for the notifications table, never its stack or cause. */
export function describeError(error: unknown): string {
  return error instanceof Error ? (error.name && error.name !== 'Error' ? `${error.name}: ${error.message}` : error.message) : String(error);
}

/**
 * {@link describeError} for logs: email addresses replaced by [email], since provider errors can name the
 * recipient (e.g. SES "Email address is not verified: ...") and logs carry no personal data (T23 review).
 */
export function describeErrorForLog(error: unknown): string {
  return describeError(error).replace(/[^\s@<>()"',;:]+@[^\s@<>()"',;:]+/g, '[email]');
}
