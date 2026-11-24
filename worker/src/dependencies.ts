import { createPool } from 'mysql2/promise';
import { loadConfig } from './config';
import { ContactDirectory } from './contacts';
import { SesEmailSender, type SesClientLike } from './email';
import { createEventParser } from './events';
import { jsonLogger } from './log';
import { NotificationStore } from './notifications';
import type { PipelineDeps } from './pipeline';
import { createHandlers } from './registry';
import { EventBridgeReminderScheduler, type SchedulerClientLike } from './scheduler';

/**
 * Everything the pipeline needs, built from the environment (tested in dependencies.test.ts with stand-in AWS
 * clients). index.ts passes the real SES and Scheduler clients; close() ends the database pool.
 */
export function createDependencies(
  env: Record<string, string | undefined>,
  clients: { ses: SesClientLike; scheduler: SchedulerClientLike },
): PipelineDeps & { close(): Promise<void> } {
  const config = loadConfig(env);
  const pool = createPool({ ...config.database, timezone: 'Z', connectionLimit: 2 });
  return {
    parse: createEventParser(),
    handlers: createHandlers(pool, new EventBridgeReminderScheduler(clients.scheduler, config.reminders)),
    notifications: new NotificationStore(pool),
    contacts: new ContactDirectory(pool),
    email: new SesEmailSender(clients.ses, config.emailFrom),
    log: jsonLogger(),
    close: () => pool.end(),
  };
}
