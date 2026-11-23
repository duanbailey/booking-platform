import { dropDatabase, TEST_DATABASE_VARIABLE } from './mysql';

/** Drops the database global setup created. */
export default async function globalTeardown(): Promise<void> {
  const name = process.env[TEST_DATABASE_VARIABLE];
  if (name) await dropDatabase(name);
}
