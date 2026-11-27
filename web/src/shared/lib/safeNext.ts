/**
 * The ?next= path to go on to after login or sign-up, or null when it isn't a path on this origin (T34). A path
 * must start with one "/"; "//host" and "/\host" are refused because browsers read both as another site.
 */
export function safeNext(next: string | null): string | null {
  if (!next || !next.startsWith("/") || next.startsWith("//") || next.startsWith("/\\")) return null;
  return next;
}

/** `path` with ?next= kept, when there is a next to keep. */
export function withNext(path: string, next: string | null): string {
  return next ? `${path}?${new URLSearchParams({ next })}` : path;
}
