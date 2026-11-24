import type { Pool, RowDataPacket } from 'mysql2/promise';

/** A row an event refers to is missing: fails the message, so it ends in the dead-letter queue (T24). */
export class MissingRecordError extends Error {
  override name = 'MissingRecordError';
}

/** A booking as a ReminderDue needs it (T25): to check staleness and to write the email. */
export interface ReminderBooking {
  staffId: string;
  serviceId: string;
  customerId: string;
  businessId: string;
  /** The business's current zone (R2: a zone change sends BookingLocalTimeChanged, so reminders use the new one). */
  businessTimeZone: string;
  startUtc: Date;
  endUtc: Date;
  status: 'Confirmed' | 'Cancelled';
}

export interface BusinessInfo {
  name: string;
  ownerUserId: string;
}

/**
 * What booking emails need beyond the event's IDs (T24, decided 2026-11-24), read when sending so names are current.
 * Only these columns are read, which T27 grants to the worker's database user: businesses (id, name, owner_user_id),
 * services (id, name), staff (id, user_id) and bookings (id, service_id); for T25's reminders also bookings
 * (staff_id, customer_id, start_utc, end_utc, status), services (business_id) and businesses (time_zone).
 */
export class BookingDirectory {
  constructor(private readonly pool: Pool) {}

  async business(id: string): Promise<BusinessInfo> {
    const row = await this.one('SELECT name, owner_user_id FROM businesses WHERE id = ?', id, 'Business');
    return { name: row.name, ownerUserId: row.owner_user_id };
  }

  async serviceName(id: string): Promise<string> {
    return (await this.one('SELECT name FROM services WHERE id = ?', id, 'Service')).name;
  }

  /** The user behind a staff row; removed staff keep their row (specs/05-data-model.md), so they can still be emailed. */
  async staffUserId(staffId: string): Promise<string> {
    return (await this.one('SELECT user_id FROM staff WHERE id = ?', staffId, 'Staff member')).user_id;
  }

  async bookingServiceId(bookingId: string): Promise<string> {
    return (await this.one('SELECT service_id FROM bookings WHERE id = ?', bookingId, 'Booking')).service_id;
  }

  async reminderBooking(bookingId: string): Promise<ReminderBooking> {
    const row = await this.one(
      `SELECT b.staff_id, b.service_id, b.customer_id, b.start_utc, b.end_utc, b.status, s.business_id, biz.time_zone
       FROM bookings b JOIN services s ON s.id = b.service_id JOIN businesses biz ON biz.id = s.business_id
       WHERE b.id = ?`, bookingId, 'Booking');
    return {
      staffId: row.staff_id,
      serviceId: row.service_id,
      customerId: row.customer_id,
      businessId: row.business_id,
      businessTimeZone: row.time_zone,
      startUtc: row.start_utc,
      endUtc: row.end_utc,
      status: row.status,
    };
  }

  private async one(sql: string, id: string, what: string): Promise<RowDataPacket> {
    const [rows] = await this.pool.query<RowDataPacket[]>(sql, [id]);
    if (!rows[0]) throw new MissingRecordError(`${what} ${id} not found.`);
    return rows[0];
  }
}
