/** The browser's own IANA time zone. */
export function browserTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

/**
 * The browser's IANA zones, sorted, including the browser's own (some browsers leave e.g. UTC out of the list) and
 * `also` (e.g. the user's saved zone).
 */
export function timeZoneOptions(also?: string): string[] {
  const zones = new Set(Intl.supportedValuesOf("timeZone"));
  zones.add(browserTimeZone());
  if (also) zones.add(also);
  return [...zones].sort();
}
