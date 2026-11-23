import type { SQSBatchResponse, SQSEvent } from 'aws-lambda';
import { describeErrorForLog } from './log';
import { processMessage, type PipelineDeps } from './pipeline';

/**
 * The Lambda handler for the SQS FIFO queue (T23). Messages are handled in order; at the first failure that message
 * and every later one are reported failed (ReportBatchItemFailures, as AWS documents for FIFO queues, decided
 * 2026-11-23), so no message overtakes an earlier one of its group. A failed message is retried and, after the
 * queue's maximum receive count, goes to the dead-letter queue.
 */
export function createSqsHandler(
  deps: () => Promise<PipelineDeps>,
  process: (body: string, deps: PipelineDeps) => Promise<unknown> = processMessage,
): (event: SQSEvent) => Promise<SQSBatchResponse> {
  return async (event) => {
    const resolved = await deps();
    const records = event.Records;
    for (let i = 0; i < records.length; i++) {
      const record = records[i];
      try {
        await process(record.body, resolved);
      } catch (error) {
        resolved.log.error('Message failed; it and the rest of the batch will be retried', {
          messageId: record.messageId,
          ...readableIds(record.body),
          error: describeErrorForLog(error),
          batchItemsFailed: records.length - i,
        });
        return { batchItemFailures: records.slice(i).map((r) => ({ itemIdentifier: r.messageId })) };
      }
    }
    return { batchItemFailures: [] };
  };
}

/** The envelope's IDs if the body is JSON and they are strings, for the failure log; never anything else from it. */
function readableIds(body: string): Record<string, string> {
  try {
    const value = JSON.parse(body) as Record<string, unknown>;
    const ids: Record<string, string> = {};
    for (const key of ['eventId', 'eventType', 'correlationId'] as const) {
      if (typeof value?.[key] === 'string') ids[key] = (value[key] as string).slice(0, 100);
    }
    return ids;
  } catch {
    return {};
  }
}
