import type { Pool, RowDataPacket } from 'mysql2/promise';

/** A row an event refers to is missing: fails the message, so it ends in the dead-letter queue (T24). */
export class MissingRecordError extends Error {
  override name = 'MissingRecordError';
}

export interface BusinessInfo {
  name: string;
  ownerUserId: string;
}

/**
 * What booking emails need beyond the event's IDs (T24, decided 2026-11-24), read when sending so names are current.
 * Only these columns are read, which T27 grants to the worker's database user: businesses (id, name, owner_user_id),
 * services (id, name), staff (id, user_id) and bookings (id, service_id).
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

  private async one(sql: string, id: string, what: string): Promise<RowDataPacket> {
    const [rows] = await this.pool.query<RowDataPacket[]>(sql, [id]);
    if (!rows[0]) throw new MissingRecordError(`${what} ${id} not found.`);
    return rows[0];
  }
}
