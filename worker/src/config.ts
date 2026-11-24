/**
 * The worker's settings, from environment variables (decided 2026-11-23). T27b: the database password comes either from
 * DB_PASSWORD (local, tests) or from the Secrets Manager secret DB_PASSWORD_SECRET_ARN names, read at startup, so it
 * never sits in the Lambda configuration or Terraform state.
 */
export interface WorkerConfig {
  database: { host: string; port: number; user: string; database: string } & ({ password: string } | { passwordSecretArn: string });
  emailFrom: string;
  /** T25: where reminder schedules live and what they send to (the SQS FIFO queue), and the role Scheduler uses. */
  reminders: { groupName: string; targetArn: string; roleArn: string; deadLetterArn: string };
  /** T26: where invite and recovery links point (no trailing slash), and the product name in account emails. */
  app: { baseUrl: string; name: string };
}

const REQUIRED = [
  'DB_HOST', 'DB_PORT', 'DB_USER', 'DB_NAME', 'EMAIL_FROM', 'REMINDER_GROUP', 'REMINDER_TARGET_ARN', 'REMINDER_ROLE_ARN', 'REMINDER_DLQ_ARN',
  'APP_BASE_URL', 'APP_NAME',
] as const;

/** Throws naming every missing or malformed setting, never a value. */
export function loadConfig(env: Record<string, string | undefined> = process.env): WorkerConfig {
  const missing = REQUIRED.filter((name) => !env[name]);
  if (missing.length > 0) throw new Error(`Missing worker settings: ${missing.join(', ')}.`);
  if (!env.DB_PASSWORD && !env.DB_PASSWORD_SECRET_ARN) throw new Error('Missing worker settings: DB_PASSWORD or DB_PASSWORD_SECRET_ARN.');
  if (env.DB_PASSWORD && env.DB_PASSWORD_SECRET_ARN) throw new Error('Set only one of DB_PASSWORD and DB_PASSWORD_SECRET_ARN.');
  const port = Number(env.DB_PORT);
  if (!Number.isInteger(port) || port <= 0) throw new Error('DB_PORT must be a port number.');
  return {
    app: { baseUrl: baseUrl(env.APP_BASE_URL!), name: env.APP_NAME! },
    database: {
      host: env.DB_HOST!, port, user: env.DB_USER!,
      ...(env.DB_PASSWORD ? { password: env.DB_PASSWORD } : { passwordSecretArn: env.DB_PASSWORD_SECRET_ARN! }),
      database: env.DB_NAME!,
    },
    emailFrom: env.EMAIL_FROM!,
    reminders: {
      groupName: env.REMINDER_GROUP!, targetArn: env.REMINDER_TARGET_ARN!, roleArn: env.REMINDER_ROLE_ARN!,
      deadLetterArn: env.REMINDER_DLQ_ARN!,
    },
  };
}

/** An absolute http(s) URL without query or fragment, returned without a trailing slash so paths join cleanly (T26). */
function baseUrl(value: string): string {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error('APP_BASE_URL must be an absolute http(s) URL.');
  }
  if ((url.protocol !== 'https:' && url.protocol !== 'http:') || url.search || url.hash || value.includes('?') || value.includes('#')) {
    throw new Error('APP_BASE_URL must be an absolute http(s) URL without a query or fragment.');
  }
  return value.replace(/\/+$/, '');
}
