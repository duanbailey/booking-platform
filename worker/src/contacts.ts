import type { Pool, RowDataPacket } from 'mysql2/promise';

/** What an email needs about its recipient, read when sending (events carry IDs only, specs/06-events.md). */
export interface Contact {
  userId: string;
  email: string;
  name: string;
  timeZone: string;
}

/**
 * Looks recipients up in users. Only these columns are read: never password_hash, which the worker's database user
 * may not read (specs/07-nfr.md).
 */
export class ContactDirectory {
  constructor(private readonly pool: Pool) {}

  async find(userId: string): Promise<Contact | null> {
    const [rows] = await this.pool.query<RowDataPacket[]>('SELECT id, email, name, time_zone FROM users WHERE id = ?', [userId]);
    const row = rows[0];
    return row ? { userId: row.id, email: row.email, name: row.name, timeZone: row.time_zone } : null;
  }
}
