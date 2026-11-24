/** The worker's settings, from environment variables until T27 wires Secrets Manager (decided 2026-11-23). */
export interface WorkerConfig {
  database: { host: string; port: number; user: string; password: string; database: string };
  emailFrom: string;
  /** T25: where reminder schedules live and what they send to (the SQS FIFO queue), and the role Scheduler uses. */
  reminders: { groupName: string; targetArn: string; roleArn: string };
}

const REQUIRED = [
  'DB_HOST', 'DB_PORT', 'DB_USER', 'DB_PASSWORD', 'DB_NAME', 'EMAIL_FROM', 'REMINDER_GROUP', 'REMINDER_TARGET_ARN', 'REMINDER_ROLE_ARN',
] as const;

/** Throws naming every missing or malformed setting, never a value. */
export function loadConfig(env: Record<string, string | undefined> = process.env): WorkerConfig {
  const missing = REQUIRED.filter((name) => !env[name]);
  if (missing.length > 0) throw new Error(`Missing worker settings: ${missing.join(', ')}.`);
  const port = Number(env.DB_PORT);
  if (!Number.isInteger(port) || port <= 0) throw new Error('DB_PORT must be a port number.');
  return {
    database: { host: env.DB_HOST!, port, user: env.DB_USER!, password: env.DB_PASSWORD!, database: env.DB_NAME! },
    emailFrom: env.EMAIL_FROM!,
    reminders: { groupName: env.REMINDER_GROUP!, targetArn: env.REMINDER_TARGET_ARN!, roleArn: env.REMINDER_ROLE_ARN! },
  };
}
