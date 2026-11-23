import { createEventParser, InvalidEventError, UnknownEventError } from './events';
import { passwordChanged } from './testing/fakes';

// T23: envelope and schema validation against specs/events (decided 2026-11-23: ajv, draft 2020-12). The body is
// the envelope as MySQL returns it (T21 review): same content, keys sorted, MySQL's spacing, so only parsed JSON counts.
describe('event parsing', () => {
  const parse = createEventParser();

  it('accepts a valid event and returns its envelope', () => {
    const event = passwordChanged();
    const parsed = parse(JSON.stringify(event));
    expect(parsed).toEqual(event);
  });

  it('accepts the same event in MySQL canonical form (keys sorted, its spacing)', () => {
    const event = passwordChanged();
    const canonical =
      `{"data": {"method": "Changed", "userId": "${event.data.userId}"}, "eventId": "${event.eventId}", ` +
      `"eventType": "PasswordChanged", "occurredAt": "${event.occurredAt}", "correlationId": "corr-123", "schemaVersion": 1}`;
    expect(parse(canonical)).toEqual(event);
  });

  it('accepts every event type the API writes (one sample each)', () => {
    const id = () => '0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b';
    const slot = { staffId: id(), startUtc: '2026-12-01T10:00:00.000Z', endUtc: '2026-12-01T11:00:00.000Z' };
    const samples: [string, object][] = [
      ['BookingCancelled', {
        bookingId: id(), bookingVersion: 2, businessId: id(), businessTimeZone: 'Europe/London', serviceId: id(), customerId: id(),
        slot, cancelledBy: { userId: id(), role: 'Owner' }, reason: 'TimeOff',
      }],
      ['BookingLocalTimeChanged', {
        bookingId: id(), businessId: id(), customerId: id(), slot, oldTimeZone: 'America/New_York', newTimeZone: 'America/Chicago', timeZoneChangeId: id(),
      }],
      ['PasswordResetRequested', { userId: id(), passwordResetId: id() }],
    ];
    for (const [eventType, data] of samples) {
      const body = JSON.stringify({ ...passwordChanged(), eventType, data });
      expect(() => parse(body)).not.toThrow();
    }
  });

  it.each([
    ['not JSON', '{"eventId": '],
    ['not an object', '"PasswordChanged"'],
    ['an envelope field missing', JSON.stringify({ ...passwordChanged(), correlationId: undefined })],
    ['an eventId that is not a UUID', JSON.stringify({ ...passwordChanged(), eventId: 'abc' })],
    ['occurredAt without Z', JSON.stringify({ ...passwordChanged(), occurredAt: '2026-11-23T10:00:00.000+01:00' })],
    ['data breaking its schema', JSON.stringify({ ...passwordChanged(), data: { userId: 'x', method: 'Changed' } })],
    ['an unexpected data field', JSON.stringify({ ...passwordChanged(), data: { ...passwordChanged().data, extra: 1 } })],
  ])('rejects %s as invalid', (_, body) => {
    expect(() => parse(body)).toThrow(InvalidEventError);
  });

  it.each([
    ['an unknown eventType', { eventType: 'BookingTeleported' }],
    ['an unknown schemaVersion', { schemaVersion: 2 }],
  ])('rejects %s as unknown, so it goes to the dead-letter queue', (_, change) => {
    expect(() => parse(JSON.stringify({ ...passwordChanged(), ...change }))).toThrow(UnknownEventError);
  });

  it('never puts the body in its error message', () => {
    const body = JSON.stringify({ ...passwordChanged(), data: { userId: 'secret-looking-value', method: 'Changed' } });
    expect(() => parse(body)).toThrow(expect.objectContaining({ message: expect.not.stringContaining('secret-looking-value') }));
  });
});
