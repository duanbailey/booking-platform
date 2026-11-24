import {
  CreateScheduleCommand, DeleteScheduleCommand, UpdateScheduleCommand, type CreateScheduleCommandInput,
} from '@aws-sdk/client-scheduler';

/** One reminder schedule: when it fires, and the ReminderDue body it sends to the queue in the booking's group. */
export interface ReminderSchedule {
  name: string;
  at: Date;
  messageGroupId: string;
  input: string;
}

/** Creates, moves and deletes reminder schedules by name, idempotently (ADR 0007: EventBridge Scheduler, a fake in tests). */
export interface ReminderScheduler {
  /** Creates the schedule, or replaces the one with this name. */
  upsert(schedule: ReminderSchedule): Promise<void>;
  /** Deletes the schedule; one that does not exist (never made, already run, or already deleted) is fine. */
  delete(name: string): Promise<void>;
}

/** The part of SchedulerClient this adapter uses, so tests can pass a stand-in. */
export interface SchedulerClientLike {
  send(command: CreateScheduleCommand | UpdateScheduleCommand | DeleteScheduleCommand): Promise<unknown>;
}

export interface SchedulerSettings {
  groupName: string;
  /** The SQS FIFO queue ReminderDue is sent to (decided 2026-11-24). */
  targetArn: string;
  /** The role Scheduler assumes to send to the queue. */
  roleArn: string;
}

/**
 * EventBridge Scheduler (T25, decided 2026-11-24): one-time at(...) schedules in UTC, no flexible window, deleted by
 * Scheduler after running, sending the payload to the FIFO queue with the booking ID as message group. Scheduler
 * cannot set a deduplication ID, so the queue uses content-based deduplication (T27).
 */
export class EventBridgeReminderScheduler implements ReminderScheduler {
  constructor(private readonly client: SchedulerClientLike, private readonly settings: SchedulerSettings) {}

  async upsert(schedule: ReminderSchedule): Promise<void> {
    const definition: CreateScheduleCommandInput = {
      Name: schedule.name,
      GroupName: this.settings.groupName,
      ScheduleExpression: `at(${schedule.at.toISOString().slice(0, 19)})`,
      ScheduleExpressionTimezone: 'UTC',
      FlexibleTimeWindow: { Mode: 'OFF' },
      ActionAfterCompletion: 'DELETE',
      State: 'ENABLED',
      Target: {
        Arn: this.settings.targetArn,
        RoleArn: this.settings.roleArn,
        Input: schedule.input,
        SqsParameters: { MessageGroupId: schedule.messageGroupId },
      },
    };
    try {
      await this.client.send(new CreateScheduleCommand(definition));
    } catch (error) {
      if (!isNamed(error, 'ConflictException')) throw error;
      // UpdateSchedule replaces the whole definition, so it takes the same fields.
      await this.client.send(new UpdateScheduleCommand(definition));
    }
  }

  async delete(name: string): Promise<void> {
    try {
      await this.client.send(new DeleteScheduleCommand({ Name: name, GroupName: this.settings.groupName }));
    } catch (error) {
      if (!isNamed(error, 'ResourceNotFoundException')) throw error;
    }
  }
}

function isNamed(error: unknown, name: string): boolean {
  return error instanceof Error && error.name === name;
}
