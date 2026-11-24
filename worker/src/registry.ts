import type { Pool } from 'mysql2/promise';
import { bookingHandlers } from './bookingEmails';
import { ContactDirectory } from './contacts';
import { BookingDirectory } from './directory';
import type { HandlerRegistry } from './pipeline';
import { Reminders, reminderDueHandler } from './reminders';
import type { ReminderScheduler } from './scheduler';

/**
 * The event handlers the Lambda entry point registers (tested in registry.test.ts): T24 booking emails, with T25's
 * reminder changes, and T25's ReminderDue; T26 (invite and account emails) adds its own. Any other event fails into
 * the dead-letter queue.
 */
export function createHandlers(pool: Pool, scheduler: ReminderScheduler, now: () => Date = () => new Date()): HandlerRegistry {
  const directory = new BookingDirectory(pool);
  const contacts = new ContactDirectory(pool);
  return new Map([
    ...bookingHandlers({ directory, contacts, reminders: new Reminders(scheduler, now) }),
    ['ReminderDue.v1', reminderDueHandler({ directory, contacts, now })],
  ]);
}
