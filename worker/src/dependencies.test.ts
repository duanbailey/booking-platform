import { CreateScheduleCommand } from '@aws-sdk/client-scheduler';
import { SendEmailCommand } from '@aws-sdk/client-sesv2';
import type { Pool } from 'mysql2/promise';
import { createDependencies } from './dependencies';
import { processMessage } from './pipeline';
import { bookingWorld, created } from './testing/bookingWorld';
import { serverSettings, testDatabaseName, testPool } from './testing/mysql';

// T25 review: the Lambda entry point's wiring. The dependencies built from the environment must send email from the
// configured address and create reminder schedules in the configured group, targeting the configured queue with the
// configured role, through the same handlers the registry test covers.
describe('production dependencies', () => {
  let pool: Pool;
  beforeAll(() => {
    pool = testPool();
  });
  afterAll(async () => {
    await pool.end();
  });

  it('wires the configured SES From address and Scheduler settings into the handlers', async () => {
    const w = await bookingWorld(pool); // test data only; its own fakes are not used here
    const server = serverSettings();
    const env = {
      DB_HOST: server.host, DB_PORT: String(server.port), DB_USER: server.user, DB_PASSWORD: server.password,
      DB_NAME: testDatabaseName(), EMAIL_FROM: 'bookings@example.test',
      REMINDER_GROUP: 'booking-reminders', REMINDER_TARGET_ARN: 'arn:aws:sqs:eu-west-2:123456789012:events.fifo',
      REMINDER_ROLE_ARN: 'arn:aws:iam::123456789012:role/scheduler-send',
    };
    const sesCommands: SendEmailCommand[] = [];
    const schedulerCommands: object[] = [];
    const deps = createDependencies(env, {
      ses: { send: async (command) => { sesCommands.push(command); return { MessageId: 'ses-1' }; } },
      scheduler: { send: async (command) => { schedulerCommands.push(command); return {}; } },
    });

    try {
      // A booking far enough ahead that the real clock schedules both reminders.
      await processMessage(JSON.stringify(created(w, { startUtc: '2027-01-11T15:00:00Z', endUtc: '2027-01-11T16:00:00Z' })), deps);
    } finally {
      await deps.close();
    }

    expect(sesCommands).toHaveLength(1);
    expect(sesCommands[0].input.FromEmailAddress).toBe('bookings@example.test');
    expect(schedulerCommands).toHaveLength(2);
    for (const command of schedulerCommands) {
      expect(command).toBeInstanceOf(CreateScheduleCommand);
      expect((command as CreateScheduleCommand).input).toMatchObject({
        GroupName: 'booking-reminders',
        Target: {
          Arn: 'arn:aws:sqs:eu-west-2:123456789012:events.fifo',
          RoleArn: 'arn:aws:iam::123456789012:role/scheduler-send',
          SqsParameters: { MessageGroupId: w.bookingId },
        },
      });
    }
  });
});
