// Times on screen (T36, R2) match the worker's emails (worker/src/time.ts, decided 2026-11-24): a 24-hour range with
// the date, the IANA zone and its UTC offset (which tells apart the two occurrences of a local time on a fall-back
// night), and the duration as elapsed time. Kept in step with the worker by the same test cases.

const WEEKDAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October",
  "November", "December"];

const formatters = new Map<string, Intl.DateTimeFormat>();

function formatterFor(zone: string): Intl.DateTimeFormat {
  let formatter = formatters.get(zone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat("en-US", {
      timeZone: zone, year: "numeric", month: "numeric", day: "numeric", hour: "numeric", minute: "numeric",
      second: "numeric", hourCycle: "h23",
    });
    formatters.set(zone, formatter);
  }
  return formatter;
}

interface LocalTime {
  /** yyyy-mm-dd */
  isoDate: string;
  date: string;
  time: string;
  offset: string;
}

const pad = (n: number) => String(n).padStart(2, "0");

/** The instant's wall-clock date, time and UTC offset in the zone. */
function local(instant: Date, zone: string): LocalTime {
  if (Number.isNaN(instant.getTime())) throw new RangeError("Not a timestamp.");
  const parts = Object.fromEntries(formatterFor(zone).formatToParts(instant).map((p) => [p.type, p.value]));
  const [year, month, day, hour, minute, second] =
    ["year", "month", "day", "hour", "minute", "second"].map((k) => Number(parts[k])) as
      [number, number, number, number, number, number];
  const wallAsUtc = Date.UTC(year, month - 1, day, hour, minute, second);
  const offsetMinutes = Math.round((wallAsUtc - Math.floor(instant.getTime() / 1000) * 1000) / 60000);
  const isoDate = `${year}-${pad(month)}-${pad(day)}`;
  return { isoDate, date: formatDate(isoDate), time: `${pad(hour)}:${pad(minute)}`, offset: formatOffset(offsetMinutes) };
}

function formatOffset(minutes: number): string {
  const sign = minutes < 0 ? "-" : "+";
  const abs = Math.abs(minutes);
  return `UTC${sign}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`;
}

/**
 * E.g. "Monday 23 November 2026, 14:00-15:00 America/New_York (UTC-05:00)". A range ending on another local date
 * names both dates; one crossing an offset change (R2) gives each end its own offset.
 */
export function formatRange(startUtc: string, endUtc: string, zone: string): string {
  const start = local(new Date(startUtc), zone);
  const end = local(new Date(endUtc), zone);
  const sameDate = start.date === end.date;
  const endText = sameDate ? end.time : `${end.date}, ${end.time}`;
  if (start.offset === end.offset) {
    return `${start.date}, ${start.time}${sameDate ? "-" : " - "}${endText} ${zone} (${start.offset})`;
  }
  return `${start.date}, ${start.time} (${start.offset}) - ${endText} (${end.offset}) ${zone}`;
}

/** A slot's times under its date heading, e.g. "14:00-15:00"; the full range when it crosses a date or offset change. */
export function formatTimes(startUtc: string, endUtc: string, zone: string): string {
  const start = local(new Date(startUtc), zone);
  const end = local(new Date(endUtc), zone);
  if (start.date !== end.date || start.offset !== end.offset) return formatRange(startUtc, endUtc, zone);
  return `${start.time}-${end.time}`;
}

/** E.g. "1 hour 30 minutes". */
export function formatMinutes(minutes: number): string {
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  const parts: string[] = [];
  if (hours > 0) parts.push(`${hours} ${hours === 1 ? "hour" : "hours"}`);
  if (rest > 0 || hours === 0) parts.push(`${rest} ${rest === 1 ? "minute" : "minutes"}`);
  return parts.join(" ");
}

/** The elapsed time between start and end (R2: actual elapsed time across a transition). */
export function formatDuration(startUtc: string, endUtc: string): string {
  return formatMinutes(Math.round((new Date(endUtc).getTime() - new Date(startUtc).getTime()) / 60000));
}

/** The instant's local date (yyyy-mm-dd) in the zone. */
export function localDateOf(utc: string, zone: string): string {
  return local(new Date(utc), zone).isoDate;
}

/** Today's local date (yyyy-mm-dd) in the zone. */
export function todayIn(zone: string, now: Date = new Date()): string {
  return local(now, zone).isoDate;
}

function parseDate(isoDate: string): number {
  const [year, month, day] = isoDate.split("-").map(Number) as [number, number, number];
  return Date.UTC(year, month - 1, day);
}

function toIsoDate(utcMidnight: number): string {
  const d = new Date(utcMidnight);
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
}

/** E.g. "Monday 30 November 2026" for "2026-11-30". */
export function formatDate(isoDate: string): string {
  const d = new Date(parseDate(isoDate));
  return `${WEEKDAYS[d.getUTCDay()]} ${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}`;
}

/** The local date `days` after (or before) `isoDate`. */
export function addDays(isoDate: string, days: number): string {
  return toIsoDate(parseDate(isoDate) + days * 86_400_000);
}

/** How many dates the inclusive range from..to covers. */
export function rangeDays(from: string, to: string): number {
  return Math.round((parseDate(to) - parseDate(from)) / 86_400_000) + 1;
}
