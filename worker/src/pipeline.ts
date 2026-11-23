import type { Contact, ContactDirectory } from './contacts';
import type { EmailContent, EmailSender } from './email';
import type { Envelope } from './events';
import { describeError, describeErrorForLog, withFields, type Logger } from './log';
import type { NotificationKind, NotificationStore } from './notifications';

/** What a handler gets: the validated event, a logger carrying its IDs, and idempotent sending. */
export interface HandlerContext {
  event: Envelope;
  log: Logger;
  /**
   * Emails one recipient at most once for this event (the notifications row decides). The contact is read just
   * before sending and passed to compose; a send error marks the row Failed and is rethrown, failing the message so
   * SQS delivers it again, and the retry sends only what is not Sent yet.
   */
  sendEmail(recipientUserId: string, kind: NotificationKind, compose: (contact: Contact) => EmailContent): Promise<void>;
}

export type EventHandler = (context: HandlerContext) => Promise<void>;

/** Handlers by "EventType.vN"; T24-T26 register the real ones. */
export type HandlerRegistry = ReadonlyMap<string, EventHandler>;

/** A known, valid event with no handler yet: fails into the dead-letter queue like an unknown one (decided 2026-11-23). */
export class NoHandlerError extends Error {
  override name = 'NoHandlerError';
}

export class RecipientNotFoundError extends Error {
  override name = 'RecipientNotFoundError';
}

export interface PipelineDeps {
  parse: (body: string) => Envelope;
  handlers: HandlerRegistry;
  notifications: Pick<NotificationStore, 'claim' | 'markSent' | 'markFailed'>;
  contacts: Pick<ContactDirectory, 'find'>;
  email: EmailSender;
  log: Logger;
}

/** Parses, validates and handles one message body; throws to fail the message (T23). */
export async function processMessage(body: string, deps: PipelineDeps): Promise<Envelope> {
  const event = deps.parse(body);
  const log = withFields(deps.log, { eventId: event.eventId, eventType: event.eventType, correlationId: event.correlationId });
  const handler = deps.handlers.get(`${event.eventType}.v${event.schemaVersion}`);
  if (!handler) throw new NoHandlerError(`No handler for ${event.eventType} v${event.schemaVersion} yet.`);

  const sendEmail: HandlerContext['sendEmail'] = async (recipientUserId, kind, compose) => {
    const row = await deps.notifications.claim(event.eventId, recipientUserId, kind);
    if (row.status === 'Sent') {
      log.info('Email already sent for this event; skipped', { recipientUserId, kind, notificationId: row.id });
      return;
    }
    let messageId: string;
    try {
      const contact = await deps.contacts.find(recipientUserId);
      if (!contact) throw new RecipientNotFoundError(`User ${recipientUserId} not found.`);
      messageId = await deps.email.send({ to: contact.email, ...compose(contact) });
    } catch (error) {
      await deps.notifications.markFailed(row.id, describeError(error));
      log.error('Email failed', { recipientUserId, kind, notificationId: row.id, error: describeErrorForLog(error) });
      throw error;
    }
    // Outside the try: if recording Sent fails, the email did go out, so the row stays Pending rather than Failed and
    // the message fails; the retry sends it again, a duplicate the spec accepts (T23 review).
    await deps.notifications.markSent(row.id, messageId);
    log.info('Email sent', { recipientUserId, kind, notificationId: row.id, sesMessageId: messageId });
  };

  log.info('Handling event');
  await handler({ event, log, sendEmail });
  log.info('Event handled');
  return event;
}
