import { randomUUID } from 'node:crypto';
import type { Pool, RowDataPacket } from 'mysql2/promise';

/** notifications.kind (specs/05-data-model.md). */
export type NotificationKind =
  | 'Invite' | 'AccessGranted' | 'PasswordReset' | 'PasswordChanged' | 'Confirmation' | 'Rescheduled' | 'Cancelled'
  | 'LocalTimeChanged' | 'RebookedInsideWindow' | 'Reminder24h' | 'Reminder1h';

export type NotificationStatus = 'Pending' | 'Sent' | 'Failed';

export interface NotificationRow {
  id: string;
  status: NotificationStatus;
}

/** The longest error kept on a row; the column is TEXT, but an SES error never needs more. */
export const MAX_ERROR_LENGTH = 2000;

/**
 * The notifications table, which makes sending idempotent per event and recipient (specs/05-data-model.md): the
 * unique (event_id, recipient_user_id) key means one row per email, whatever the number of deliveries.
 */
export class NotificationStore {
  constructor(private readonly pool: Pool, private readonly now: () => Date = () => new Date()) {}

  /** Inserts a Pending row, or finds the existing one (any status) for this event and recipient. */
  async claim(eventId: string, recipientUserId: string, kind: NotificationKind): Promise<NotificationRow> {
    await this.pool.query(
      `INSERT INTO notifications (id, event_id, recipient_user_id, kind, status, created_at)
       VALUES (?, ?, ?, ?, 'Pending', ?) ON DUPLICATE KEY UPDATE id = id`,
      [randomUUID(), eventId, recipientUserId, kind, this.now()],
    );
    const [rows] = await this.pool.query<(RowDataPacket & NotificationRow)[]>(
      'SELECT id, status FROM notifications WHERE event_id = ? AND recipient_user_id = ?', [eventId, recipientUserId]);
    return { id: rows[0].id, status: rows[0].status };
  }

  async markSent(id: string, sesMessageId: string): Promise<void> {
    await this.pool.query(
      "UPDATE notifications SET status = 'Sent', ses_message_id = ?, sent_at = ?, error = NULL WHERE id = ?",
      [sesMessageId, this.now(), id]);
  }

  async markFailed(id: string, error: string): Promise<void> {
    await this.pool.query("UPDATE notifications SET status = 'Failed', error = ? WHERE id = ?", [error.slice(0, MAX_ERROR_LENGTH), id]);
  }
}
