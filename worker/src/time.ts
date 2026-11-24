// Times in emails (T24, R2; decided 2026-11-24, plan/tasks.md): the business's IANA zone first, the recipient's own
// zone only when it differs, a 24-hour range with the date, the zone name and its UTC offset (which tells apart the
// two occurrences of a local time on a fall-back night), and the duration as elapsed time.

const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

const formatters = new Map<string, Intl.DateTimeFormat>();

function formatterFor(zone: string): Intl.DateTimeFormat {
  let formatter = formatters.get(zone);
  if (!formatter) {
    // Throws a RangeError for an unknown zone, failing the message rather than sending a wrong time.
    formatter = new Intl.DateTimeFormat('en-US', {
      timeZone: zone, year: 'numeric', month: 'numeric', day: 'numeric', hour: 'numeric', minute: 'numeric',
      second: 'numeric', hourCycle: 'h23',
    });
    formatters.set(zone, formatter);
  }
  return formatter;
}

interface LocalTime {
  date: string;
  time: string;
  offset: string;
}

/** The instant's wall-clock date, time and UTC offset in the zone. */
function local(utc: string, zone: string): LocalTime {
  const instant = new Date(utc);
  if (Number.isNaN(instant.getTime())) throw new RangeError(`Not a timestamp: ${utc}`);
  const parts = Object.fromEntries(formatterFor(zone).formatToParts(instant).map((p) => [p.type, p.value]));
  const [year, month, day, hour, minute, second] = ['year', 'month', 'day', 'hour', 'minute', 'second'].map((k) => Number(parts[k]));
  const wallAsUtc = Date.UTC(year, month - 1, day, hour, minute, second);
  const offsetMinutes = Math.round((wallAsUtc - Math.floor(instant.getTime() / 1000) * 1000) / 60000);
  const weekday = WEEKDAYS[new Date(Date.UTC(year, month - 1, day)).getUTCDay()];
  return {
    date: `${weekday} ${day} ${MONTHS[month - 1]} ${year}`,
    time: `${pad(hour)}:${pad(minute)}`,
    offset: formatOffset(offsetMinutes),
  };
}

function pad(n: number): string {
  return String(n).padStart(2, '0');
}

function formatOffset(minutes: number): string {
  const sign = minutes < 0 ? '-' : '+';
  const abs = Math.abs(minutes);
  return `UTC${sign}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`;
}

/** E.g. "Tuesday 1 December 2026, 10:00 America/New_York (UTC-05:00)": one instant (T26: expiries, change times). */
export function formatInstant(utc: string, zone: string): string {
  const at = local(utc, zone);
  return `${at.date}, ${at.time} ${zone} (${at.offset})`;
}

/** E.g. "Monday 23 November 2026, 14:00": a start in the zone, for subjects. */
export function formatStart(startUtc: string, zone: string): string {
  const start = local(startUtc, zone);
  return `${start.date}, ${start.time}`;
}

/**
 * E.g. "Monday 23 November 2026, 14:00-15:00 America/New_York (UTC-05:00)". A range ending on another local date
 * names both dates; one crossing an offset change (R2) gives each end its own offset.
 */
export function formatRange(startUtc: string, endUtc: string, zone: string): string {
  const start = local(startUtc, zone);
  const end = local(endUtc, zone);
  const sameDate = start.date === end.date;
  const endText = sameDate ? end.time : `${end.date}, ${end.time}`;
  if (start.offset === end.offset) {
    return `${start.date}, ${start.time}${sameDate ? '-' : ' - '}${endText} ${zone} (${start.offset})`;
  }
  return `${start.date}, ${start.time} (${start.offset}) - ${endText} (${end.offset}) ${zone}`;
}

/** The elapsed time between start and end (R2: actual elapsed time across a transition), e.g. "1 hour 30 minutes". */
export function formatDuration(startUtc: string, endUtc: string): string {
  const minutes = Math.round((new Date(endUtc).getTime() - new Date(startUtc).getTime()) / 60000);
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  const parts: string[] = [];
  if (hours > 0) parts.push(`${hours} ${hours === 1 ? 'hour' : 'hours'}`);
  if (rest > 0 || hours === 0) parts.push(`${rest} ${rest === 1 ? 'minute' : 'minutes'}`);
  return parts.join(' ');
}

/** The "When", optional "Your time" and "Duration" lines of a booking (R2). */
export function whenLines(slot: { startUtc: string; endUtc: string }, businessZone: string, recipientZone: string): string[] {
  const lines = [`When: ${formatRange(slot.startUtc, slot.endUtc, businessZone)}`];
  if (recipientZone !== businessZone) lines.push(`Your time: ${formatRange(slot.startUtc, slot.endUtc, recipientZone)}`);
  lines.push(`Duration: ${formatDuration(slot.startUtc, slot.endUtc)}`);
  return lines;
}
