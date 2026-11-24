import { createHash, randomUUID } from 'node:crypto';
import type { Pool, RowDataPacket } from 'mysql2/promise';
import { MissingRecordError } from './directory';
import { processMessage } from './pipeline';
import { bookingWorld, envelope, type BookingWorld } from './testing/bookingWorld';
import { insertInvite, insertPasswordReset, insertUser, notificationsFor, testPool, workerPool } from './testing/mysql';

// T26: invite and account emails. Acceptance: worker handling table in specs/06-events.md; invite and recovery stories
// (end to end with T7 and T7a). Decisions 2026-11-24 (plan/tasks.md, T26): tokens are 32 random bytes, base64url,
// stored as lowercase hex SHA-256 with an expiry 7 days (invite) or 1 hour (recovery) ahead; the token is made after
// the notifications row is claimed and only if it is not Sent, so a retry makes a new one; a used or superseded link
// is skipped before claiming; links are APP_BASE_URL/accept-invite?token= and /reset-password?token=; APP_NAME names
// the product. Fixtures: testing/bookingWorld.ts (clock 2026-11-20T10:00Z, app "Bookly" at https://app.example.test).
describe('invite and account emails', () => {
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

  const world = () => bookingWorld({ pool, worker });
  const run = (w: BookingWorld, event: object) => processMessage(JSON.stringify(event), w.deps);
  const sha256 = (token: string) => createHash('sha256').update(token, 'utf8').digest('hex');

  async function linkRow(table: 'user_invites' | 'password_resets', id: string) {
    const [rows] = await pool.query<RowDataPacket[]>(`SELECT token_hash, expires_at FROM ${table} WHERE id = ?`, [id]);
    return rows[0] as { token_hash: string | null; expires_at: Date | null };
  }

  function tokenIn(text: string, path: 'accept-invite' | 'reset-password'): string {
    const match = new RegExp(`https://app\\.example\\.test/${path}\\?token=([A-Za-z0-9_-]+)\\n`).exec(text);
    if (!match) throw new Error(`No ${path} link in:\n${text}`);
    return match[1];
  }

  const accessGranted = (w: BookingWorld, fields: { userId: string; role: 'Owner' | 'Staff'; inviteId: string | null }) =>
    envelope('AccessGranted', { userId: fields.userId, businessId: w.businessId, role: fields.role, inviteId: fields.inviteId, grantedByUserId: w.ownerUser });

  /** A user with no password yet, in New York, and an invite for them. */
  async function invitee(w: BookingWorld, fields: { createdAt?: string; usedAt?: string; id?: string } = {}) {
    const userId = await insertUser(pool, { name: 'Ivy Invitee', email: `ivy-${w.tag}@example.test`, timeZone: 'America/New_York' });
    const inviteId = await insertInvite(pool, { userId, createdBy: w.ownerUser, ...fields });
    return { userId, inviteId };
  }

  describe('AccessGranted with an invite', () => {
    it('emails an owner a set-password link, storing only the token\'s hash and a 7-day expiry', async () => {
      const w = await world();
      const { userId, inviteId } = await invitee(w);
      const event = accessGranted(w, { userId, role: 'Owner', inviteId });

      await run(w, event);

      expect(w.email.sent).toHaveLength(1);
      const [mail] = w.email.sent;
      expect(mail.to).toBe(`ivy-${w.tag}@example.test`);
      expect(mail.subject).toBe('Bookly: set your password to manage Cut & Dry');
      const token = tokenIn(mail.text, 'accept-invite');
      expect(token).toMatch(/^[A-Za-z0-9_-]{43}$/); // 32 random bytes, base64url without padding
      expect(mail.text).toBe([
        'Hello Ivy Invitee,',
        '',
        'You are now the owner of Cut & Dry on Bookly. Set your password to get started:',
        '',
        `https://app.example.test/accept-invite?token=${token}`,
        '',
        'This link works once and expires on Friday 27 November 2026, 05:00 America/New_York (UTC-05:00).',
        'If it has expired, ask for a new invite.',
        '',
      ].join('\n'));
      const row = await linkRow('user_invites', inviteId);
      expect(row.token_hash).toBe(sha256(token));
      expect(row.expires_at!.toISOString()).toBe('2026-11-27T10:00:00.000Z');
      const notes = await notificationsFor(pool, event.eventId);
      expect(notes.map((n) => [n.recipient_user_id, n.kind, n.status])).toEqual([[userId, 'Invite', 'Sent']]);
    });

    it('uses staff wording for a staff invite', async () => {
      const w = await world();
      const { userId, inviteId } = await invitee(w);

      await run(w, accessGranted(w, { userId, role: 'Staff', inviteId }));

      expect(w.email.sent[0].subject).toBe('Bookly: set your password to join Cut & Dry');
      expect(w.email.sent[0].text).toContain('You have been added as staff at Cut & Dry on Bookly. Set your password to get started:');
    });

    it('a redelivered event after the email was sent changes nothing, so the emailed link keeps working', async () => {
      const w = await world();
      const { userId, inviteId } = await invitee(w);
      const body = JSON.stringify(accessGranted(w, { userId, role: 'Staff', inviteId }));

      await processMessage(body, w.deps);
      const first = await linkRow('user_invites', inviteId);
      await processMessage(body, w.deps);

      expect(w.email.sent).toHaveLength(1);
      expect(await linkRow('user_invites', inviteId)).toEqual(first);
    });

    it('a retry after a failed send makes a new token, so only the latest link works', async () => {
      const w = await world();
      const { userId, inviteId } = await invitee(w);
      const body = JSON.stringify(accessGranted(w, { userId, role: 'Staff', inviteId }));
      w.email.failFor.add(`ivy-${w.tag}@example.test`);

      await expect(processMessage(body, w.deps)).rejects.toThrow('MessageRejected');
      const failedHash = (await linkRow('user_invites', inviteId)).token_hash;
      w.email.failFor.clear();
      await processMessage(body, w.deps);

      const token = tokenIn(w.email.sent[0].text, 'accept-invite');
      const row = await linkRow('user_invites', inviteId);
      expect(failedHash).not.toBeNull();
      expect(row.token_hash).toBe(sha256(token));
      expect(row.token_hash).not.toBe(failedHash);
    });

    it('never logs the token or its hash', async () => {
      const w = await world();
      const { userId, inviteId } = await invitee(w);

      await run(w, accessGranted(w, { userId, role: 'Owner', inviteId }));

      const token = tokenIn(w.email.sent[0].text, 'accept-invite');
      const logged = JSON.stringify(w.log.entries);
      expect(logged).not.toContain(token);
      expect(logged).not.toContain(sha256(token));
    });

    describe('skips an invite whose link could never work, with no token, email or notifications row', () => {
      async function expectSkipped(w: BookingWorld, event: { eventId: string }, inviteId: string, reason: string) {
        await run(w, event);
        expect(w.email.sent).toHaveLength(0);
        expect((await linkRow('user_invites', inviteId)).token_hash).toBeNull();
        expect(await notificationsFor(pool, event.eventId)).toHaveLength(0);
        expect(w.log.entries).toContainEqual(expect.objectContaining({ level: 'info', message: 'Link skipped', reason, linkId: inviteId }));
      }

      it('when it is used', async () => {
        const w = await world();
        const { userId, inviteId } = await invitee(w, { usedAt: '2026-11-20T09:30:00Z' });

        await expectSkipped(w, accessGranted(w, { userId, role: 'Staff', inviteId }), inviteId, 'Used');
      });

      it('when the user has a newer invite (by created_at)', async () => {
        const w = await world();
        const { userId, inviteId } = await invitee(w, { createdAt: '2026-11-20T09:00:00Z' });
        await insertInvite(pool, { userId, createdBy: w.ownerUser, createdAt: '2026-11-20T09:05:00Z' });

        await expectSkipped(w, accessGranted(w, { userId, role: 'Staff', inviteId }), inviteId, 'Superseded');
      });

      it('when a newer invite has the same created_at and a greater id', async () => {
        const w = await world();
        const { userId, inviteId } = await invitee(w, { id: `00000000-0000-4000-8000-${w.tag}0000` });
        await insertInvite(pool, { userId, createdBy: w.ownerUser, id: `ffffffff-ffff-4fff-8fff-${w.tag}ffff` });

        await expectSkipped(w, accessGranted(w, { userId, role: 'Staff', inviteId }), inviteId, 'Superseded');
      });
    });

    it('sends the newest invite when an older one has the same created_at and a smaller id', async () => {
      const w = await world();
      const { userId } = await invitee(w, { id: `00000000-0000-4000-8000-${w.tag}0000` });
      const newest = await insertInvite(pool, { userId, createdBy: w.ownerUser, id: `ffffffff-ffff-4fff-8fff-${w.tag}ffff` });

      await run(w, accessGranted(w, { userId, role: 'Staff', inviteId: newest }));

      expect(w.email.sent).toHaveLength(1);
    });

    it('fails the message if the invite row does not exist', async () => {
      const w = await world();
      const { userId } = await invitee(w);

      await expect(run(w, accessGranted(w, { userId, role: 'Staff', inviteId: randomUUID() }))).rejects.toBeInstanceOf(MissingRecordError);
      expect(w.email.sent).toHaveLength(0);
    });
  });

  describe('AccessGranted without an invite', () => {
    it('tells an existing user they now own the business', async () => {
      const w = await world();
      const event = accessGranted(w, { userId: w.kimUser, role: 'Owner', inviteId: null });

      await run(w, event);

      expect(w.email.sent).toHaveLength(1);
      expect(w.email.sent[0].subject).toBe('Bookly: you now own Cut & Dry');
      expect(w.email.sent[0].text).toBe([
        'Hello Kim Staff,',
        '',
        'You are now the owner of Cut & Dry on Bookly. Log in with your existing account:',
        '',
        'https://app.example.test',
        '',
      ].join('\n'));
      const notes = await notificationsFor(pool, event.eventId);
      expect(notes.map((n) => n.kind)).toEqual(['AccessGranted']);
    });

    it('tells an existing user they were added as staff', async () => {
      const w = await world();

      await run(w, accessGranted(w, { userId: w.kimUser, role: 'Staff', inviteId: null }));

      expect(w.email.sent[0].subject).toBe('Bookly: you have been added as staff at Cut & Dry');
      expect(w.email.sent[0].text).toContain('You have been added as staff at Cut & Dry on Bookly. Log in with your existing account:');
    });
  });

  describe('PasswordResetRequested', () => {
    const resetRequested = (userId: string, passwordResetId: string) => envelope('PasswordResetRequested', { userId, passwordResetId });

    it('emails a recovery link, storing only the token\'s hash and a 1-hour expiry', async () => {
      const w = await world();
      const resetId = await insertPasswordReset(pool, { userId: w.customer });
      const event = resetRequested(w.customer, resetId);

      await run(w, event);

      const [mail] = w.email.sent;
      expect(mail.subject).toBe('Bookly: reset your password');
      const token = tokenIn(mail.text, 'reset-password');
      expect(mail.text).toBe([
        'Hello Ann Customer,',
        '',
        'Someone asked to reset the password for your Bookly account. To choose a new password, follow this link:',
        '',
        `https://app.example.test/reset-password?token=${token}`,
        '',
        'This link works once and expires on Friday 20 November 2026, 11:00 Europe/London (UTC+00:00).',
        'If you did not ask for this, ignore this email; your password stays the same.',
        '',
      ].join('\n'));
      const row = await linkRow('password_resets', resetId);
      expect(row.token_hash).toBe(sha256(token));
      expect(row.expires_at!.toISOString()).toBe('2026-11-20T11:00:00.000Z');
      const notes = await notificationsFor(pool, event.eventId);
      expect(notes.map((n) => [n.kind, n.status])).toEqual([['PasswordReset', 'Sent']]);
      expect(JSON.stringify(w.log.entries)).not.toContain(token);
    });

    it('a retry after a failed send makes a new token', async () => {
      const w = await world();
      const resetId = await insertPasswordReset(pool, { userId: w.customer });
      const body = JSON.stringify(resetRequested(w.customer, resetId));
      w.email.failFor.add(await w.addressOf(w.customer));

      await expect(processMessage(body, w.deps)).rejects.toThrow('MessageRejected');
      const failedHash = (await linkRow('password_resets', resetId)).token_hash;
      w.email.failFor.clear();
      await processMessage(body, w.deps);

      const row = await linkRow('password_resets', resetId);
      expect(row.token_hash).toBe(sha256(tokenIn(w.email.sent[0].text, 'reset-password')));
      expect(row.token_hash).not.toBe(failedHash);
    });

    it.each([
      ['Used', { usedAt: '2026-11-20T09:30:00Z' }, false],
      ['Superseded', {}, true],
    ] as const)('skips a link that is %s (decided 2026-11-24)', async (reason, fields, addNewer) => {
      const w = await world();
      const resetId = await insertPasswordReset(pool, { userId: w.customer, createdAt: '2026-11-20T09:00:00Z', ...fields });
      if (addNewer) await insertPasswordReset(pool, { userId: w.customer, createdAt: '2026-11-20T09:06:00Z' });
      const event = resetRequested(w.customer, resetId);

      await run(w, event);

      expect(w.email.sent).toHaveLength(0);
      expect((await linkRow('password_resets', resetId)).token_hash).toBeNull();
      expect(await notificationsFor(pool, event.eventId)).toHaveLength(0);
      expect(w.log.entries).toContainEqual(expect.objectContaining({ message: 'Link skipped', reason, linkId: resetId }));
    });
  });

  describe('PasswordChanged', () => {
    it.each([
      ['Changed', 'while you were logged in.'],
      ['Reset', 'through a password recovery link.'],
    ])('says when and how (%s), and where to go if it was not them', async (method, how) => {
      const w = await world();
      const event = envelope('PasswordChanged', { userId: w.customer, method });

      await run(w, event);

      const [mail] = w.email.sent;
      expect(mail.subject).toBe('Bookly: your password was changed');
      expect(mail.text).toBe([
        'Hello Ann Customer,',
        '',
        `Your Bookly password was changed on Friday 20 November 2026, 10:00 Europe/London (UTC+00:00), ${how}`,
        '',
        'If this was not you, reset your password at https://app.example.test/forgot-password',
        '',
      ].join('\n'));
      const notes = await notificationsFor(pool, event.eventId);
      expect(notes.map((n) => n.kind)).toEqual(['PasswordChanged']);
    });
  });
});
