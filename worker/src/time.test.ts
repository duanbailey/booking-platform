import { formatDuration, formatInstant, formatRange, formatStart, whenLines } from './time';

// T24, R2: times in emails (decided 2026-11-24, plan/tasks.md): the business's zone first, the recipient's own zone
// only when it differs, a 24-hour range with the date, the IANA name and the UTC offset, and the elapsed duration.
describe('time formatting (R2)', () => {
  it('formats a same-day range with the zone and its offset', () => {
    expect(formatRange('2026-11-23T19:00:00Z', '2026-11-23T20:00:00Z', 'America/New_York'))
      .toBe('Monday 23 November 2026, 14:00-15:00 America/New_York (UTC-05:00)');
  });

  it('formats a zone at UTC and a zone with a half-hour offset', () => {
    expect(formatRange('2026-11-23T19:00:00Z', '2026-11-23T20:00:00Z', 'Europe/London'))
      .toBe('Monday 23 November 2026, 19:00-20:00 Europe/London (UTC+00:00)');
    expect(formatRange('2026-11-23T19:00:00Z', '2026-11-23T20:00:00Z', 'Asia/Kolkata'))
      .toBe('Tuesday 24 November 2026, 00:30-01:30 Asia/Kolkata (UTC+05:30)');
  });

  it('names both dates when the range ends on another local date', () => {
    expect(formatRange('2026-11-24T03:00:00Z', '2026-11-24T06:00:00Z', 'America/New_York'))
      .toBe('Monday 23 November 2026, 22:00 - Tuesday 24 November 2026, 01:00 America/New_York (UTC-05:00)');
  });

  it('R2: a 2-hour booking at 01:00 on a spring-forward night ends at 04:00 local and lasts 2 hours', () => {
    // 2026-03-08: New York skips 02:00-03:00 (EST UTC-05:00 to EDT UTC-04:00).
    const start = '2026-03-08T06:00:00Z';
    const end = '2026-03-08T08:00:00Z';
    expect(formatRange(start, end, 'America/New_York'))
      .toBe('Sunday 8 March 2026, 01:00 (UTC-05:00) - 04:00 (UTC-04:00) America/New_York');
    expect(formatDuration(start, end)).toBe('2 hours');
  });

  it('R2: the offset tells apart the two 01:30s of a fall-back night', () => {
    // 2026-11-01: New York repeats 01:00-02:00 (EDT UTC-04:00, then EST UTC-05:00).
    expect(formatRange('2026-11-01T05:30:00Z', '2026-11-01T06:00:00Z', 'America/New_York'))
      .toBe('Sunday 1 November 2026, 01:30 (UTC-04:00) - 01:00 (UTC-05:00) America/New_York');
    expect(formatRange('2026-11-01T05:30:00Z', '2026-11-01T05:45:00Z', 'America/New_York'))
      .toBe('Sunday 1 November 2026, 01:30-01:45 America/New_York (UTC-04:00)');
    expect(formatRange('2026-11-01T06:30:00Z', '2026-11-01T06:45:00Z', 'America/New_York'))
      .toBe('Sunday 1 November 2026, 01:30-01:45 America/New_York (UTC-05:00)');
  });

  it('formats durations as elapsed hours and minutes', () => {
    const at = (minutes: number) => new Date(Date.UTC(2026, 10, 23, 9) + minutes * 60000).toISOString();
    expect(formatDuration(at(0), at(1))).toBe('1 minute');
    expect(formatDuration(at(0), at(45))).toBe('45 minutes');
    expect(formatDuration(at(0), at(60))).toBe('1 hour');
    expect(formatDuration(at(0), at(90))).toBe('1 hour 30 minutes');
    expect(formatDuration(at(0), at(121))).toBe('2 hours 1 minute');
    expect(formatDuration(at(0), at(1440))).toBe('24 hours');
  });

  it('formats an instant with its zone and offset (T26: link expiry, password change time)', () => {
    expect(formatInstant('2026-12-01T15:00:00.000Z', 'America/New_York')).toBe('Tuesday 1 December 2026, 10:00 America/New_York (UTC-05:00)');
  });

  it('formats a start for subjects', () => {
    expect(formatStart('2026-11-23T19:00:00Z', 'America/New_York')).toBe('Monday 23 November 2026, 14:00');
  });

  it('shows the recipient\'s own zone only when it differs from the business\'s', () => {
    const slot = { startUtc: '2026-11-23T19:00:00Z', endUtc: '2026-11-23T20:00:00Z' };
    expect(whenLines(slot, 'America/New_York', 'Europe/London')).toEqual([
      'When: Monday 23 November 2026, 14:00-15:00 America/New_York (UTC-05:00)',
      'Your time: Monday 23 November 2026, 19:00-20:00 Europe/London (UTC+00:00)',
      'Duration: 1 hour',
    ]);
    expect(whenLines(slot, 'America/New_York', 'America/New_York')).toEqual([
      'When: Monday 23 November 2026, 14:00-15:00 America/New_York (UTC-05:00)',
      'Duration: 1 hour',
    ]);
  });
});
