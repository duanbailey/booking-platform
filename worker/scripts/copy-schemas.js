// Copies the event schemas (specs/events) next to the build, so the deployed worker validates against the same files
// the API's tests use (T23, decided 2026-11-23).
const { cpSync, mkdirSync, readdirSync } = require('node:fs');
const { join } = require('node:path');

const from = join(__dirname, '..', '..', 'specs', 'events');
const to = join(__dirname, '..', 'dist', 'schemas');
mkdirSync(to, { recursive: true });
for (const file of readdirSync(from).filter((f) => f.endsWith('.schema.json'))) cpSync(join(from, file), join(to, file));
