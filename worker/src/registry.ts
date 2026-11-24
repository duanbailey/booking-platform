import type { Pool } from 'mysql2/promise';
import { bookingHandlers } from './bookingEmails';
import { ContactDirectory } from './contacts';
import { BookingDirectory } from './directory';
import type { HandlerRegistry } from './pipeline';

/**
 * The event handlers the Lambda entry point registers (tested in registry.test.ts): T24 booking emails; T25
 * (reminders) and T26 (invite and account emails) add theirs. Any other event fails into the dead-letter queue.
 */
export function createHandlers(pool: Pool): HandlerRegistry {
  return new Map([
    ...bookingHandlers({ directory: new BookingDirectory(pool), contacts: new ContactDirectory(pool) }),
  ]);
}
