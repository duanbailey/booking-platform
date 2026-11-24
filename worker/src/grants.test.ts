import { randomUUID } from 'node:crypto';
import type { Pool } from 'mysql2/promise';
import { insertInvite, insertPasswordReset, insertUser, testPool, workerPool } from './testing/mysql';

// T26: the worker's database user (db/worker-grants.sql) enforces specs/07-nfr.md: it may update only token_hash and
// expires_at on user_invites and password_resets, cannot insert or delete them, and cannot read or write
// users.password_hash. Every other worker test connects as the same user, which proves the grants are enough.
describe('worker database grants (specs/07-nfr.md)', () => {
  let pool: Pool;
  let worker: Pool;
  beforeAll(() => {
    pool = testPool();
    worker = workerPool();
  });
  afterAll(async () => {
    await pool.end();
    await worker.end();
  });

  const denied = expect.objectContaining({ code: expect.stringMatching(/^ER_(COLUMNACCESS|TABLEACCESS)_DENIED_ERROR$/) });

  it('cannot read or write users.password_hash, or change users at all', async () => {
    const user = await insertUser(pool);

    await expect(worker.query('SELECT password_hash FROM users WHERE id = ?', [user])).rejects.toEqual(denied);
    await expect(worker.query('SELECT * FROM users WHERE id = ?', [user])).rejects.toEqual(denied);
    await expect(worker.query("UPDATE users SET password_hash = 'x' WHERE id = ?", [user])).rejects.toEqual(denied);
    await expect(worker.query("UPDATE users SET name = 'x' WHERE id = ?", [user])).rejects.toEqual(denied);
  });

  it('may update only token_hash and expires_at on invites and recovery links', async () => {
    const user = await insertUser(pool);
    const invite = await insertInvite(pool, { userId: user, createdBy: user });
    const reset = await insertPasswordReset(pool, { userId: user });

    for (const [table, id] of [['user_invites', invite], ['password_resets', reset]] as const) {
      await worker.query(`UPDATE ${table} SET token_hash = 'abc', expires_at = UTC_TIMESTAMP(6) WHERE id = ?`, [id]);
      await expect(worker.query(`UPDATE ${table} SET used_at = UTC_TIMESTAMP(6) WHERE id = ?`, [id])).rejects.toEqual(denied);
      await expect(worker.query(`UPDATE ${table} SET user_id = ? WHERE id = ?`, [randomUUID(), id])).rejects.toEqual(denied);
      await expect(worker.query(`DELETE FROM ${table} WHERE id = ?`, [id])).rejects.toEqual(denied);
      await expect(worker.query(`SELECT token_hash FROM ${table} WHERE id = ?`, [id])).rejects.toEqual(denied);
    }
    await expect(worker.query(
      'INSERT INTO user_invites (id, user_id, created_by, created_at) VALUES (?, ?, ?, UTC_TIMESTAMP(6))', [randomUUID(), user, user],
    )).rejects.toEqual(denied);
    await expect(worker.query(
      'INSERT INTO password_resets (id, user_id, created_at) VALUES (?, ?, UTC_TIMESTAMP(6))', [randomUUID(), user],
    )).rejects.toEqual(denied);
  });

  it('cannot touch tables the worker has no business with', async () => {
    await expect(worker.query('SELECT * FROM refresh_tokens LIMIT 1')).rejects.toEqual(denied);
    await expect(worker.query('SELECT * FROM outbox LIMIT 1')).rejects.toEqual(denied);
    await expect(worker.query('SELECT blocked_start_utc FROM bookings LIMIT 1')).rejects.toEqual(denied);
    await expect(worker.query('DELETE FROM notifications')).rejects.toEqual(denied);
  });
});
