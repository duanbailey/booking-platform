import { SendEmailCommand, SESv2Client } from '@aws-sdk/client-sesv2';
import { SesEmailSender } from './email';
import { loadConfig } from './config';

// T23: the SES adapter behind the EmailSender interface (ADR 0007). The From address comes from config.
describe('SES email sender', () => {
  it('sends a simple text and HTML email from the configured address and returns the message ID', async () => {
    const commands: SendEmailCommand[] = [];
    const client = { send: async (command: SendEmailCommand) => { commands.push(command); return { MessageId: 'ses-1', $metadata: {} }; } };
    const sender = new SesEmailSender(client, 'Bookings <bookings@example.test>');

    const id = await sender.send({ to: 'ann@example.test', subject: 'Hi', text: 'Plain', html: '<p>Rich</p>' });

    expect(id).toBe('ses-1');
    expect(commands[0].input).toEqual({
      FromEmailAddress: 'Bookings <bookings@example.test>',
      Destination: { ToAddresses: ['ann@example.test'] },
      Content: {
        Simple: {
          Subject: { Data: 'Hi', Charset: 'UTF-8' },
          Body: { Text: { Data: 'Plain', Charset: 'UTF-8' }, Html: { Data: '<p>Rich</p>', Charset: 'UTF-8' } },
        },
      },
    });
  });

  it('leaves out the HTML part when there is none', async () => {
    const commands: SendEmailCommand[] = [];
    const sender = new SesEmailSender({ send: async (c: SendEmailCommand) => { commands.push(c); return { MessageId: 'x', $metadata: {} }; } }, 'a@example.test');

    await sender.send({ to: 'b@example.test', subject: 's', text: 't' });

    expect(commands[0].input.Content?.Simple?.Body).toEqual({ Text: { Data: 't', Charset: 'UTF-8' } });
  });

  it('fails when SES returns no message ID', async () => {
    const sender = new SesEmailSender({ send: async () => ({ $metadata: {} }) }, 'a@example.test');

    await expect(sender.send({ to: 'b@example.test', subject: 's', text: 't' })).rejects.toThrow('no MessageId');
  });

  // ADR 0007: a small real-AWS suite, skipped unless configured (run with BOOKING_TEST_SES_FROM and
  // BOOKING_TEST_SES_TO set to verified sandbox addresses and AWS credentials in the environment).
  const realSes = process.env.BOOKING_TEST_SES_FROM && process.env.BOOKING_TEST_SES_TO ? it : it.skip;
  realSes('[RealAws] sends through SES', async () => {
    const sender = new SesEmailSender(new SESv2Client({}), process.env.BOOKING_TEST_SES_FROM!);
    const id = await sender.send({ to: process.env.BOOKING_TEST_SES_TO!, subject: 'Booking platform real-AWS test', text: 'Test email (ADR 0007).' });
    expect(id).toBeTruthy();
  });
});

describe('config', () => {
  const complete = {
    DB_HOST: 'db', DB_PORT: '3306', DB_USER: 'worker', DB_PASSWORD: 'pw', DB_NAME: 'booking', EMAIL_FROM: 'bookings@example.test',
    REMINDER_GROUP: 'booking-reminders', REMINDER_TARGET_ARN: 'arn:aws:sqs:eu-west-2:123456789012:events.fifo',
    REMINDER_ROLE_ARN: 'arn:aws:iam::123456789012:role/scheduler-send',
  };

  it('reads the database, From address and reminder settings from the environment (T25)', () => {
    expect(loadConfig(complete)).toEqual({
      database: { host: 'db', port: 3306, user: 'worker', password: 'pw', database: 'booking' },
      emailFrom: 'bookings@example.test',
      reminders: {
        groupName: 'booking-reminders',
        targetArn: 'arn:aws:sqs:eu-west-2:123456789012:events.fifo',
        roleArn: 'arn:aws:iam::123456789012:role/scheduler-send',
      },
    });
  });

  it('requires the reminder settings (T25)', () => {
    expect(() => loadConfig({ ...complete, REMINDER_GROUP: undefined, REMINDER_ROLE_ARN: '' })).toThrow('REMINDER_GROUP, REMINDER_ROLE_ARN');
  });

  it('names every missing setting, but never a value', () => {
    expect(() => loadConfig({ ...complete, DB_HOST: undefined, EMAIL_FROM: '' })).toThrow('DB_HOST, EMAIL_FROM');
    expect(() => loadConfig({ ...complete, DB_HOST: undefined })).toThrow(expect.objectContaining({ message: expect.not.stringContaining('pw') }));
  });

  it('rejects a port that is not a number', () => {
    expect(() => loadConfig({ ...complete, DB_PORT: 'abc' })).toThrow('DB_PORT');
  });
});
