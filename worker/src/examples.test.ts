import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createEventParser } from './events';

// T23 review (2026-11-23): specs/events/examples holds each event exactly as the API publishes it (written by the
// real OutboxWriter, read back from MySQL; checked by the C# EventExamplesTests). The worker must accept every one,
// so the two validators (Corvus in C#, ajv here) can't silently disagree.
describe('published event examples', () => {
  const specs = join(__dirname, '..', '..', 'specs', 'events');
  const examples = readdirSync(join(specs, 'examples')).filter((f) => f.endsWith('.json'));
  const parse = createEventParser();

  it.each(examples)('%s is accepted by the worker', (file) => {
    const body = readFileSync(join(specs, 'examples', file), 'utf8');
    const event = parse(body);
    expect(`${event.eventType}.v${event.schemaVersion}.json`).toBe(file);
  });

  it('every event schema the API publishes has an example', () => {
    // ReminderDue comes from EventBridge Scheduler (the worker sets its payload, T25), not from the API's outbox.
    const published = readdirSync(specs)
      .filter((f) => /^[A-Za-z]+\.v\d+\.schema\.json$/.test(f) && !f.startsWith('envelope.') && !f.startsWith('common.') && !f.startsWith('ReminderDue.'))
      .map((f) => f.replace('.schema.json', '.json'));
    expect(examples.sort()).toEqual(published.sort());
  });
});
