import type { components } from "./schema";

/** The API's error codes (generated from specs/04-api.openapi.yaml), plus NetworkError when no response arrived. */
export type ErrorCode = components["schemas"]["ErrorCode"] | "NetworkError";

type ErrorBody = components["schemas"]["Error"];

/** A failed API call: the shared Error shape, its HTTP status (0 without a response) and the correlation ID. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: ErrorCode;
  readonly rule: string | null;
  readonly details: Record<string, unknown>;
  readonly correlationId: string | undefined;

  constructor(init: {
    status: number;
    code: ErrorCode;
    message: string;
    rule: string | null;
    details: Record<string, unknown>;
    correlationId: string | undefined;
  }) {
    super(init.message);
    this.name = "ApiError";
    this.status = init.status;
    this.code = init.code;
    this.rule = init.rule;
    this.details = init.details;
    this.correlationId = init.correlationId;
  }
}

/** Relative paths only: the app and the API share one origin (ADR 0012). */
export type ApiPath = `/v1/${string}`;

export interface ApiRequest {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  /** Sent as JSON. */
  body?: unknown;
  /** Sent as a Bearer token; the session (T34) keeps it in memory only (ADR 0013). */
  accessToken?: string;
  headers?: Record<string, string>;
  signal?: AbortSignal;
}

/**
 * Calls the API and returns the parsed JSON body (undefined when there is none). Throws ApiError for any non-2xx response
 * and for a network failure. Same-origin credentials, so the browser sends the refresh cookie to /v1/auth
 * (ADR 0013).
 */
export async function apiFetch<T = unknown>(path: ApiPath, request: ApiRequest = {}): Promise<T> {
  const headers = new Headers({ Accept: "application/json", ...request.headers });
  if (request.body !== undefined) headers.set("Content-Type", "application/json");
  if (request.accessToken) headers.set("Authorization", `Bearer ${request.accessToken}`);

  let response: Response;
  try {
    response = await fetch(path, {
      method: request.method ?? "GET",
      headers,
      body: request.body === undefined ? undefined : JSON.stringify(request.body),
      credentials: "same-origin",
      signal: request.signal,
    });
  } catch (cause) {
    if (request.signal?.aborted) throw cause;
    throw new ApiError({
      status: 0, code: "NetworkError", message: "No response from the server.", rule: null, details: {},
      correlationId: undefined,
    });
  }

  if (response.ok) {
    // No body (204, or forgotPassword's 202): undefined.
    const text = await response.text();
    return (text === "" ? undefined : JSON.parse(text)) as T;
  }
  throw await toApiError(response);
}

async function toApiError(response: Response): Promise<ApiError> {
  const correlationId = response.headers.get("X-Correlation-Id") ?? undefined;
  const body = await readErrorBody(response);
  if (body) {
    return new ApiError({
      status: response.status, code: body.code, message: body.message, rule: body.rule ?? null,
      details: (body.details ?? {}) as Record<string, unknown>, correlationId,
    });
  }
  // Not the shared Error shape, e.g. an HTML page from a proxy (specs/09-ui.md "Errors").
  return new ApiError({
    status: response.status,
    code: response.status >= 500 ? "InternalError" : "RequestRejected",
    message: `Unexpected response (HTTP ${response.status}).`,
    rule: null,
    details: {},
    correlationId,
  });
}

async function readErrorBody(response: Response): Promise<ErrorBody | undefined> {
  if (!response.headers.get("Content-Type")?.includes("json")) return undefined;
  try {
    const body: unknown = await response.json();
    if (body && typeof body === "object" && typeof (body as ErrorBody).code === "string"
      && typeof (body as ErrorBody).message === "string") {
      return body as ErrorBody;
    }
  } catch {
    // Not JSON after all.
  }
  return undefined;
}
