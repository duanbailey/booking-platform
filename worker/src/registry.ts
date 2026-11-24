import type { Pool } from 'mysql2/promise';
import { accountHandlers, type AppSettings } from './accountEmails';
import { bookingHandlers } from './bookingEmails';
import { ContactDirectory } from './contacts';
import { BookingDirectory } from './directory';
import { LinkStore } from './links';
import type { HandlerRegistry } from './pipeline';
import { Reminders, reminderDueHandler } from './reminders';
import type { ReminderScheduler } from './scheduler';

export interface HandlerOptions {
  scheduler: ReminderScheduler;
  app: AppSettings;
  now?: () => Date;
}

/**
 * The event handlers the Lambda entry point registers (tested in registry.test.ts): T24 booking emails with T25's
 * reminder changes, T25's ReminderDue, and T26's invite and account emails. Any other event fails into the
 * dead-letter queue.
 */
export function createHandlers(pool: Pool, options: HandlerOptions): HandlerRegistry {
  const now = options.now ?? (() => new Date());
  const directory = new BookingDirectory(pool);
  const contacts = new ContactDirectory(pool);
  return new Map([
    ...bookingHandlers({ directory, contacts, reminders: new Reminders(options.scheduler, now), appName: options.app.name }),
    ['ReminderDue.v1', reminderDueHandler({ directory, contacts, now, appName: options.app.name })],
    ...accountHandlers({ directory, links: new LinkStore(pool), app: options.app, now }),
  ]);
}
