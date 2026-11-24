import { randomUUID } from 'node:crypto';
import type { Pool } from 'mysql2/promise';
import { MissingRecordError } from './directory';
import { createEventParser } from './events';
import { processMessage } from './pipeline';
import { reminderEventId } from './reminders';
import {
  bookingWorld, cancelled, created, envelope, rescheduled, NEW_START, SLOT_START, type BookingWorld,
} from './testing/bookingWorld';
import { notificationsFor, testPool } from './testing/mysql';

// T25: reminders. Acceptance: R9; story "Reminders go out 24 hours and 1 hour before a booking". Decisions
// 2026-11-24 (plan/tasks.md, T25): due exactly 24 h and 1 h of elapsed time before the start; schedules named
// reminder-<bookingId>-24h / -1h deliver ReminderDue through the FIFO queue (group = booking ID); its eventId is a
// UUIDv5 of the causing event's eventId and the reminder type; emails go before reminder changes; a ReminderDue is
// dropped if the booking is not Confirmed, starts at another time, or has started.
describe('reminders (R9)', () => {
  let pool: Pool;
  beforeAll(() => {
    pool = testPool();
  });
  afterAll(async () => {
    await pool.end();
  });

  const parse = createEventParser();
  const run = (w: BookingWorld, event: object | string) => processMessage(typeof event === 'string' ? event : JSON.stringify(event), w.deps);
  const name = (w: BookingWorld, type: '24h' | '1h') => `reminder-${w.bookingId}-${type}`;
  const payload = (w: BookingWorld, type: '24h' | '1h') => parse(w.scheduler.schedules.get(name(w, type))!.input);

  describe('scheduling on BookingCreated', () => {
    it('schedules the 24h and 1h reminders, delivering ReminderDue to the booking\'s message group', async () => {
      const w = await bookingWorld(pool);
      const event = created(w);

      await run(w, event);

      expect(w.scheduler.calls).toEqual([`upsert ${name(w, '24h')}`, `upsert ${name(w, '1h')}`]);
      const day = w.scheduler.schedules.get(name(w, '24h'))!;
      expect(day.at.toISOString()).toBe('2026-11-22T19:00:00.000Z');
      expect(day.messageGroupId).toBe(w.bookingId);
      expect(w.scheduler.schedules.get(name(w, '1h'))!.at.toISOString()).toBe('2026-11-23T18:00:00.000Z');

      // The payloads are valid ReminderDue v1 events (the schema check is in parse).
      expect(payload(w, '24h')).toEqual({
        eventId: reminderEventId(event.eventId, '24h'),
        eventType: 'ReminderDue',
        schemaVersion: 1,
        occurredAt: '2026-11-22T19:00:00.000Z',
        correlationId: 'corr-t24',
        data: { bookingId: w.bookingId, reminder: '24h', scheduledForStartUtc: '2026-11-23T19:00:00.000Z' },
      });
      expect(payload(w, '1h')).toMatchObject({
        eventId: reminderEventId(event.eventId, '1h'),
        occurredAt: '2026-11-23T18:00:00.000Z',
        data: { reminder: '1h' },
      });
      expect(reminderEventId(event.eventId, '24h')).not.toBe(reminderEventId(event.eventId, '1h'));
    });

    it('R9: a booking made less than 24 hours ahead skips the 24h reminder and still schedules the 1h one', async () => {
      const w = await bookingWorld(pool, { now: '2026-11-22T20:00:00Z' }); // 23 hours before the start

      await run(w, created(w));

      expect(w.scheduler.calls).toEqual([`upsert ${name(w, '1h')}`]);
    });

    it('R9: a reminder due exactly now counts as passed; one due a minute later is scheduled', async () => {
      const atDue = await bookingWorld(pool, { now: '2026-11-22T19:00:00Z' });
      await run(atDue, created(atDue));
      expect(atDue.scheduler.calls).toEqual([`upsert ${name(atDue, '1h')}`]);

      const before = await bookingWorld(pool, { now: '2026-11-22T18:59:00Z' });
      await run(before, created(before));
      expect(before.scheduler.calls).toHaveLength(2);
    });

    it('R9: a booking made less than an hour ahead gets no reminders', async () => {
      const w = await bookingWorld(pool, { now: '2026-11-23T18:30:00Z' });

      await run(w, created(w));

      expect(w.scheduler.calls).toEqual([]);
      expect(w.email.sent).toHaveLength(1);
    });

    it('a redelivered event writes the same schedules with the same reminder IDs', async () => {
      const w = await bookingWorld(pool);
      const body = JSON.stringify(created(w));

      await run(w, body);
      const first = payload(w, '24h').eventId;
      await run(w, body);

      expect(payload(w, '24h').eventId).toBe(first);
      expect(w.email.sent).toHaveLength(1);
    });

    it('sends the emails before touching reminders: a failed email schedules nothing', async () => {
      const w = await bookingWorld(pool);
      w.email.failFor.add(await w.addressOf(w.customer));

      await expect(run(w, created(w))).rejects.toThrow('MessageRejected');

      expect(w.scheduler.calls).toEqual([]);
    });

    it('a scheduler failure fails the message; the retry schedules without a second email', async () => {
      const w = await bookingWorld(pool);
      const body = JSON.stringify(created(w));
      w.scheduler.failing = true;

      await expect(run(w, body)).rejects.toThrow('ServiceUnavailable');
      w.scheduler.failing = false;
      await run(w, body);

      expect(w.email.sent).toHaveLength(1);
      expect(w.scheduler.schedules.size).toBe(2);
    });
  });

  describe('moving on BookingRescheduled', () => {
    it('moves both reminders to the new start with new reminder IDs', async () => {
      const w = await bookingWorld(pool);
      const create = created(w);
      await run(w, create);
      w.scheduler.calls.length = 0;
      const move = rescheduled(w, w.sam);

      await run(w, move);

      expect(w.scheduler.calls).toEqual([`upsert ${name(w, '24h')}`, `upsert ${name(w, '1h')}`]);
      expect(w.scheduler.schedules.get(name(w, '24h'))!.at.toISOString()).toBe('2026-11-23T15:00:00.000Z');
      expect(w.scheduler.schedules.get(name(w, '1h'))!.at.toISOString()).toBe('2026-11-24T14:00:00.000Z');
      expect(payload(w, '24h')).toMatchObject({
        eventId: reminderEventId(move.eventId, '24h'),
        data: { scheduledForStartUtc: '2026-11-24T15:00:00.000Z' },
      });
      expect(payload(w, '24h').eventId).not.toBe(reminderEventId(create.eventId, '24h'));
    });

    it('R9: deletes a reminder whose new time has already passed', async () => {
      const w = await bookingWorld(pool, { now: '2026-11-23T16:00:00Z' }); // 23 hours before the new start

      await run(w, rescheduled(w, w.kim));

      expect(w.scheduler.calls).toEqual([`delete ${name(w, '24h')}`, `upsert ${name(w, '1h')}`]);
    });
  });

  describe('deleting on BookingCancelled', () => {
    it.each(['CustomerCancelled', 'BusinessCancelled', 'TimeOff', 'AvailabilityChanged', 'StaffRemoved'])(
      'R9: %s deletes both pending reminders, after the emails', async (reason) => {
        const w = await bookingWorld(pool);
        await run(w, created(w));
        w.scheduler.calls.length = 0;

        await run(w, cancelled(w, reason));

        expect(w.scheduler.calls).toEqual([`delete ${name(w, '24h')}`, `delete ${name(w, '1h')}`]);
        expect(w.scheduler.schedules.size).toBe(0);
        expect(w.email.sent.map((e) => e.subject.split(':')[0])).toEqual(['Booking confirmed', 'Booking cancelled', 'Booking cancelled']);
      });
  });

  it('BookingLocalTimeChanged and RebookedInsideWindow leave reminders alone', async () => {
    const w = await bookingWorld(pool);
    const slot = { staffId: w.sam, startUtc: SLOT_START, endUtc: '2026-11-23T20:00:00Z' };

    await run(w, envelope('BookingLocalTimeChanged', {
      bookingId: w.bookingId, businessId: w.businessId, customerId: w.customer, slot,
      oldTimeZone: 'America/New_York', newTimeZone: 'America/Chicago', timeZoneChangeId: randomUUID(),
    }));
    await run(w, envelope('RebookedInsideWindow', {
      businessId: w.businessId, businessTimeZone: 'America/New_York', customerId: w.customer,
      originalBookingId: w.bookingId, originalSlot: slot, newBookingId: w.bookingId, newSlot: slot,
    }));

    expect(w.scheduler.calls).toEqual([]);
  });

  describe('ReminderDue', () => {
    /** Schedules the reminders through BookingCreated, then returns the scheduled body for the given reminder. */
    async function scheduled(w: BookingWorld, type: '24h' | '1h'): Promise<string> {
      await run(w, created(w));
      return w.scheduler.schedules.get(name(w, type))!.input;
    }

    it('R9: emails the customer a 24h reminder in the T24 style', async () => {
      const w = await bookingWorld(pool);
      const body = await scheduled(w, '24h');
      w.clock.now = new Date('2026-11-22T19:00:30Z');

      await run(w, body);

      const mails = await w.to(w.customer);
      expect(mails).toHaveLength(2); // the confirmation, then the reminder
      const reminder = mails[1];
      expect(reminder.subject).toBe('Reminder: Haircut at Cut & Dry on Monday 23 November 2026, 14:00');
      expect(reminder.text).toBe([
        'Hello Ann Customer,',
        '',
        'This is a reminder of your booking in 24 hours.',
        '',
        'Service: Haircut',
        'Business: Cut & Dry',
        'With: Sam Staff',
        'When: Monday 23 November 2026, 14:00-15:00 America/New_York (UTC-05:00)',
        'Your time: Monday 23 November 2026, 19:00-20:00 Europe/London (UTC+00:00)',
        'Duration: 1 hour',
        '',
      ].join('\n'));
      const rows = await notificationsFor(pool, JSON.parse(body).eventId);
      expect(rows.map((r) => [r.recipient_user_id, r.kind, r.status])).toEqual([[w.customer, 'Reminder24h', 'Sent']]);
    });

    it('R9: emails the 1h reminder', async () => {
      const w = await bookingWorld(pool);
      const body = await scheduled(w, '1h');
      w.clock.now = new Date('2026-11-23T18:00:00Z');

      await run(w, body);

      const reminder = w.email.sent[1];
      expect(reminder.text).toContain('This is a reminder of your booking in 1 hour.');
      const rows = await notificationsFor(pool, JSON.parse(body).eventId);
      expect(rows.map((r) => r.kind)).toEqual(['Reminder1h']);
    });

    it('a redelivered reminder is sent once', async () => {
      const w = await bookingWorld(pool);
      const body = await scheduled(w, '1h');
      w.clock.now = new Date('2026-11-23T18:00:00Z');

      await run(w, body);
      await run(w, body);

      expect(w.email.sent).toHaveLength(2);
    });

    it('uses the names and the business\'s time zone current when it is sent', async () => {
      const w = await bookingWorld(pool);
      const body = await scheduled(w, '24h');
      await pool.query('UPDATE businesses SET time_zone = ? WHERE id = ?', ['America/Chicago', w.businessId]);
      await pool.query('UPDATE bookings SET staff_id = ? WHERE id = ?', [w.kim, w.bookingId]);
      w.clock.now = new Date('2026-11-22T19:00:00Z');

      await run(w, body);

      expect(w.email.sent[1].text).toContain('With: Kim Staff\nWhen: Monday 23 November 2026, 13:00-14:00 America/Chicago (UTC-06:00)');
    });

    describe('stale reminders are dropped without an email', () => {
      async function expectDropped(w: BookingWorld, body: string, reason: string) {
        await run(w, body);
        expect(w.email.sent).toHaveLength(1); // only the confirmation
        expect(await notificationsFor(pool, JSON.parse(body).eventId)).toHaveLength(0);
        expect(w.log.entries).toContainEqual(expect.objectContaining({
          level: 'info', message: 'Stale reminder dropped', reason, bookingId: w.bookingId, eventId: JSON.parse(body).eventId,
        }));
      }

      it('when the booking was cancelled', async () => {
        const w = await bookingWorld(pool);
        const body = await scheduled(w, '24h');
        await pool.query(
          `UPDATE bookings SET status = 'Cancelled', cancelled_at = UTC_TIMESTAMP(6), cancelled_by = customer_id,
             cancellation_reason = 'CustomerCancelled' WHERE id = ?`, [w.bookingId]);
        w.clock.now = new Date('2026-11-22T19:00:00Z');

        await expectDropped(w, body, 'NotConfirmed');
      });

      it('when the booking now starts at another time (a reminder for the old time)', async () => {
        const w = await bookingWorld(pool);
        const body = await scheduled(w, '24h');
        const newStart = new Date(NEW_START);
        const newEnd = new Date(newStart.getTime() + 3600_000);
        await pool.query(
          'UPDATE bookings SET start_utc = ?, end_utc = ?, blocked_start_utc = ?, blocked_end_utc = ? WHERE id = ?',
          [newStart, newEnd, newStart, newEnd, w.bookingId]);
        w.clock.now = new Date('2026-11-22T19:00:00Z');

        await expectDropped(w, body, 'StartChanged');
      });

      it('when the booking has already started (delivered late)', async () => {
        const w = await bookingWorld(pool);
        const body = await scheduled(w, '1h');
        w.clock.now = new Date(SLOT_START);

        await expectDropped(w, body, 'AlreadyStarted');
      });
    });

    it('fails the message if the booking does not exist', async () => {
      const w = await bookingWorld(pool);
      const event = envelope('ReminderDue', { bookingId: randomUUID(), reminder: '24h', scheduledForStartUtc: '2026-11-23T19:00:00.000Z' });

      await expect(run(w, event)).rejects.toBeInstanceOf(MissingRecordError);
      expect(w.email.sent).toHaveLength(0);
    });
  });

  it('reminder IDs are UUIDv5 values that differ by causing event and type', () => {
    const a = randomUUID();
    const b = randomUUID();
    expect(reminderEventId(a, '24h')).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(reminderEventId(a, '24h')).toBe(reminderEventId(a, '24h'));
    expect(new Set([reminderEventId(a, '24h'), reminderEventId(a, '1h'), reminderEventId(b, '24h')]).size).toBe(3);
  });
});
