import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { createDatabase, TEST_DATABASE_VARIABLE } from './mysql';

/**
 * Jest global setup (T23, decided 2026-11-23): one throwaway database per run, its schema generated from the EF Core
 * migrations with `dotnet ef migrations script`, so it always matches the API's. Needs the .NET SDK and the test
 * MySQL (docker compose up -d --wait).
 */
export default async function globalSetup(): Promise<void> {
  const repoRoot = resolve(__dirname, '..', '..', '..');
  const dir = mkdtempSync(join(tmpdir(), 'worker-schema-'));
  try {
    const file = join(dir, 'schema.sql');
    execFileSync('dotnet', ['ef', 'migrations', 'script', '--project', 'src/BookingPlatform.Api', '--output', file], {
      cwd: repoRoot,
      stdio: ['ignore', 'ignore', 'inherit'],
    });
    const sql = readFileSync(file, 'utf8').replace(/^﻿/, '');
    const name = `worker_test_${randomUUID().replace(/-/g, '').slice(0, 16)}`;
    await createDatabase(name, sql);
    process.env[TEST_DATABASE_VARIABLE] = name; // inherited by the test workers and seen by teardown
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}
