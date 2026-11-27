import { describe, expect, it } from "vitest";
import { call, json, mockFetch } from "../../test/fetch";
import { ApiError, apiFetch } from "./client";

// T33: the API client. Relative /v1 URLs (one origin with the API, ADR 0012), JSON in and out, the shared Error
// shape (specs/04-api.openapi.yaml) turned into ApiError with the X-Correlation-Id kept for error messages.
describe("apiFetch", () => {
  it("calls the relative URL with the same-origin cookie policy and returns the JSON body", async () => {
    const fetch = mockFetch(json(200, { name: "Cut & Dry" }));

    const data = await apiFetch<{ name: string }>("/v1/businesses/b1");

    expect(data).toEqual({ name: "Cut & Dry" });
    const [url, init] = call(fetch);
    expect(url).toBe("/v1/businesses/b1");
    expect(init.credentials).toBe("same-origin"); // the refresh cookie goes with /v1/auth calls (ADR 0013)
    expect(new Headers(init.headers).get("Accept")).toBe("application/json");
  });

  it("sends a JSON body and the access token", async () => {
    const fetch = mockFetch(json(201, { id: "b1" }));

    await apiFetch("/v1/bookings", { method: "POST", body: { serviceId: "s1" }, accessToken: "token-1" });

    const [, init] = call(fetch);
    expect(init.method).toBe("POST");
    expect(init.body).toBe(JSON.stringify({ serviceId: "s1" }));
    const headers = new Headers(init.headers);
    expect(headers.get("Content-Type")).toBe("application/json");
    expect(headers.get("Authorization")).toBe("Bearer token-1");
  });

  it("sends no body or Content-Type when there is no body, and returns undefined for 204", async () => {
    const fetch = mockFetch(new Response(null, { status: 204 }));

    expect(await apiFetch("/v1/auth/logout", { method: "POST" })).toBeUndefined();

    const [, init] = call(fetch);
    expect(init.body).toBeUndefined();
    expect(new Headers(init.headers).has("Content-Type")).toBe(false);
  });

  it("turns an Error response into ApiError with its status, code, details and correlation ID", async () => {
    mockFetch(
      json(409, { code: "SlotConflict", message: "The slot is taken.", rule: "R7", details: { staffId: "x" } },
        { "X-Correlation-Id": "corr-1" }),
    );

    const error = await apiFetch("/v1/bookings", { method: "POST", body: {} }).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({
      status: 409, code: "SlotConflict", message: "The slot is taken.", rule: "R7", details: { staffId: "x" },
      correlationId: "corr-1",
    });
  });

  it.each([
    [502, "InternalError"], // e.g. the proxy or CloudFront answering with HTML
    [404, "RequestRejected"],
  ])("treats a %i without the Error shape as %s, keeping the correlation ID if any", async (status, code) => {
    mockFetch(new Response("<html>Bad gateway</html>",
      { status, headers: { "Content-Type": "text/html", "X-Correlation-Id": "corr-2" } }));

    const error = await apiFetch("/v1/me").catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status, code, correlationId: "corr-2", details: {} });
  });

  it("turns a network failure into ApiError NetworkError with status 0", async () => {
    mockFetch(new TypeError("Failed to fetch"));

    const error = await apiFetch("/v1/me").catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 0, code: "NetworkError", correlationId: undefined });
  });
});
