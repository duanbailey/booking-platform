import { vi } from "vitest";

/** A Response with a JSON body. */
export function json(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });
}

/** Replaces global fetch with a mock answering each call with the next response (or throwing it if it's an Error). */
export function mockFetch(...responses: (Response | Error)[]) {
  const fetchMock = vi.fn(async (..._args: unknown[]) => {
    const next = responses.shift();
    if (next === undefined) throw new Error("Unexpected fetch call");
    if (next instanceof Error) throw next;
    return next;
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

/** The URL and init of the nth fetch call. */
export function call(fetchMock: ReturnType<typeof mockFetch>, n = 0): [string, RequestInit] {
  const args = fetchMock.mock.calls[n];
  if (!args) throw new Error(`fetch was not called ${n + 1} times`);
  return [args[0] as string, (args[1] ?? {}) as RequestInit];
}
