import { vi } from "vitest";

export type Answer = Response | Error | (() => Response | Promise<Response>);

/** A recorded call to the fake API. */
export interface ApiCall {
  url: string;
  method: string;
  headers: Headers;
  body: unknown;
  credentials: RequestCredentials | undefined;
  signal: AbortSignal | undefined;
}

/**
 * Replaces global fetch with a fake API: each "METHOD /path" key answers its calls with the next entry of its
 * queue (a Response, an Error to throw, or a function returning a Response). A call with no answer left fails the
 * test. Returns the calls made, by key.
 */
export function mockApi(routes: Record<string, Answer[]>) {
  const queues = new Map(Object.entries(routes).map(([k, v]) => [k, [...v]]));
  const calls: Record<string, ApiCall[]> = {};
  vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit = {}) => {
    const key = `${init.method ?? "GET"} ${url}`;
    (calls[key] ??= []).push({
      url,
      method: init.method ?? "GET",
      headers: new Headers(init.headers),
      body: typeof init.body === "string" ? JSON.parse(init.body) : undefined,
      credentials: init.credentials,
      signal: init.signal ?? undefined,
    });
    const queue = queues.get(key);
    const next = queue?.[0];
    if (next === undefined) throw new Error(`Unexpected call: ${key}`);
    if (!repeating.has(next)) queue!.shift();
    if (next instanceof Error) throw next;
    return typeof next === "function" ? next() : next;
  }));
  return {
    /** The calls made to "METHOD /path", in order. */
    calls: (key: string): ApiCall[] => calls[key] ?? [],
    /** Adds answers for "METHOD /path". */
    answer: (key: string, ...answers: Answer[]) => {
      queues.set(key, [...(queues.get(key) ?? []), ...answers]);
    },
  };
}

const repeating = new WeakSet<object>();

/** An answer given to every call (it stays first in its queue), e.g. for data a loader reloads. */
export function always(make: () => Response | Promise<Response>): () => Response | Promise<Response> {
  const answer = () => make();
  repeating.add(answer);
  return answer;
}

/** A JSON response. */
export function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

/** An API error response in the shared Error shape. */
export function apiError(status: number, code: string, details: Record<string, unknown> = {}, message = "API message.") {
  return new Response(JSON.stringify({ code, message, rule: null, details }), {
    status, headers: { "Content-Type": "application/json" },
  });
}

/** A successful login or refresh body (AuthToken). */
export function token(accessToken: string) {
  return new Response(JSON.stringify({
    accessToken, tokenType: "Bearer", expiresAt: "2026-11-27T12:15:00Z", refreshTokenExpiresAt: "2026-12-27T12:00:00Z",
  }), { status: 200, headers: { "Content-Type": "application/json" } });
}

/** A getMe body for a user with the given roles. */
export function me(
  roles: { admin?: boolean; owner?: boolean; staff?: boolean } = {}, name = "Olive Owner", timeZone = "America/New_York",
) {
  return new Response(JSON.stringify({
    id: "u1", email: "olive@example.test", name, timeZone,
    isAdmin: roles.admin ?? false,
    ownedBusinessId: roles.owner ? "b1" : null,
    staff: roles.staff ? [{ staffId: "s1", businessId: "b1" }] : [],
  }), { status: 200, headers: { "Content-Type": "application/json" } });
}

/** A fetch answer that never arrives (until the call is aborted). */
export function pending(): () => Promise<Response> {
  return () => new Promise<Response>(() => {});
}
