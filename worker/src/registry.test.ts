import type { Pool } from 'mysql2/promise';
import { createHandlers } from './registry';
import { FakeReminderScheduler } from './testing/fakes';
import { testPool } from './testing/mysql';

// T24 review: the registry the Lambda entry point (index.ts) uses must handle every booking event, or they would fail
// into the dead-letter queue as having no handler, with no other test noticing.
describe('production handler registry', () => {
  let pool: Pool;
  beforeAll(() => {
    pool = testPool();
  });
  afterAll(async () => {
    await pool.end();
  });

  it('registers the T24 booking email, T25 ReminderDue and T26 account email handlers', () => {
    const handlers = createHandlers(pool, { scheduler: new FakeReminderScheduler(), app: { baseUrl: 'https://app.example.test', name: 'Bookly' } });

    for (const key of ['BookingCreated.v1', 'BookingRescheduled.v1', 'BookingCancelled.v1', 'BookingLocalTimeChanged.v1', 'RebookedInsideWindow.v1', 'ReminderDue.v1',
      'AccessGranted.v1', 'PasswordResetRequested.v1', 'PasswordChanged.v1']) {
      expect(handlers.has(key)).toBe(true);
    }
  });
});
