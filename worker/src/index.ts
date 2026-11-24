// Notification worker entry point (T23): the Lambda handler for the SQS FIFO queue (specs/06-events.md).
import { SchedulerClient } from '@aws-sdk/client-scheduler';
import { SecretsManagerClient } from '@aws-sdk/client-secrets-manager';
import { SESv2Client } from '@aws-sdk/client-sesv2';
import { createDependencies } from './dependencies';
import { createSqsHandler } from './handler';
import type { PipelineDeps } from './pipeline';

let deps: Promise<PipelineDeps> | undefined;

/** Built once per Lambda container and reused across invocations (the pool and AWS clients keep connections). */
function dependencies(): Promise<PipelineDeps> {
  // A failed start (e.g. Secrets Manager briefly unreachable, T27b) is not cached: the next invocation tries again.
  deps ??= createDependencies(process.env, {
    ses: new SESv2Client({}), scheduler: new SchedulerClient({}), secrets: new SecretsManagerClient({}),
  }).catch((error: unknown) => {
    deps = undefined;
    throw error;
  });
  return deps;
}

export const handler = createSqsHandler(dependencies);
