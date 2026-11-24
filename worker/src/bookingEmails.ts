// Booking emails (T24): handlers for BookingCreated, BookingRescheduled, BookingCancelled, BookingLocalTimeChanged and
// RebookedInsideWindow (specs/06-events.md "Worker handling"). Decisions 2026-11-24 (plan/tasks.md, T24): plain text,
// no links; times in the business's zone plus the recipient's own zone when it differs (R2); staff- and owner-facing
// emails name the customer and give their email; one email per event and recipient (the notifications key), sent to
// the customer first so the customer wording wins for a user in two roles; events are sent as they are, never checked
// for staleness.
import type { Contact, ContactDirectory } from './contacts';
import { MissingRecordError, type BookingDirectory } from './directory';
import type { EventHandler } from './pipeline';
import { formatRange, formatStart, whenLines } from './time';

export interface BookingEmailDeps {
  directory: Pick<BookingDirectory, 'business' | 'serviceName' | 'staffUserId' | 'bookingServiceId'>;
  contacts: Pick<ContactDirectory, 'find'>;
}

interface Slot {
  staffId: string;
  startUtc: string;
  endUtc: string;
}

type CancellationReason = 'CustomerCancelled' | 'BusinessCancelled' | 'TimeOff' | 'AvailabilityChanged' | 'StaffRemoved';

/** The handlers by "EventType.vN", for the pipeline's registry. */
export function bookingHandlers(deps: BookingEmailDeps): Map<string, EventHandler> {
  const { directory } = deps;

  /** Someone named in an email who is not its recipient (the staff member, or the customer in a staff email). */
  async function person(userId: string): Promise<Contact> {
    const contact = await deps.contacts.find(userId);
    if (!contact) throw new MissingRecordError(`User ${userId} not found.`);
    return contact;
  }

  async function staffMember(staffId: string): Promise<Contact> {
    return person(await directory.staffUserId(staffId));
  }

  const bookingCreated: EventHandler = async ({ event, sendEmail }) => {
    const data = event.data as { businessId: string; businessTimeZone: string; serviceId: string; customerId: string; slot: Slot };
    const zone = data.businessTimeZone;
    const business = await directory.business(data.businessId);
    const service = await directory.serviceName(data.serviceId);
    const staff = await staffMember(data.slot.staffId);

    await sendEmail(data.customerId, 'Confirmation', (to) => ({
      subject: `Booking confirmed: ${service} at ${business.name} on ${formatStart(data.slot.startUtc, zone)}`,
      text: body(to, 'Your booking is confirmed.', [
        `Service: ${service}`,
        `Business: ${business.name}`,
        `With: ${staff.name}`,
        ...whenLines(data.slot, zone, to.timeZone),
      ]),
    }));
  };

  const bookingRescheduled: EventHandler = async ({ event, sendEmail }) => {
    const data = event.data as {
      businessId: string; businessTimeZone: string; serviceId: string; customerId: string; oldSlot: Slot; newSlot: Slot;
    };
    const zone = data.businessTimeZone;
    const business = await directory.business(data.businessId);
    const service = await directory.serviceName(data.serviceId);
    const customer = await person(data.customerId);
    const oldStaff = await staffMember(data.oldSlot.staffId);
    const newStaff = data.newSlot.staffId === data.oldSlot.staffId ? oldStaff : await staffMember(data.newSlot.staffId);
    const staffChanged = newStaff !== oldStaff;

    const compose = (intro: string, forBusiness: boolean) => (to: Contact) => ({
      subject: `Booking rescheduled: ${service} at ${business.name}, now on ${formatStart(data.newSlot.startUtc, zone)}`,
      text: body(to, intro, [
        `Service: ${service}`,
        `Business: ${business.name}`,
        ...(forBusiness ? [customerLine(customer)] : []),
        '',
        'Before:',
        `With: ${oldStaff.name}`,
        ...whenLines(data.oldSlot, zone, to.timeZone),
        '',
        'Now:',
        `With: ${newStaff.name}`,
        ...whenLines(data.newSlot, zone, to.timeZone),
      ]),
    });

    await sendEmail(data.customerId, 'Rescheduled', compose('Your booking has been moved.', false));
    if (!staffChanged) {
      await sendEmail(newStaff.userId, 'Rescheduled', compose('A booking with you has been moved.', true));
      return;
    }
    await sendEmail(newStaff.userId, 'Rescheduled', compose(`A booking has been moved to you from ${oldStaff.name}.`, true));
    await sendEmail(oldStaff.userId, 'Rescheduled', compose(`This booking has been moved to ${newStaff.name}.`, true));
  };

  const bookingCancelled: EventHandler = async ({ event, sendEmail }) => {
    const data = event.data as {
      businessId: string; businessTimeZone: string; serviceId: string; customerId: string; slot: Slot; reason: CancellationReason;
    };
    const zone = data.businessTimeZone;
    const business = await directory.business(data.businessId);
    const service = await directory.serviceName(data.serviceId);
    const customer = await person(data.customerId);
    const staff = await staffMember(data.slot.staffId);
    const lines = cancellationLines(data.reason, business.name, staff.name);

    const compose = (intro: string, forBusiness: boolean) => (to: Contact) => ({
      subject: `Booking cancelled: ${service} at ${business.name} on ${formatStart(data.slot.startUtc, zone)}`,
      text: body(to, intro, [
        `Service: ${service}`,
        `Business: ${business.name}`,
        ...(forBusiness ? [customerLine(customer)] : []),
        `With: ${staff.name}`,
        ...whenLines(data.slot, zone, to.timeZone),
      ]),
    });

    // R6: cancellation notifications go to all affected users, including a removed staff member (StaffRemoved).
    await sendEmail(data.customerId, 'Cancelled', compose(lines.toCustomer, false));
    await sendEmail(staff.userId, 'Cancelled', compose(lines.toStaff, true));
  };

  const bookingLocalTimeChanged: EventHandler = async ({ event, sendEmail }) => {
    const data = event.data as {
      bookingId: string; businessId: string; customerId: string; slot: Slot; oldTimeZone: string; newTimeZone: string;
    };
    const business = await directory.business(data.businessId);
    const service = await directory.serviceName(await directory.bookingServiceId(data.bookingId));
    const staff = await staffMember(data.slot.staffId);

    // R2: the booking keeps its UTC times; its local time in the business's zone changed.
    await sendEmail(data.customerId, 'LocalTimeChanged', (to) => {
      const [when, ...rest] = whenLines(data.slot, data.newTimeZone, to.timeZone);
      return {
        subject: `New local time for your booking: ${service} at ${business.name}`,
        text: body(to,
          `${business.name} has changed its time zone from ${data.oldTimeZone} to ${data.newTimeZone}.\n` +
          'Your booking has not moved, but its local time at the business has changed.', [
            `Service: ${service}`,
            `Business: ${business.name}`,
            `With: ${staff.name}`,
            when,
            `Previously: ${formatRange(data.slot.startUtc, data.slot.endUtc, data.oldTimeZone)}`,
            ...rest,
          ]),
      };
    });
  };

  const rebookedInsideWindow: EventHandler = async ({ event, sendEmail }) => {
    const data = event.data as {
      businessId: string; businessTimeZone: string; customerId: string;
      originalBookingId: string; originalSlot: Slot; newBookingId: string; newSlot: Slot;
    };
    const zone = data.businessTimeZone;
    const business = await directory.business(data.businessId);
    const customer = await person(data.customerId);
    const originalService = await directory.serviceName(await directory.bookingServiceId(data.originalBookingId));
    const newService = await directory.serviceName(await directory.bookingServiceId(data.newBookingId));
    const originalStaff = await staffMember(data.originalSlot.staffId);
    const newStaff = data.newSlot.staffId === data.originalSlot.staffId ? originalStaff : await staffMember(data.newSlot.staffId);

    const compose = (to: Contact) => ({
      subject: `${customer.name} holds two bookings at ${business.name}`,
      text: body(to,
        `${customer.name} has made a new booking at ${business.name} while holding a booking that starts within the ` +
        'cancellation window. The original booking still stands, so cancel it if the customer no longer needs it.', [
          `Business: ${business.name}`,
          customerLine(customer),
          '',
          'Original booking:',
          `Service: ${originalService}`,
          `With: ${originalStaff.name}`,
          ...whenLines(data.originalSlot, zone, to.timeZone),
          '',
          'New booking:',
          `Service: ${newService}`,
          `With: ${newStaff.name}`,
          ...whenLines(data.newSlot, zone, to.timeZone),
        ]),
    });

    // R4: the original booking's staff member, then the business's owner (one email if they are the same user).
    await sendEmail(originalStaff.userId, 'RebookedInsideWindow', compose);
    await sendEmail(business.ownerUserId, 'RebookedInsideWindow', compose);
  };

  return new Map<string, EventHandler>([
    ['BookingCreated.v1', bookingCreated],
    ['BookingRescheduled.v1', bookingRescheduled],
    ['BookingCancelled.v1', bookingCancelled],
    ['BookingLocalTimeChanged.v1', bookingLocalTimeChanged],
    ['RebookedInsideWindow.v1', rebookedInsideWindow],
  ]);
}

/** One sentence by reason for each recipient, naming no actor (decided 2026-11-24). */
export function cancellationLines(reason: CancellationReason, businessName: string, staffName: string): { toCustomer: string; toStaff: string } {
  switch (reason) {
    case 'CustomerCancelled':
      return { toCustomer: 'You cancelled this booking.', toStaff: 'The customer cancelled this booking.' };
    case 'BusinessCancelled':
      return { toCustomer: `${businessName} cancelled your booking.`, toStaff: 'This booking was cancelled by the business.' };
    case 'TimeOff':
    case 'AvailabilityChanged':
      return {
        toCustomer: `Your booking was cancelled because ${staffName} is no longer available at that time.`,
        toStaff: 'This booking was cancelled because you are no longer available at that time.',
      };
    case 'StaffRemoved':
      return {
        toCustomer: `Your booking was cancelled because ${staffName} is no longer available at ${businessName}.`,
        toStaff: `You were removed as staff at ${businessName}, so this booking was cancelled.`,
      };
    default:
      // The schema only allows the five reasons above; a new one needs wording before it is sent.
      throw new Error(`No wording for cancellation reason ${String(reason)}.`);
  }
}

function customerLine(customer: Contact): string {
  return `Customer: ${customer.name} <${customer.email}>`;
}

function body(to: Contact, intro: string, lines: string[]): string {
  return [`Hello ${to.name},`, '', intro, '', ...lines, ''].join('\n');
}
