import type { SQSEvent, SQSRecord } from 'aws-lambda';
import { createSqsHandler } from './handler';
import type { PipelineDeps } from './pipeline';
import { CapturingLogger, passwordChanged } from './testing/fakes';

// T23: the Lambda handler for the FIFO queue. Decided 2026-11-23: messages are processed in order, and at the first
// failure that message and every later one in the batch are reported failed (ReportBatchItemFailures), so order is
// kept; the failure is logged with eventId, eventType and correlationId when readable, never the body.
describe('SQS handler', () => {
  function record(messageId: string, body: string): SQSRecord {
    return { messageId, body, attributes: { MessageGroupId: 'g' } } as unknown as SQSRecord;
  }

  function batch(...records: SQSRecord[]): SQSEvent {
    return { Records: records };
  }

  function harness(failOn: (body: string) => boolean) {
    const log = new CapturingLogger();
    const processed: string[] = [];
    const process = async (body: string) => {
      if (failOn(body)) throw new Error('boom');
      processed.push(body);
    };
    const handler = createSqsHandler(async () => ({ log }) as unknown as PipelineDeps, process);
    return { handler, log, processed };
  }

  it('reports nothing when every message succeeds', async () => {
    const { handler, processed } = harness(() => false);

    const result = await handler(batch(record('1', 'a'), record('2', 'b')));

    expect(result).toEqual({ batchItemFailures: [] });
    expect(processed).toEqual(['a', 'b']);
  });

  it('reports the first failure and every later message, and stops processing there', async () => {
    const { handler, processed } = harness((body) => body === 'b');

    const result = await handler(batch(record('1', 'a'), record('2', 'b'), record('3', 'c')));

    expect(result).toEqual({ batchItemFailures: [{ itemIdentifier: '2' }, { itemIdentifier: '3' }] });
    expect(processed).toEqual(['a']);
  });

  it('logs a failure with what can be read of the envelope, never the body', async () => {
    const event = passwordChanged({ correlationId: 'req-9' });
    const body = JSON.stringify({ ...event, data: { userId: 'private-value', method: 'Changed' } });
    const { handler, log } = harness(() => true);

    await handler(batch(record('m-1', body)));

    const error = log.entries.find((e) => e.level === 'error')!;
    expect(error).toMatchObject({ messageId: 'm-1', eventId: event.eventId, eventType: 'PasswordChanged', correlationId: 'req-9', error: 'boom' });
    expect(JSON.stringify(log.entries)).not.toContain('private-value');
  });

  it('removes email addresses from the logged error', async () => {
    const log = new CapturingLogger();
    const handler = createSqsHandler(async () => ({ log }) as unknown as PipelineDeps, async () => {
      throw new Error('MessageRejected: Email address is not verified: Ann.Example+x@sub.example.test');
    });

    await handler(batch(record('m-3', JSON.stringify(passwordChanged()))));

    const error = log.entries.find((e) => e.level === 'error')!;
    expect(error.error).toBe('MessageRejected: Email address is not verified: [email]');
  });

  it('logs a failure for a body that is not JSON', async () => {
    const { handler, log } = harness(() => true);

    const result = await handler(batch(record('m-2', '{not json')));

    expect(result.batchItemFailures).toEqual([{ itemIdentifier: 'm-2' }]);
    expect(log.entries.find((e) => e.level === 'error')).toMatchObject({ messageId: 'm-2' });
  });
});
