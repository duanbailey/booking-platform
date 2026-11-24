import { dropDatabase, dropWorkerUser, TEST_DATABASE_VARIABLE, WORKER_USER_VARIABLE } from './mysql';

/** Drops the database and the worker user global setup created. */
export default async function globalTeardown(): Promise<void> {
  const user = process.env[WORKER_USER_VARIABLE];
  if (user) await dropWorkerUser(user);
  const name = process.env[TEST_DATABASE_VARIABLE];
  if (name) await dropDatabase(name);
}
