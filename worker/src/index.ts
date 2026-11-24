// Notification worker entry point (T23): the Lambda handler for the SQS FIFO queue (specs/06-events.md).
import { SESv2Client } from '@aws-sdk/client-sesv2';
import { createPool } from 'mysql2/promise';
import { loadConfig } from './config';
import { ContactDirectory } from './contacts';
import { SesEmailSender } from './email';
import { createEventParser } from './events';
import { createSqsHandler } from './handler';
import { jsonLogger } from './log';
import { NotificationStore } from './notifications';
import type { PipelineDeps } from './pipeline';
import { createHandlers } from './registry';

let deps: Promise<PipelineDeps> | undefined;

/** Built once per Lambda container and reused across invocations (the pool and SES client keep connections). */
function dependencies(): Promise<PipelineDeps> {
  deps ??= (async () => {
    const config = loadConfig();
    const pool = createPool({ ...config.database, timezone: 'Z', connectionLimit: 2 });
    return {
      parse: createEventParser(),
      handlers: createHandlers(pool),
      notifications: new NotificationStore(pool),
      contacts: new ContactDirectory(pool),
      email: new SesEmailSender(new SESv2Client({}), config.emailFrom),
      log: jsonLogger(),
    };
  })();
  return deps;
}

export const handler = createSqsHandler(dependencies);
