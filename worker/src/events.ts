import Ajv2020, { type ValidateFunction } from 'ajv/dist/2020';
import addFormats from 'ajv-formats';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

/** The envelope (specs/events/envelope.v1.schema.json); data is checked against the event's own schema. */
export interface Envelope {
  eventId: string;
  eventType: string;
  schemaVersion: number;
  occurredAt: string;
  correlationId: string;
  data: Record<string, unknown>;
}

/** Not JSON, or not a valid envelope or event: fails into the dead-letter queue after retries. */
export class InvalidEventError extends Error {
  override name = 'InvalidEventError';
}

/** A valid envelope of an eventType or schemaVersion this worker has no schema for (specs/06-events.md "Versioning"). */
export class UnknownEventError extends Error {
  override name = 'UnknownEventError';
}

const ENVELOPE = 'envelope.v1.schema.json';

/**
 * The schemas: copied next to the build (dist/schemas, by `npm run build`), else read from the repository's
 * specs/events when running from source (tests).
 */
export function defaultSchemaDir(): string {
  const candidates = [join(__dirname, 'schemas'), join(__dirname, '..', '..', 'specs', 'events')];
  const found = candidates.find((dir) => existsSync(join(dir, ENVELOPE)));
  if (!found) throw new Error(`Event schemas not found (looked in ${candidates.join(', ')}).`);
  return found;
}

/**
 * Parses and validates a message body (T23, decided 2026-11-23: ajv, draft 2020-12). Only the parsed JSON counts: the
 * body is the envelope as MySQL returns it, keys sorted and with its spacing (T21 review). Error messages name the
 * failing path and rule, never a value from the body.
 */
export function createEventParser(schemaDir: string = defaultSchemaDir()): (body: string) => Envelope {
  // strictTypes off: the event schemas add properties next to allOf: [envelope] without restating type: object, which
  // is valid JSON Schema; every other strict check stays on.
  const ajv = new Ajv2020({ allErrors: false, strict: true, strictTypes: false });
  addFormats(ajv);
  // The schema files reference one another by file name; an $id built from it lets those references resolve.
  const base = 'https://booking-platform.invalid/events/';
  const files = readdirSync(schemaDir).filter((f) => f.endsWith('.schema.json'));
  for (const file of files) {
    ajv.addSchema({ ...JSON.parse(readFileSync(join(schemaDir, file), 'utf8')), $id: base + file });
  }
  const envelope = ajv.getSchema(base + ENVELOPE)!;
  const events = new Map<string, ValidateFunction>();
  for (const file of files) {
    const match = /^([A-Za-z]+)\.v(\d+)\.schema\.json$/.exec(file);
    if (match && file !== ENVELOPE && !file.startsWith('common.')) events.set(`${match[1]}.v${match[2]}`, ajv.getSchema(base + file)!);
  }

  return (body: string) => {
    let value: unknown;
    try {
      value = JSON.parse(body);
    } catch {
      throw new InvalidEventError('The message body is not JSON.');
    }
    if (!envelope(value)) throw new InvalidEventError(`Invalid envelope: ${describe(envelope)}.`);
    const event = value as Envelope;
    const validate = events.get(`${event.eventType}.v${event.schemaVersion}`);
    if (!validate) throw new UnknownEventError(`No schema for ${event.eventType} v${event.schemaVersion}.`);
    if (!validate(value)) throw new InvalidEventError(`Invalid ${event.eventType} v${event.schemaVersion}: ${describe(validate)}.`);
    return event;
  };
}

function describe(validate: ValidateFunction): string {
  const error = validate.errors?.[0];
  return error ? `${error.instancePath || '/'} ${error.message ?? error.keyword}` : 'unknown error';
}
