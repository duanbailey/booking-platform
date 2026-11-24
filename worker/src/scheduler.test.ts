import { CreateScheduleCommand, DeleteScheduleCommand, UpdateScheduleCommand } from '@aws-sdk/client-scheduler';
import { SchedulerClient } from '@aws-sdk/client-scheduler';
import { EventBridgeReminderScheduler, type SchedulerClientLike } from './scheduler';

// T25: the EventBridge Scheduler adapter (decided 2026-11-24, plan/tasks.md): one-time at(...) schedules in UTC,
// no flexible window, deleted after running, targeting the SQS FIFO queue with the booking ID as message group;
// create-or-update on conflict; delete ignores not-found.
describe('EventBridge reminder scheduler', () => {
  const settings = {
    groupName: 'booking-reminders',
    targetArn: 'arn:aws:sqs:eu-west-2:123456789012:events.fifo',
    roleArn: 'arn:aws:iam::123456789012:role/scheduler-send',
  };
  const schedule = {
    name: 'reminder-0190a000-0000-7000-8000-000000000001-24h',
    at: new Date('2026-11-22T19:00:00.000Z'),
    messageGroupId: '0190a000-0000-7000-8000-000000000001',
    input: '{"eventType":"ReminderDue"}',
  };

  function awsError(name: string): Error {
    const error = new Error(`${name}: from the stand-in`);
    error.name = name;
    return error;
  }

  class StandIn implements SchedulerClientLike {
    readonly commands: object[] = [];
    failWith = new Map<string, Error>();

    async send(command: { constructor: { name: string } }): Promise<unknown> {
      this.commands.push(command);
      const failure = this.failWith.get(command.constructor.name);
      if (failure) throw failure;
      return {};
    }
  }

  const expectedDefinition = {
    Name: schedule.name,
    GroupName: 'booking-reminders',
    ScheduleExpression: 'at(2026-11-22T19:00:00)',
    ScheduleExpressionTimezone: 'UTC',
    FlexibleTimeWindow: { Mode: 'OFF' },
    ActionAfterCompletion: 'DELETE',
    State: 'ENABLED',
    Target: {
      Arn: settings.targetArn,
      RoleArn: settings.roleArn,
      Input: schedule.input,
      SqsParameters: { MessageGroupId: schedule.messageGroupId },
    },
  };

  it('creates a one-time schedule that sends the payload to the FIFO queue', async () => {
    const client = new StandIn();

    await new EventBridgeReminderScheduler(client, settings).upsert(schedule);

    expect(client.commands).toHaveLength(1);
    expect(client.commands[0]).toBeInstanceOf(CreateScheduleCommand);
    expect((client.commands[0] as CreateScheduleCommand).input).toEqual(expectedDefinition);
  });

  it('updates the schedule when one with the name exists (moving a reminder, or a redelivered event)', async () => {
    const client = new StandIn();
    client.failWith.set('CreateScheduleCommand', awsError('ConflictException'));

    await new EventBridgeReminderScheduler(client, settings).upsert(schedule);

    expect(client.commands.map((c) => c.constructor)).toEqual([CreateScheduleCommand, UpdateScheduleCommand]);
    expect((client.commands[1] as UpdateScheduleCommand).input).toEqual(expectedDefinition);
  });

  it('rethrows other errors, failing the message', async () => {
    const client = new StandIn();
    client.failWith.set('CreateScheduleCommand', awsError('ThrottlingException'));

    await expect(new EventBridgeReminderScheduler(client, settings).upsert(schedule)).rejects.toThrow('ThrottlingException');
  });

  it('deletes a schedule by name in the group, ignoring one that does not exist', async () => {
    const client = new StandIn();
    const scheduler = new EventBridgeReminderScheduler(client, settings);

    await scheduler.delete(schedule.name);
    client.failWith.set('DeleteScheduleCommand', awsError('ResourceNotFoundException'));
    await scheduler.delete(schedule.name);

    expect(client.commands).toHaveLength(2);
    expect(client.commands[0]).toBeInstanceOf(DeleteScheduleCommand);
    expect((client.commands[0] as DeleteScheduleCommand).input).toEqual({ Name: schedule.name, GroupName: 'booking-reminders' });
  });

  it('rethrows other delete errors', async () => {
    const client = new StandIn();
    client.failWith.set('DeleteScheduleCommand', awsError('AccessDeniedException'));

    await expect(new EventBridgeReminderScheduler(client, settings).delete(schedule.name)).rejects.toThrow('AccessDeniedException');
  });

  // ADR 0007: a small real-AWS suite, skipped unless configured (BOOKING_TEST_SCHEDULER_GROUP, _TARGET_ARN and
  // _ROLE_ARN, with AWS credentials and region in the environment). Creates, moves and deletes a schedule a year ahead, so
  // one left behind by a failed run never fires during the run's lifetime.
  const configured = process.env.BOOKING_TEST_SCHEDULER_GROUP && process.env.BOOKING_TEST_SCHEDULER_TARGET_ARN
    && process.env.BOOKING_TEST_SCHEDULER_ROLE_ARN;
  (configured ? it : it.skip)('[RealAws] creates, moves and deletes a schedule', async () => {
    const real = new EventBridgeReminderScheduler(new SchedulerClient({}), {
      groupName: process.env.BOOKING_TEST_SCHEDULER_GROUP!,
      targetArn: process.env.BOOKING_TEST_SCHEDULER_TARGET_ARN!,
      roleArn: process.env.BOOKING_TEST_SCHEDULER_ROLE_ARN!,
    });
    const name = `reminder-realaws-test-${Date.now()}`;
    const at = new Date(Date.now() + 365 * 24 * 3600_000);
    await real.upsert({ name, at, messageGroupId: 'realaws-test', input: '{}' });
    await real.upsert({ name, at: new Date(at.getTime() + 3600_000), messageGroupId: 'realaws-test', input: '{}' });
    await real.delete(name);
    await real.delete(name);
  });
});
