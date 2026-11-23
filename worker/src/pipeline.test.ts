import type { Pool, RowDataPacket } from 'mysql2/promise';
import { ContactDirectory } from './contacts';
import { createEventParser } from './events';
import { NotificationStore } from './notifications';
import { NoHandlerError, processMessage, type EventHandler, type PipelineDeps } from './pipeline';
import { CapturingLogger, FakeEmailSender, passwordChanged } from './testing/fakes';
import { insertUser, notificationsFor, testPool } from './testing/mysql';

// T23: the pipeline against MySQL. Done when: a duplicate event sends each recipient at most one email. Decisions
// 2026-11-23 (plan/tasks.md, T23): per recipient a Pending notifications row is inserted or found (Sent: skip;
// Pending or Failed: send); contact details are read just before sending; a send error marks the row Failed and
// fails the message, so SQS redelivers it. T23 ships no production handler, so these tests register one that emails
// a PasswordChanged event's user and a second, fixed user.
describe('pipeline', () => {
  let pool: Pool;
  beforeAll(() => {
    pool = testPool();
  });
  afterAll(async () => {
    await pool.end();
  });

  async function setup(handlers?: Map<string, EventHandler>) {
    const email = new FakeEmailSender();
    const log = new CapturingLogger();
    const user = await insertUser(pool, { name: 'Ann Example', email: `ann-${Date.now()}-${Math.random()}@example.test` });
    const other = await insertUser(pool, { name: 'Bo Other' });
    const testHandler: EventHandler = async (ctx) => {
      for (const recipient of [ctx.event.data.userId as string, other]) {
        await ctx.sendEmail(recipient, 'PasswordChanged', (contact) => ({
          subject: 'Your password was changed',
          text: `Hello ${contact.name}, in ${contact.timeZone}`,
        }));
      }
    };
    const deps: PipelineDeps = {
      parse: createEventParser(),
      handlers: handlers ?? new Map([['PasswordChanged.v1', testHandler]]),
      notifications: new NotificationStore(pool),
      contacts: new ContactDirectory(pool),
      email,
      log,
    };
    return { deps, email, log, user, other };
  }

  it('sends each recipient one email and records it as Sent', async () => {
    const { deps, email, user, other } = await setup();
    const event = passwordChanged({ userId: user });

    await processMessage(JSON.stringify(event), deps);

    expect(email.sent.map((e) => e.subject)).toEqual(['Your password was changed', 'Your password was changed']);
    expect(email.sent[0].text).toBe('Hello Ann Example, in Europe/London');
    const rows = await notificationsFor(pool, event.eventId);
    expect(rows.map((r) => r.recipient_user_id).sort()).toEqual([user, other].sort());
    for (const row of rows) {
      expect(row.status).toBe('Sent');
      expect(row.kind).toBe('PasswordChanged');
      expect(email.sent.map((e) => e.messageId)).toContain(row.ses_message_id);
      expect(row.sent_at).toBeInstanceOf(Date);
      expect(row.error).toBeNull();
    }
  });

  it('a duplicate event sends each recipient at most one email (T23 done when)', async () => {
    const { deps, email, user } = await setup();
    const body = JSON.stringify(passwordChanged({ userId: user }));

    await processMessage(body, deps);
    await processMessage(body, deps);
    await processMessage(body, deps);

    expect(email.sent).toHaveLength(2);
  });

  it('after a failed send the retry sends only the missing email', async () => {
    const { deps, email, user, other } = await setup();
    const event = passwordChanged({ userId: user });
    const otherEmail = (await new ContactDirectory(pool).find(other))!.email;
    email.failFor.add(otherEmail);

    await expect(processMessage(JSON.stringify(event), deps)).rejects.toThrow('MessageRejected');

    let rows = await notificationsFor(pool, event.eventId);
    const failed = rows.find((r) => r.recipient_user_id === other)!;
    expect(failed.status).toBe('Failed');
    expect(failed.error).toContain('MessageRejected');
    expect(failed.ses_message_id).toBeNull();
    expect(rows.find((r) => r.recipient_user_id === user)!.status).toBe('Sent');

    email.failFor.clear();
    await processMessage(JSON.stringify(event), deps);

    expect(email.sent.map((e) => e.to)).toEqual([(await new ContactDirectory(pool).find(user))!.email, otherEmail]);
    rows = await notificationsFor(pool, event.eventId);
    expect(rows.every((r) => r.status === 'Sent')).toBe(true);
    expect(rows.find((r) => r.recipient_user_id === other)!.error).toBeNull();
  });

  it('a Pending row (a crash before marking it) is sent again', async () => {
    const { deps, email, user } = await setup();
    const event = passwordChanged({ userId: user });
    await new NotificationStore(pool).claim(event.eventId, user, 'PasswordChanged');

    await processMessage(JSON.stringify(event), deps);

    expect(email.sent).toHaveLength(2);
  });

  it('contact details are read when sending, not taken from the event', async () => {
    const { deps, email, user } = await setup();
    await pool.query('UPDATE users SET email = ?, name = ?, time_zone = ? WHERE id = ?', ['new@example.test', 'Ann Renamed', 'Asia/Tokyo', user]);

    await processMessage(JSON.stringify(passwordChanged({ userId: user })), deps);

    expect(email.sent[0]).toMatchObject({ to: 'new@example.test', text: 'Hello Ann Renamed, in Asia/Tokyo' });
  });

  it('a known event type without a handler yet fails, so it goes to the dead-letter queue', async () => {
    const { deps, email } = await setup(new Map());

    await expect(processMessage(JSON.stringify(passwordChanged()), deps)).rejects.toThrow(NoHandlerError);
    expect(email.sent).toHaveLength(0);
  });

  it('a failed send logs no email address, even when the error contains one', async () => {
    // T23 review: SES errors can name the recipient ("not verified: ann@..."); logs carry no personal data.
    const { deps, email, log, other } = await setup();
    const otherEmail = (await new ContactDirectory(pool).find(other))!.email;
    email.failFor.add(otherEmail);

    await expect(processMessage(JSON.stringify(passwordChanged({ userId: other })), deps)).rejects.toThrow();

    const failure = log.entries.find((e) => e.level === 'error')!;
    expect(failure.error).toContain('MessageRejected');
    expect(JSON.stringify(log.entries)).not.toContain('@');
  });

  it('a failure to record Sent leaves the row Pending, so it is not recorded as a failed send', async () => {
    // T23 review: SES accepted it; only the bookkeeping failed. The retry sends again, as the spec accepts.
    const { deps, email, user } = await setup();
    const event = passwordChanged({ userId: user });
    const store = new NotificationStore(pool);
    let failNext = true;
    deps.notifications = {
      claim: (...args) => store.claim(...args),
      markFailed: (...args) => store.markFailed(...args),
      markSent: async (...args) => {
        if (failNext) { failNext = false; throw new Error('Connection lost'); }
        return store.markSent(...args);
      },
    };

    await expect(processMessage(JSON.stringify(event), deps)).rejects.toThrow('Connection lost');

    const row = (await notificationsFor(pool, event.eventId)).find((r) => r.recipient_user_id === user)!;
    expect(row.status).toBe('Pending');
    expect(row.error).toBeNull();

    await processMessage(JSON.stringify(event), deps);
    expect(email.sent).toHaveLength(3); // the first recipient twice (sent, unrecorded, sent again), the other once
  });

  it('stores its timestamps in UTC', async () => {
    const { deps, user } = await setup();
    const event = passwordChanged({ userId: user });

    await processMessage(JSON.stringify(event), deps);

    const [rows] = await pool.query<RowDataPacket[]>(
      'SELECT TIMESTAMPDIFF(SECOND, created_at, UTC_TIMESTAMP(6)) AS created, TIMESTAMPDIFF(SECOND, sent_at, UTC_TIMESTAMP(6)) AS sent FROM notifications WHERE event_id = ?',
      [event.eventId]);
    for (const row of rows) {
      expect(Math.abs(row.created)).toBeLessThan(60);
      expect(Math.abs(row.sent)).toBeLessThan(60);
    }
  });

  it('logs with the event ID, type and correlation ID', async () => {
    const { deps, log, user } = await setup();
    const event = passwordChanged({ userId: user, correlationId: 'req-77' });

    await processMessage(JSON.stringify(event), deps);

    expect(log.entries.length).toBeGreaterThan(0);
    for (const entry of log.entries) {
      expect(entry).toMatchObject({ eventId: event.eventId, eventType: 'PasswordChanged', correlationId: 'req-77' });
    }
    expect(JSON.stringify(log.entries)).not.toContain('@example.test'); // no email addresses in logs
  });
});
