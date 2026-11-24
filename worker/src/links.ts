import { createHash, randomBytes } from 'node:crypto';
import type { Pool, RowDataPacket } from 'mysql2/promise';
import { MissingRecordError } from './directory';

export type LinkTable = 'user_invites' | 'password_resets';

/** Why a link is not sent (specs/06-events.md: its link could never work). */
export type LinkSkipReason = 'Used' | 'Superseded';

/**
 * Invite and recovery link rows (T26). The worker may read which row is used or newest and update only token_hash and
 * expires_at (specs/07-nfr.md, db/worker-grants.sql); the raw token goes only into the email, never a log or event.
 */
export class LinkStore {
  constructor(private readonly pool: Pool) {}

  /** Null if the link can still be sent; else why not: used, or no longer the user's newest (created_at, then id). */
  async skipReason(table: LinkTable, id: string): Promise<LinkSkipReason | null> {
    const [rows] = await this.pool.query<RowDataPacket[]>(`SELECT user_id, used_at FROM ${table} WHERE id = ?`, [id]);
    const row = rows[0];
    if (!row) throw new MissingRecordError(`${table} row ${id} not found.`);
    if (row.used_at !== null) return 'Used';
    const [newest] = await this.pool.query<RowDataPacket[]>(
      `SELECT id FROM ${table} WHERE user_id = ? ORDER BY created_at DESC, id DESC LIMIT 1`, [row.user_id]);
    return newest[0].id === id ? null : 'Superseded';
  }

  /** Makes a new token for the row, replacing any earlier one, and returns it with its expiry (now + validFor). */
  async issueToken(table: LinkTable, id: string, now: Date, validForMs: number): Promise<{ token: string; expiresAt: Date }> {
    const token = randomBytes(32).toString('base64url');
    const expiresAt = new Date(now.getTime() + validForMs);
    await this.pool.query(`UPDATE ${table} SET token_hash = ?, expires_at = ? WHERE id = ?`, [tokenHash(token), expiresAt, id]);
    return { token, expiresAt };
  }
}

/** Lowercase hex SHA-256 of the token's UTF-8 bytes, as the API's TokenHash.Of (specs/05-data-model.md). */
export function tokenHash(token: string): string {
  return createHash('sha256').update(token, 'utf8').digest('hex');
}
