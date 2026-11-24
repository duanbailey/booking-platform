// Reminders (T25, R9): scheduling, moving and deleting the 24h and 1h reminders in EventBridge Scheduler, and
// handling ReminderDue. Decisions 2026-11-24 (plan/tasks.md, T25): due exactly 24 h and 1 h of elapsed time before
// the start; a reminder already due when the event is handled is skipped (deleted when moving); the ReminderDue
// eventId is a UUIDv5 of the causing event's eventId and the reminder type; a ReminderDue is dropped if the booking is
// not Confirmed, starts at another time, or has already started.
import { createHash } from 'node:crypto';
import { signedBody } from './bookingEmails';
import type { ContactDirectory } from './contacts';
import { MissingRecordError, type BookingDirectory } from './directory';
import type { Envelope } from './events';
import type { NotificationKind } from './notifications';
import type { EventHandler } from './pipeline';
import type { ReminderScheduler } from './scheduler';
import { formatStart, whenLines } from './time';

export type ReminderType = '24h' | '1h';

const REMINDERS: readonly { type: ReminderType; hours: number; kind: NotificationKind; phrase: string }[] = [
  { type: '24h', hours: 24, kind: 'Reminder24h', phrase: 'in 24 hours' },
  { type: '1h', hours: 1, kind: 'Reminder1h', phrase: 'in 1 hour' },
];

/** specs/06-events.md: schedules are named from the booking ID and reminder type, so every call is idempotent. */
export function scheduleName(bookingId: string, type: ReminderType): string {
  return `reminder-${bookingId}-${type}`;
}

/** The UUIDv5 namespace of reminder event IDs (fixed; changing it would change every ID). */
const REMINDER_NAMESPACE = '6f1c2b8e-3d4a-4f5b-9c7d-2e8a1b0c4d3f';

/**
 * The ReminderDue eventId: new for each create or move (each causing event), as specs/06-events.md requires, but the
 * same when an event is redelivered, so a retry cannot slip a second reminder past the notifications key.
 */
export function reminderEventId(causingEventId: string, type: ReminderType): string {
  const hash = createHash('sha1')
    .update(Buffer.from(REMINDER_NAMESPACE.replace(/-/g, ''), 'hex'))
    .update(`${causingEventId}:${type}`, 'utf8')
    .digest();
  const bytes = hash.subarray(0, 16);
  bytes[6] = (bytes[6] & 0x0f) | 0x50; // version 5
  bytes[8] = (bytes[8] & 0x3f) | 0x80; // RFC 4122 variant
  const hex = bytes.toString('hex');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** What the booking handlers call after their emails (decided 2026-11-24: emails first). */
export class Reminders {
  constructor(private readonly scheduler: ReminderScheduler, private readonly now: () => Date) {}

  /** BookingCreated: schedules both reminders, skipping any already due (R9). */
  async schedule(cause: Envelope, bookingId: string, startUtc: string): Promise<void> {
    for (const reminder of REMINDERS) {
      const due = this.dueAt(startUtc, reminder.hours);
      if (due.getTime() <= this.now().getTime()) continue;
      await this.upsert(cause, bookingId, startUtc, reminder.type, due);
    }
  }

  /** BookingRescheduled: moves both reminders to the new start, deleting any already due at the new start (R9). */
  async move(cause: Envelope, bookingId: string, newStartUtc: string): Promise<void> {
    for (const reminder of REMINDERS) {
      const due = this.dueAt(newStartUtc, reminder.hours);
      if (due.getTime() <= this.now().getTime()) await this.scheduler.delete(scheduleName(bookingId, reminder.type));
      else await this.upsert(cause, bookingId, newStartUtc, reminder.type, due);
    }
  }

  /** BookingCancelled: deletes the pending reminders, whatever the reason (R9, including R6 cancellations). */
  async delete(bookingId: string): Promise<void> {
    for (const reminder of REMINDERS) await this.scheduler.delete(scheduleName(bookingId, reminder.type));
  }

  private dueAt(startUtc: string, hours: number): Date {
    return new Date(new Date(startUtc).getTime() - hours * 3600_000);
  }

  private async upsert(cause: Envelope, bookingId: string, startUtc: string, type: ReminderType, due: Date): Promise<void> {
    const payload: Envelope = {
      eventId: reminderEventId(cause.eventId, type),
      eventType: 'ReminderDue',
      schemaVersion: 1,
      occurredAt: due.toISOString(),
      correlationId: cause.correlationId,
      data: { bookingId, reminder: type, scheduledForStartUtc: new Date(startUtc).toISOString() },
    };
    await this.scheduler.upsert({ name: scheduleName(bookingId, type), at: due, messageGroupId: bookingId, input: JSON.stringify(payload) });
  }
}

export interface ReminderDueDeps {
  directory: Pick<BookingDirectory, 'reminderBooking' | 'business' | 'serviceName' | 'staffUserId'>;
  contacts: Pick<ContactDirectory, 'find'>;
  now: () => Date;
  /** APP_NAME: the subject prefix and the sign-off, as the other booking emails. */
  appName: string;
}

/** ReminderDue v1: a reminder to the customer, unless it is stale (specs/06-events.md, R9). */
export function reminderDueHandler(deps: ReminderDueDeps): EventHandler {
  return async ({ event, log, sendEmail }) => {
    const data = event.data as { bookingId: string; reminder: ReminderType; scheduledForStartUtc: string };
    const reminder = REMINDERS.find((r) => r.type === data.reminder)!;
    const booking = await deps.directory.reminderBooking(data.bookingId);

    const stale = booking.status !== 'Confirmed' ? 'NotConfirmed'
      : booking.startUtc.getTime() !== new Date(data.scheduledForStartUtc).getTime() ? 'StartChanged'
      : deps.now().getTime() >= booking.startUtc.getTime() ? 'AlreadyStarted'
      : null;
    if (stale) {
      log.info('Stale reminder dropped', { reason: stale, bookingId: data.bookingId, reminder: data.reminder });
      return;
    }

    const business = await deps.directory.business(booking.businessId);
    const service = await deps.directory.serviceName(booking.serviceId);
    const staffUserId = await deps.directory.staffUserId(booking.staffId);
    const staff = await deps.contacts.find(staffUserId);
    if (!staff) throw new MissingRecordError(`User ${staffUserId} not found.`);
    const zone = booking.businessTimeZone;
    const slot = { startUtc: booking.startUtc.toISOString(), endUtc: booking.endUtc.toISOString() };

    await sendEmail(booking.customerId, reminder.kind, (to) => ({
      subject: `${deps.appName}: reminder: ${service} at ${business.name} on ${formatStart(slot.startUtc, zone)}`,
      text: signedBody(deps.appName, to, `This is a reminder of your booking ${reminder.phrase}.`, [
        `Service: ${service}`,
        `Business: ${business.name}`,
        `With: ${staff.name}`,
        ...whenLines(slot, zone, to.timeZone),
      ]),
    }));
  };
}
