import { createConnection, createPool, type Pool, type RowDataPacket } from 'mysql2/promise';
import { randomUUID } from 'node:crypto';

/**
 * The test MySQL server: BOOKING_TEST_MYSQL in the C# tests' format ("Server=...;Port=...;User ID=...;Password=..."),
 * defaulting to docker-compose.yml's MySQL, as the C# integration tests do (T1).
 */
export function serverSettings(env: NodeJS.ProcessEnv = process.env) {
  const parts = Object.fromEntries(
    (env.BOOKING_TEST_MYSQL ?? 'Server=localhost;Port=3307;User ID=root;Password=localdev;')
      .split(';')
      .filter((p) => p.includes('='))
      .map((p) => {
        const [key, ...value] = p.split('=');
        return [key.trim().toLowerCase(), value.join('=').trim()];
      }),
  );
  return {
    host: parts['server'] ?? 'localhost',
    port: Number(parts['port'] ?? 3307),
    user: parts['user id'] ?? parts['user'] ?? 'root',
    password: parts['password'] ?? '',
  };
}

export const TEST_DATABASE_VARIABLE = 'WORKER_TEST_DATABASE';

/** The database global setup created for this run. */
export function testDatabaseName(): string {
  const name = process.env[TEST_DATABASE_VARIABLE];
  if (!name) throw new Error(`${TEST_DATABASE_VARIABLE} is not set: run the tests through Jest's global setup.`);
  return name;
}

export function testPool(): Pool {
  return createPool({ ...serverSettings(), database: testDatabaseName(), timezone: 'Z', connectionLimit: 4 });
}

export async function createDatabase(name: string, schemaSql: string): Promise<void> {
  const connection = await createConnection({ ...serverSettings(), multipleStatements: true, timezone: 'Z' });
  try {
    await connection.query(`CREATE DATABASE \`${name}\``);
    await connection.query(`USE \`${name}\``);
    await connection.query(schemaSql);
  } finally {
    await connection.end();
  }
}

export async function dropDatabase(name: string): Promise<void> {
  const connection = await createConnection(serverSettings());
  try {
    await connection.query(`DROP DATABASE IF EXISTS \`${name}\``);
  } finally {
    await connection.end();
  }
}

/** Inserts a user with a password hash, so tests can check the worker never reads it. */
export async function insertUser(pool: Pool, fields: { name?: string; email?: string; timeZone?: string } = {}): Promise<string> {
  const id = randomUUID();
  await pool.query(
    'INSERT INTO users (id, email, password_hash, name, time_zone, created_at) VALUES (?, ?, ?, ?, ?, UTC_TIMESTAMP(6))',
    [id, fields.email ?? `${id}@example.test`, 'secret-hash-never-read', fields.name ?? 'Ann Example', fields.timeZone ?? 'Europe/London'],
  );
  return id;
}

export async function notificationsFor(pool: Pool, eventId: string): Promise<RowDataPacket[]> {
  const [rows] = await pool.query<RowDataPacket[]>(
    'SELECT * FROM notifications WHERE event_id = ? ORDER BY recipient_user_id', [eventId]);
  return rows;
}
