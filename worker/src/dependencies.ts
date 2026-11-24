import { GetSecretValueCommand, type GetSecretValueCommandOutput } from '@aws-sdk/client-secrets-manager';
import { createPool } from 'mysql2/promise';
import { loadConfig, type WorkerConfig } from './config';
import { ContactDirectory } from './contacts';
import { SesEmailSender, type SesClientLike } from './email';
import { createEventParser } from './events';
import { jsonLogger } from './log';
import { NotificationStore } from './notifications';
import type { PipelineDeps } from './pipeline';
import { createHandlers } from './registry';
import { EventBridgeReminderScheduler, type SchedulerClientLike } from './scheduler';

/** The part of SecretsManagerClient this uses, so tests can pass a stand-in. */
export interface SecretsClientLike {
  send(command: GetSecretValueCommand): Promise<Partial<GetSecretValueCommandOutput>>;
}

/**
 * Everything the pipeline needs, built from the environment (tested in dependencies.test.ts with stand-in AWS
 * clients). index.ts passes the real clients, once per Lambda container; close() ends the database pool. T27b: when
 * DB_PASSWORD_SECRET_ARN is set, the password is read from Secrets Manager here, once.
 */
export async function createDependencies(
  env: Record<string, string | undefined>,
  clients: { ses: SesClientLike; scheduler: SchedulerClientLike; secrets: SecretsClientLike },
): Promise<PipelineDeps & { close(): Promise<void> }> {
  const config = loadConfig(env);
  const { host, port, user, database } = config.database;
  const pool = createPool({ host, port, user, database, password: await databasePassword(config, clients.secrets), timezone: 'Z', connectionLimit: 2 });
  return {
    parse: createEventParser(),
    handlers: createHandlers(pool, { scheduler: new EventBridgeReminderScheduler(clients.scheduler, config.reminders), app: config.app }),
    notifications: new NotificationStore(pool),
    contacts: new ContactDirectory(pool),
    email: new SesEmailSender(clients.ses, config.emailFrom),
    log: jsonLogger(),
    close: () => pool.end(),
  };
}

async function databasePassword(config: WorkerConfig, secrets: SecretsClientLike): Promise<string> {
  if ('password' in config.database) return config.database.password;
  const output = await secrets.send(new GetSecretValueCommand({ SecretId: config.database.passwordSecretArn }));
  if (typeof output.SecretString !== 'string' || output.SecretString.length === 0) {
    throw new Error('The secret DB_PASSWORD_SECRET_ARN names has no string value.');
  }
  return output.SecretString;
}
