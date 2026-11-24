import { randomUUID } from 'node:crypto';
import type { Pool } from 'mysql2/promise';
import { ContactDirectory } from '../contacts';
import { createEventParser } from '../events';
import { NotificationStore } from '../notifications';
import type { PipelineDeps } from '../pipeline';
import { createHandlers } from '../registry';
import { CapturingLogger, FakeEmailSender, FakeReminderScheduler } from './fakes';
import { insertBooking, insertBusiness, insertService, insertStaff, insertUser } from './mysql';

// Fixtures for the booking event tests (T24, T25): a business in New York with an owner who is also staff, two more
// staff members, a customer in London, two services and one booking, wired to the production handler registry with
// fake email and scheduler and a fixed clock. Fixtures are inserted through `pool` (root); the handlers, notifications and
// contact lookups use `worker`, the limited user made from db/worker-grants.sql (T26).

export const SLOT_START = '2026-11-23T19:00:00Z'; // 14:00 in New York, 19:00 in London
export const SLOT_END = '2026-11-23T20:00:00Z';
export const NEW_START = '2026-11-24T15:00:00Z'; // 10:00 in New York
export const NEW_END = '2026-11-24T16:00:00Z';
/** The app settings handlers get in tests (T26). */
export const TEST_APP = { baseUrl: 'https://app.example.test', name: 'Bookly' };
/** The fixed "now" of the handlers unless a test passes another: days before both slots. */
export const DEFAULT_NOW = '2026-11-20T10:00:00Z';

export async function bookingWorld(pools: { pool: Pool; worker: Pool }, options: { now?: string } = {}) {
  const { pool, worker } = pools;
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
  const scheduler = new FakeReminderScheduler();
  const log = new CapturingLogger();
  const clock = { now: new Date(options.now ?? DEFAULT_NOW) };
  const contacts = new ContactDirectory(pool);
  const deps: PipelineDeps = {
    parse: createEventParser(),
    handlers: createHandlers(worker, { scheduler, app: TEST_APP, now: () => clock.now }),
    notifications: new NotificationStore(worker),
    contacts: new ContactDirectory(worker),
    email,
    log,
  };
  const addressOf = async (userId: string) => (await contacts.find(userId))!.email;
  const to = async (userId: string) => {
    const address = await addressOf(userId);
    return email.sent.filter((e) => e.to === address);
  };
  return {
    ownerUser, samUser, kimUser, customer, businessId, serviceId, beardId, sam, kim, ownerStaff, bookingId,
    email, scheduler, log, clock, deps, addressOf, to, tag,
  };
}

export type BookingWorld = Awaited<ReturnType<typeof bookingWorld>>;

export function envelope(eventType: string, data: Record<string, unknown>, correlationId = 'corr-t24') {
  return {
    eventId: randomUUID(),
    eventType,
    schemaVersion: 1,
    occurredAt: '2026-11-20T10:00:00.000000Z',
    correlationId,
    data,
  };
}

export const created = (w: BookingWorld, overrides: { customerId?: string; staffId?: string; startUtc?: string; endUtc?: string } = {}) =>
  envelope('BookingCreated', {
    bookingId: w.bookingId,
    bookingVersion: 1,
    businessId: w.businessId,
    businessTimeZone: 'America/New_York',
    serviceId: w.serviceId,
    customerId: overrides.customerId ?? w.customer,
    slot: { staffId: overrides.staffId ?? w.sam, startUtc: overrides.startUtc ?? SLOT_START, endUtc: overrides.endUtc ?? SLOT_END },
    createdBy: { userId: w.customer, role: 'Customer' },
  });

export const rescheduled = (w: BookingWorld, newStaffId: string, newSlot: { startUtc: string; endUtc: string } = { startUtc: NEW_START, endUtc: NEW_END }) =>
  envelope('BookingRescheduled', {
    bookingId: w.bookingId,
    bookingVersion: 2,
    businessId: w.businessId,
    businessTimeZone: 'America/New_York',
    serviceId: w.serviceId,
    customerId: w.customer,
    oldSlot: { staffId: w.sam, startUtc: SLOT_START, endUtc: SLOT_END },
    newSlot: { staffId: newStaffId, ...newSlot },
    rescheduledBy: { userId: w.ownerUser, role: 'Owner' },
  });

export const cancelled = (w: BookingWorld, reason: string, overrides: { customerId?: string; staffId?: string } = {}) =>
  envelope('BookingCancelled', {
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
