import { randomUUID } from 'node:crypto';
import type { Pool } from 'mysql2/promise';
import { bookingHandlers } from './bookingEmails';
import { ContactDirectory } from './contacts';
import { BookingDirectory, MissingRecordError } from './directory';
import { createEventParser } from './events';
import { NotificationStore } from './notifications';
import { processMessage, type PipelineDeps } from './pipeline';
import { CapturingLogger, FakeEmailSender } from './testing/fakes';
import { insertBooking, insertBusiness, insertService, insertStaff, insertUser, notificationsFor, testPool } from './testing/mysql';

// T24: booking emails. Acceptance: worker handling table in specs/06-events.md; R2, R4, R6. Decisions 2026-11-24
// (plan/tasks.md, T24): plain text; times in the business's zone, plus the recipient's own zone when it differs;
// staff- and owner-facing emails name the customer and give their email; cancellation wording by reason and recipient;
// one email per event and recipient, the customer first.
describe('booking emails', () => {
  let pool: Pool;
  beforeAll(() => {
    pool = testPool();
  });
  afterAll(async () => {
    await pool.end();
  });

  const SLOT_START = '2026-11-23T19:00:00Z'; // 14:00 in New York, 19:00 in London
  const SLOT_END = '2026-11-23T20:00:00Z';
  const NEW_START = '2026-11-24T15:00:00Z'; // 10:00 in New York
  const NEW_END = '2026-11-24T16:00:00Z';

  async function world() {
    const tag = randomUUID().slice(0, 8);
    const ownerUser = await insertUser(pool, { name: 'Olive Owner', email: `olive-${tag}@example.test`, timeZone: 'America/New_York' });
    const samUser = await insertUser(pool, { name: 'Sam Staff', email: `sam-${tag}@example.test`, timeZone: 'America/New_York' });
    const kimUser = await insertUser(pool, { name: 'Kim Staff', email: `kim-${tag}@example.test`, timeZone: 'America/Chicago' });
    const customer = await insertUser(pool, { name: 'Ann Customer', email: `ann-${tag}@example.test`, timeZone: 'Europe/London' });
    const businessId = await insertBusiness(pool, { ownerUserId: ownerUser, name: 'Cut & Dry', timeZone: 'America/New_York' });
    const serviceId = await insertService(pool, { businessId, name: 'Haircut' });
    const beardId = await insertService(pool, { businessId, name: 'Beard trim' });
    const sam = await insertStaff(pool, { businessId, userId: samUser });
    const kim = await insertStaff(pool, { businessId, userId: kimUser });
    const ownerStaff = await insertStaff(pool, { businessId, userId: ownerUser });
    const bookingId = await insertBooking(pool, { staffId: sam, serviceId, customerId: customer, startUtc: SLOT_START, endUtc: SLOT_END });

    const email = new FakeEmailSender();
    const contacts = new ContactDirectory(pool);
    const deps: PipelineDeps = {
      parse: createEventParser(),
      handlers: bookingHandlers({ directory: new BookingDirectory(pool), contacts }),
      notifications: new NotificationStore(pool),
      contacts,
      email,
      log: new CapturingLogger(),
    };
    const addressOf = async (userId: string) => (await contacts.find(userId))!.email;
    const to = async (userId: string) => {
      const address = await addressOf(userId);
      return email.sent.filter((e) => e.to === address);
    };
    return { ownerUser, samUser, kimUser, customer, businessId, serviceId, beardId, sam, kim, ownerStaff, bookingId, email, deps, addressOf, to, tag };
  }

  type World = Awaited<ReturnType<typeof world>>;

  function envelope(eventType: string, data: Record<string, unknown>) {
    return {
      eventId: randomUUID(),
      eventType,
      schemaVersion: 1,
      occurredAt: '2026-11-20T10:00:00.000000Z',
      correlationId: 'corr-t24',
      data,
    };
  }

  const created = (w: World, overrides: { customerId?: string; staffId?: string } = {}) => envelope('BookingCreated', {
    bookingId: w.bookingId,
    bookingVersion: 1,
    businessId: w.businessId,
    businessTimeZone: 'America/New_York',
    serviceId: w.serviceId,
    customerId: overrides.customerId ?? w.customer,
    slot: { staffId: overrides.staffId ?? w.sam, startUtc: SLOT_START, endUtc: SLOT_END },
    createdBy: { userId: w.customer, role: 'Customer' },
  });

  const rescheduled = (w: World, newStaffId: string) => envelope('BookingRescheduled', {
    bookingId: w.bookingId,
    bookingVersion: 2,
    businessId: w.businessId,
    businessTimeZone: 'America/New_York',
    serviceId: w.serviceId,
    customerId: w.customer,
    oldSlot: { staffId: w.sam, startUtc: SLOT_START, endUtc: SLOT_END },
    newSlot: { staffId: newStaffId, startUtc: NEW_START, endUtc: NEW_END },
    rescheduledBy: { userId: w.ownerUser, role: 'Owner' },
  });

  const cancelled = (w: World, reason: string, overrides: { customerId?: string; staffId?: string } = {}) => envelope('BookingCancelled', {
    bookingId: w.bookingId,
    bookingVersion: 2,
    businessId: w.businessId,
    businessTimeZone: 'America/New_York',
    serviceId: w.serviceId,
    customerId: overrides.customerId ?? w.customer,
    slot: { staffId: overrides.staffId ?? w.sam, startUtc: SLOT_START, endUtc: SLOT_END },
    cancelledBy: { userId: w.customer, role: 'Customer' },
    reason,
  });

  const run = (w: World, event: object) => processMessage(JSON.stringify(event), w.deps);

  describe('BookingCreated', () => {
    it('sends the customer a confirmation with the service, business, staff member and both time zones (R2)', async () => {
      const w = await world();
      const event = created(w);

      await run(w, event);

      expect(w.email.sent).toHaveLength(1);
      const [mail] = await w.to(w.customer);
      expect(mail.subject).toBe('Booking confirmed: Haircut at Cut & Dry on Monday 23 November 2026, 14:00');
      expect(mail.text).toContain('Hello Ann Customer,');
      expect(mail.text).toContain('Service: Haircut');
      expect(mail.text).toContain('Business: Cut & Dry');
      expect(mail.text).toContain('With: Sam Staff');
      expect(mail.text).toContain('When: Monday 23 November 2026, 14:00-15:00 America/New_York (UTC-05:00)');
      expect(mail.text).toContain('Your time: Monday 23 November 2026, 19:00-20:00 Europe/London (UTC+00:00)');
      expect(mail.text).toContain('Duration: 1 hour');
      expect(mail.html).toBeUndefined();
      const rows = await notificationsFor(pool, event.eventId);
      expect(rows.map((r) => [r.recipient_user_id, r.kind, r.status])).toEqual([[w.customer, 'Confirmation', 'Sent']]);
    });

    it('omits the customer\'s own zone when it is the business\'s (R2)', async () => {
      const w = await world();
      // Sam (America/New_York) books as a customer with Kim.
      await run(w, created(w, { customerId: w.samUser, staffId: w.kim }));

      const [mail] = await w.to(w.samUser);
      expect(mail.text).toContain('When: Monday 23 November 2026, 14:00-15:00 America/New_York (UTC-05:00)');
      expect(mail.text).not.toContain('Your time:');
    });

    it('R2: a full confirmation for a 2-hour booking at 01:00 on a spring-forward night', async () => {
      const w = await world();
      const event = created(w, { customerId: w.samUser, staffId: w.kim });
      (event.data as { slot: object }).slot = { staffId: w.kim, startUtc: '2026-03-08T06:00:00Z', endUtc: '2026-03-08T08:00:00Z' };

      await run(w, event);

      const [mail] = await w.to(w.samUser);
      expect(mail.subject).toBe('Booking confirmed: Haircut at Cut & Dry on Sunday 8 March 2026, 01:00');
      expect(mail.text).toBe([
        'Hello Sam Staff,',
        '',
        'Your booking is confirmed.',
        '',
        'Service: Haircut',
        'Business: Cut & Dry',
        'With: Kim Staff',
        'When: Sunday 8 March 2026, 01:00 (UTC-05:00) - 04:00 (UTC-04:00) America/New_York',
        'Duration: 2 hours',
        '',
      ].join('\n'));
    });

    it('R2: a booking crossing midnight in the customer\'s zone names both dates there', async () => {
      const w = await world();
      const event = created(w);
      // 18:30-19:30 in New York is 23:30-00:30 in London.
      (event.data as { slot: object }).slot = { staffId: w.sam, startUtc: '2026-11-23T23:30:00Z', endUtc: '2026-11-24T00:30:00Z' };

      await run(w, event);

      const [mail] = await w.to(w.customer);
      expect(mail.text).toContain('When: Monday 23 November 2026, 18:30-19:30 America/New_York (UTC-05:00)\n' +
        'Your time: Monday 23 November 2026, 23:30 - Tuesday 24 November 2026, 00:30 Europe/London (UTC+00:00)\n' +
        'Duration: 1 hour');
    });

    it('a duplicate event sends the confirmation once', async () => {
      const w = await world();
      const body = JSON.stringify(created(w));

      await processMessage(body, w.deps);
      await processMessage(body, w.deps);

      expect(w.email.sent).toHaveLength(1);
    });

    it('names the current names at send time', async () => {
      const w = await world();
      await pool.query('UPDATE services SET name = ? WHERE id = ?', ['Haircut deluxe', w.serviceId]);

      await run(w, created(w));

      expect(w.email.sent[0].subject).toBe('Booking confirmed: Haircut deluxe at Cut & Dry on Monday 23 November 2026, 14:00');
    });

    it('fails the message without sending if a referenced row is missing', async () => {
      const w = await world();
      const event = created(w);
      (event.data as { serviceId: string }).serviceId = randomUUID();

      await expect(run(w, event)).rejects.toBeInstanceOf(MissingRecordError);
      expect(w.email.sent).toHaveLength(0);
    });
  });

  describe('BookingRescheduled', () => {
    it('emails the customer and the staff member, showing the old and new times', async () => {
      const w = await world();
      const event = rescheduled(w, w.sam);

      await run(w, event);

      expect(w.email.sent).toHaveLength(2);
      const [toCustomer] = await w.to(w.customer);
      expect(toCustomer.subject).toBe('Booking rescheduled: Haircut at Cut & Dry, now on Tuesday 24 November 2026, 10:00');
      expect(toCustomer.text).toContain('Your booking has been moved.');
      expect(toCustomer.text).toContain('Before:\nWith: Sam Staff\nWhen: Monday 23 November 2026, 14:00-15:00 America/New_York (UTC-05:00)\nYour time: Monday 23 November 2026, 19:00-20:00 Europe/London (UTC+00:00)');
      expect(toCustomer.text).toContain('Now:\nWith: Sam Staff\nWhen: Tuesday 24 November 2026, 10:00-11:00 America/New_York (UTC-05:00)\nYour time: Tuesday 24 November 2026, 15:00-16:00 Europe/London (UTC+00:00)');
      expect(toCustomer.text).not.toContain(await w.addressOf(w.customer));

      const [toSam] = await w.to(w.samUser);
      expect(toSam.text).toContain('A booking with you has been moved.');
      expect(toSam.text).toContain(`Customer: Ann Customer <${await w.addressOf(w.customer)}>`);
      expect(toSam.text).not.toContain('Your time:');
      const rows = await notificationsFor(pool, event.eventId);
      expect(rows.map((r) => r.kind)).toEqual(['Rescheduled', 'Rescheduled']);
    });

    it('when the staff member changes, emails the customer and both staff members with role wording', async () => {
      const w = await world();

      await run(w, rescheduled(w, w.kim));

      expect(w.email.sent).toHaveLength(3);
      const [toCustomer] = await w.to(w.customer);
      expect(toCustomer.text).toContain('Before:\nWith: Sam Staff');
      expect(toCustomer.text).toContain('Now:\nWith: Kim Staff');
      const [toSam] = await w.to(w.samUser);
      expect(toSam.text).toContain('This booking has been moved to Kim Staff.');
      const [toKim] = await w.to(w.kimUser);
      expect(toKim.text).toContain('A booking has been moved to you from Sam Staff.');
      // Kim is in America/Chicago, so sees her own zone too (R2).
      expect(toKim.text).toContain('Your time: Tuesday 24 November 2026, 09:00-10:00 America/Chicago (UTC-06:00)');
      expect(w.email.sent.map((e) => e.to)).toEqual([await w.addressOf(w.customer), await w.addressOf(w.kimUser), await w.addressOf(w.samUser)]);
    });
  });

  describe('BookingCancelled (R6)', () => {
    const wording: [string, string, string][] = [
      ['CustomerCancelled', 'You cancelled this booking.', 'The customer cancelled this booking.'],
      ['BusinessCancelled', 'Cut & Dry cancelled your booking.', 'This booking was cancelled by the business.'],
      ['TimeOff', 'Your booking was cancelled because Sam Staff is no longer available at that time.', 'This booking was cancelled because you are no longer available at that time.'],
      ['AvailabilityChanged', 'Your booking was cancelled because Sam Staff is no longer available at that time.', 'This booking was cancelled because you are no longer available at that time.'],
      ['StaffRemoved', 'Your booking was cancelled because Sam Staff is no longer available at Cut & Dry.', 'You were removed as staff at Cut & Dry, so this booking was cancelled.'],
    ];

    it.each(wording)('%s: emails the customer and the staff member with wording by reason', async (reason, toCustomerLine, toStaffLine) => {
      const w = await world();
      const event = cancelled(w, reason);

      await run(w, event);

      expect(w.email.sent).toHaveLength(2);
      const [toCustomer] = await w.to(w.customer);
      expect(toCustomer.subject).toBe('Booking cancelled: Haircut at Cut & Dry on Monday 23 November 2026, 14:00');
      expect(toCustomer.text).toContain(toCustomerLine);
      expect(toCustomer.text).toContain('When: Monday 23 November 2026, 14:00-15:00 America/New_York (UTC-05:00)');
      const [toSam] = await w.to(w.samUser);
      expect(toSam.text).toContain(toStaffLine);
      expect(toSam.text).toContain(`Customer: Ann Customer <${await w.addressOf(w.customer)}>`);
      const rows = await notificationsFor(pool, event.eventId);
      expect(rows.map((r) => r.kind)).toEqual(['Cancelled', 'Cancelled']);
    });

    it('StaffRemoved reaches the removed staff member (their account still exists)', async () => {
      const w = await world();
      await pool.query('UPDATE staff SET removed_at = UTC_TIMESTAMP(6) WHERE id = ?', [w.sam]);

      await run(w, cancelled(w, 'StaffRemoved'));

      expect(await w.to(w.samUser)).toHaveLength(1);
    });

    it('a user who is both the customer and the staff member gets one email, with the customer wording', async () => {
      const w = await world();
      const event = cancelled(w, 'BusinessCancelled', { customerId: w.ownerUser, staffId: w.ownerStaff });

      await run(w, event);

      expect(w.email.sent).toHaveLength(1);
      expect(w.email.sent[0].text).toContain('Cut & Dry cancelled your booking.');
      expect(await notificationsFor(pool, event.eventId)).toHaveLength(1);
    });
  });

  describe('BookingLocalTimeChanged (R2)', () => {
    it('emails the customer the new local time in the new zone and the old one in the old zone', async () => {
      const w = await world();
      const event = envelope('BookingLocalTimeChanged', {
        bookingId: w.bookingId,
        businessId: w.businessId,
        customerId: w.customer,
        slot: { staffId: w.sam, startUtc: SLOT_START, endUtc: SLOT_END },
        oldTimeZone: 'America/New_York',
        newTimeZone: 'America/Chicago',
        timeZoneChangeId: randomUUID(),
      });

      await run(w, event);

      expect(w.email.sent).toHaveLength(1);
      const [mail] = await w.to(w.customer);
      expect(mail.subject).toBe('New local time for your booking: Haircut at Cut & Dry');
      expect(mail.text).toContain('Cut & Dry has changed its time zone from America/New_York to America/Chicago.');
      expect(mail.text).toContain('Your booking has not moved, but its local time at the business has changed.');
      expect(mail.text).toContain('With: Sam Staff');
      expect(mail.text).toContain('When: Monday 23 November 2026, 13:00-14:00 America/Chicago (UTC-06:00)');
      expect(mail.text).toContain('Your time: Monday 23 November 2026, 19:00-20:00 Europe/London (UTC+00:00)');
      expect(mail.text).toContain('Previously: Monday 23 November 2026, 14:00-15:00 America/New_York (UTC-05:00)');
      const rows = await notificationsFor(pool, event.eventId);
      expect(rows.map((r) => r.kind)).toEqual(['LocalTimeChanged']);
    });
  });

  describe('BookingLocalTimeChanged when the customer is in the new zone (R2)', () => {
    it('leaves out the customer\'s own zone and still shows the previous local time', async () => {
      const w = await world();
      // Kim (America/Chicago) is the customer, and Chicago is the new zone.
      const bookingId = await insertBooking(pool, { staffId: w.sam, serviceId: w.serviceId, customerId: w.kimUser, startUtc: SLOT_START, endUtc: SLOT_END });
      await run(w, envelope('BookingLocalTimeChanged', {
        bookingId,
        businessId: w.businessId,
        customerId: w.kimUser,
        slot: { staffId: w.sam, startUtc: SLOT_START, endUtc: SLOT_END },
        oldTimeZone: 'America/New_York',
        newTimeZone: 'America/Chicago',
        timeZoneChangeId: randomUUID(),
      }));

      const [mail] = await w.to(w.kimUser);
      expect(mail.text).toContain('With: Sam Staff\n' +
        'When: Monday 23 November 2026, 13:00-14:00 America/Chicago (UTC-06:00)\n' +
        'Previously: Monday 23 November 2026, 14:00-15:00 America/New_York (UTC-05:00)\n' +
        'Duration: 1 hour\n');
      expect(mail.text).not.toContain('Your time:');
    });
  });

  describe('RebookedInsideWindow (R4)', () => {
    async function rebook(w: World, originalStaffId: string, newStaffId: string = w.kim) {
      const originalId = await insertBooking(pool, { staffId: originalStaffId, serviceId: w.serviceId, customerId: w.customer, startUtc: SLOT_START, endUtc: SLOT_END });
      const newId = await insertBooking(pool, { staffId: newStaffId, serviceId: w.beardId, customerId: w.customer, startUtc: NEW_START, endUtc: NEW_END });
      return envelope('RebookedInsideWindow', {
        businessId: w.businessId,
        businessTimeZone: 'America/New_York',
        customerId: w.customer,
        originalBookingId: originalId,
        originalSlot: { staffId: originalStaffId, startUtc: SLOT_START, endUtc: SLOT_END },
        newBookingId: newId,
        newSlot: { staffId: newStaffId, startUtc: NEW_START, endUtc: NEW_END },
      });
    }

    it('emails the original booking\'s staff member and the owner, naming the customer and both bookings', async () => {
      const w = await world();
      const event = await rebook(w, w.sam);

      await run(w, event);

      expect(w.email.sent.map((e) => e.to)).toEqual([await w.addressOf(w.samUser), await w.addressOf(w.ownerUser)]);
      for (const mail of w.email.sent) {
        expect(mail.subject).toBe('Ann Customer holds two bookings at Cut & Dry');
        expect(mail.text).toContain(`Customer: Ann Customer <${await w.addressOf(w.customer)}>`);
        expect(mail.text).toContain('The original booking still stands');
        expect(mail.text).toContain('Original booking:\nService: Haircut\nWith: Sam Staff\nWhen: Monday 23 November 2026, 14:00-15:00 America/New_York (UTC-05:00)');
        expect(mail.text).toContain('New booking:\nService: Beard trim\nWith: Kim Staff\nWhen: Tuesday 24 November 2026, 10:00-11:00 America/New_York (UTC-05:00)');
      }
      const rows = await notificationsFor(pool, event.eventId);
      expect(rows.map((r) => r.kind)).toEqual(['RebookedInsideWindow', 'RebookedInsideWindow']);
    });

    it('names the same staff member for both bookings when the new one is with them too', async () => {
      const w = await world();

      await run(w, await rebook(w, w.sam, w.sam));

      expect(w.email.sent.map((e) => e.to)).toEqual([await w.addressOf(w.samUser), await w.addressOf(w.ownerUser)]);
      for (const mail of w.email.sent) {
        expect(mail.text).toContain('Original booking:\nService: Haircut\nWith: Sam Staff\n');
        expect(mail.text).toContain('New booking:\nService: Beard trim\nWith: Sam Staff\n');
      }
    });

    it('sends one email when the owner is the original booking\'s staff member', async () => {
      const w = await world();

      await run(w, await rebook(w, w.ownerStaff));

      expect(w.email.sent.map((e) => e.to)).toEqual([await w.addressOf(w.ownerUser)]);
    });
  });
});
