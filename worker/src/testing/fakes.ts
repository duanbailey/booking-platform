import { randomUUID } from 'node:crypto';
import type { EmailSender, OutgoingEmail } from '../email';
import type { Logger, LogFields } from '../log';

/** The in-memory stand-in for SES (ADR 0007): records each email; can fail chosen recipients. */
export class FakeEmailSender implements EmailSender {
  readonly sent: (OutgoingEmail & { messageId: string })[] = [];
  failFor = new Set<string>();

  async send(email: OutgoingEmail): Promise<string> {
    if (this.failFor.has(email.to)) throw new Error(`MessageRejected: cannot send to ${email.to}`);
    const messageId = `fake-${randomUUID()}`;
    this.sent.push({ ...email, messageId });
    return messageId;
  }
}

/** Records log entries instead of writing them. */
export class CapturingLogger implements Logger {
  readonly entries: ({ level: string; message: string } & LogFields)[] = [];

  info(message: string, fields: LogFields = {}): void {
    this.entries.push({ level: 'info', message, ...fields });
  }

  warn(message: string, fields: LogFields = {}): void {
    this.entries.push({ level: 'warn', message, ...fields });
  }

  error(message: string, fields: LogFields = {}): void {
    this.entries.push({ level: 'error', message, ...fields });
  }
}

/** A valid PasswordChanged v1 envelope (the simplest event); override fields as needed. */
export function passwordChanged(overrides: { userId?: string; eventId?: string; correlationId?: string } = {}) {
  return {
    eventId: overrides.eventId ?? randomUUID(),
    eventType: 'PasswordChanged',
    schemaVersion: 1,
    occurredAt: '2026-11-23T10:00:00.000Z',
    correlationId: overrides.correlationId ?? 'corr-123',
    data: { userId: overrides.userId ?? randomUUID(), method: 'Changed' },
  };
}
