/** The browser's own IANA time zone. */
export function browserTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

/** The browser's IANA zones, sorted, including the browser's own (some browsers leave e.g. UTC out of the list). */
export function timeZoneOptions(): string[] {
  const zones = new Set(Intl.supportedValuesOf("timeZone"));
  zones.add(browserTimeZone());
  return [...zones].sort();
}
