import { CreateScheduleCommand } from '@aws-sdk/client-scheduler';
import { GetSecretValueCommand } from '@aws-sdk/client-secrets-manager';
import { SendEmailCommand } from '@aws-sdk/client-sesv2';
import type { Pool } from 'mysql2/promise';
import { createDependencies } from './dependencies';
import { processMessage } from './pipeline';
import { bookingWorld, created } from './testing/bookingWorld';
import { serverSettings, testDatabaseName, testPool, workerCredentials } from './testing/mysql';

// T25 review: the Lambda entry point's wiring. The dependencies built from the environment must send email from the
// configured address and create reminder schedules in the configured group, targeting the configured queue with the
// configured role, through the same handlers the registry test covers. T27b: the database password can come from
// Secrets Manager, and schedules carry the dead-letter queue.
describe('production dependencies', () => {
  let pool: Pool;
  beforeAll(() => {
    pool = testPool();
  });
  afterAll(async () => {
    await pool.end();
  });

  function environment(password: { DB_PASSWORD?: string; DB_PASSWORD_SECRET_ARN?: string }) {
    const server = serverSettings();
    const limited = workerCredentials(); // T26: the production wiring runs as the limited user
    return {
      DB_HOST: server.host, DB_PORT: String(server.port), DB_USER: limited.user, ...password,
      DB_NAME: testDatabaseName(), EMAIL_FROM: 'bookings@example.test',
      REMINDER_GROUP: 'booking-reminders', REMINDER_TARGET_ARN: 'arn:aws:sqs:eu-west-2:123456789012:events.fifo',
      REMINDER_ROLE_ARN: 'arn:aws:iam::123456789012:role/scheduler-send',
      REMINDER_DLQ_ARN: 'arn:aws:sqs:eu-west-2:123456789012:reminders-dlq',
      APP_BASE_URL: 'https://app.example.test', APP_NAME: 'Bookly',
    };
  }

  function standIns() {
    const sesCommands: SendEmailCommand[] = [];
    const schedulerCommands: object[] = [];
    const secretRequests: GetSecretValueCommand[] = [];
    return {
      sesCommands, schedulerCommands, secretRequests,
      clients: {
        ses: { send: async (command: SendEmailCommand) => { sesCommands.push(command); return { MessageId: 'ses-1' }; } },
        scheduler: { send: async (command: object) => { schedulerCommands.push(command); return {}; } },
        secrets: {
          send: async (command: GetSecretValueCommand) => {
            secretRequests.push(command);
            return { SecretString: workerCredentials().password };
          },
        },
      },
    };
  }

  it('wires the configured SES From address and Scheduler settings into the handlers', async () => {
    const w = await bookingWorld({ pool, worker: pool }); // test data only; its own handlers and fakes are not used here
    const { sesCommands, schedulerCommands, secretRequests, clients } = standIns();
    const deps = await createDependencies(environment({ DB_PASSWORD: workerCredentials().password }), clients);

    try {
      // A booking far enough ahead that the real clock schedules both reminders.
      await processMessage(JSON.stringify(created(w, { startUtc: '2027-01-11T15:00:00Z', endUtc: '2027-01-11T16:00:00Z' })), deps);
    } finally {
      await deps.close();
    }

    expect(secretRequests).toHaveLength(0); // DB_PASSWORD given: no Secrets Manager call
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
          DeadLetterConfig: { Arn: 'arn:aws:sqs:eu-west-2:123456789012:reminders-dlq' },
        },
      });
    }
  });

  it('fetches the database password from Secrets Manager once and connects with it (T27b)', async () => {
    const w = await bookingWorld({ pool, worker: pool });
    const arn = 'arn:aws:secretsmanager:us-east-2:123456789012:secret:worker-db';
    const { sesCommands, secretRequests, clients } = standIns();
    const deps = await createDependencies(environment({ DB_PASSWORD_SECRET_ARN: arn }), clients);

    try {
      await processMessage(JSON.stringify(created(w, { startUtc: '2027-01-11T15:00:00Z', endUtc: '2027-01-11T16:00:00Z' })), deps);
    } finally {
      await deps.close();
    }

    expect(secretRequests.map((r) => r.input.SecretId)).toEqual([arn]);
    expect(sesCommands).toHaveLength(1); // the handler reached MySQL with the fetched password
  });

  it('fails clearly when the secret has no string value (T27b)', async () => {
    const { clients } = standIns();
    clients.secrets.send = async () => ({ SecretString: undefined as unknown as string });

    await expect(createDependencies(environment({ DB_PASSWORD_SECRET_ARN: 'arn:x' }), clients))
      .rejects.toThrow('DB_PASSWORD_SECRET_ARN');
  });
});
