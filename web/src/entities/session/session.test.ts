import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/shared/api/client";
import { apiError, me, mockApi, token, type Answer } from "../../test/api";
import { rolesOf, session, type Me } from "./index";

// T34: the access token lives in memory only and the refresh token in an HttpOnly cookie the app never reads
// (ADR 0013). Refreshes run one at a time across tabs (Web Locks), since two tabs sending the same cookie would
// look like reuse and revoke the login (ADR 0010).

const REFRESH = "POST /v1/auth/refresh";
const ME = "GET /v1/me";
const LOGOUT = "POST /v1/auth/logout";

/** Logs in with access token "a1" through start(). */
async function loggedIn(extra: Record<string, Answer[]> = {}) {
  const api = mockApi({ [REFRESH]: [token("a1")], [ME]: [me()], ...extra });
  await session.start();
  return api;
}

afterEach(() => {
  Reflect.deleteProperty(navigator, "locks");
});

describe("session.start", () => {
  it("uses the refresh cookie to log in on load, then loads the user", async () => {
    const api = mockApi({ [REFRESH]: [token("a1")], [ME]: [me({ owner: true })] });

    await session.start();

    expect(session.getState()).toMatchObject({ status: "loggedIn", user: { ownedBusinessId: "b1" } });
    const refresh = api.calls(REFRESH)[0]!;
    expect(refresh.credentials).toBe("same-origin");
    expect(refresh.body).toBeUndefined();
    expect(api.calls(ME)[0]!.headers.get("Authorization")).toBe("Bearer a1");
  });

  it("is logged out when the refresh is refused (no cookie, or a dead one)", async () => {
    const api = mockApi({ [REFRESH]: [apiError(401, "InvalidRefreshToken")] });

    await session.start();

    expect(session.getState()).toMatchObject({ status: "loggedOut", user: null, ended: false });
    expect(api.calls(ME)).toHaveLength(0);
  });

  it("fails on any other error, and tries again on the next call", async () => {
    mockApi({ [REFRESH]: [new TypeError("Failed to fetch"), token("a1")], [ME]: [me()] });

    await expect(session.start()).rejects.toMatchObject({ code: "NetworkError" });
    expect(session.getState().status).toBe("unknown");

    await session.start();
    expect(session.getState().status).toBe("loggedIn");
  });

  it("refreshes once however many callers wait for it", async () => {
    const api = mockApi({ [REFRESH]: [token("a1")], [ME]: [me()] });

    await Promise.all([session.start(), session.start(), session.start()]);
    await session.start();

    expect(api.calls(REFRESH)).toHaveLength(1);
  });

  it("refreshes inside the cross-tab Web Lock", async () => {
    const order: string[] = [];
    Object.defineProperty(navigator, "locks", {
      configurable: true,
      value: {
        request: vi.fn(async (name: string, callback: () => Promise<unknown>) => {
          order.push(`lock ${name}`);
          const result = await callback();
          order.push("unlock");
          return result;
        }),
      },
    });
    mockApi({
      [REFRESH]: [() => { order.push("refresh"); return token("a1"); }],
      [ME]: [() => { order.push("me"); return me(); }],
    });

    await session.start();

    expect(order).toEqual(["lock booking-platform-refresh", "refresh", "unlock", "me"]);
  });
});

describe("session.logIn", () => {
  it("keeps the access token and loads the user", async () => {
    const api = mockApi({ [REFRESH]: [apiError(401, "InvalidRefreshToken")], [ME]: [me({ staff: true })] });
    await session.start();

    await session.logIn({ accessToken: "a9", tokenType: "Bearer", expiresAt: "", refreshTokenExpiresAt: "" });

    expect(session.getState()).toMatchObject({ status: "loggedIn", user: { staff: [{ staffId: "s1" }] } });
    expect(api.calls(ME)[0]!.headers.get("Authorization")).toBe("Bearer a9");
  });
});

describe("session.authFetch", () => {
  it("sends the access token", async () => {
    const api = await loggedIn({ "GET /v1/me/bookings": [new Response("{}", { status: 200 })] });

    await session.authFetch("/v1/me/bookings");

    expect(api.calls("GET /v1/me/bookings")[0]!.headers.get("Authorization")).toBe("Bearer a1");
  });

  it("waits for the session to start before calling (a page's loader runs alongside the login check)", async () => {
    const api = mockApi({ [REFRESH]: [token("a1")], [ME]: [me()], "GET /v1/a": [new Response("1", { status: 200 })] });

    await expect(session.authFetch("/v1/a")).resolves.toBe(1);

    expect(api.calls("GET /v1/a")[0]!.headers.get("Authorization")).toBe("Bearer a1");
    expect(api.calls(REFRESH)).toHaveLength(1);
  });

  it("refreshes once on 401 Unauthenticated and retries with the new token", async () => {
    const api = await loggedIn({
      "GET /v1/me/bookings": [apiError(401, "Unauthenticated"), new Response('{"items":[]}', { status: 200 })],
    });
    api.answer(REFRESH, token("a2"));

    await expect(session.authFetch("/v1/me/bookings")).resolves.toEqual({ items: [] });

    const calls = api.calls("GET /v1/me/bookings");
    expect(calls.map((c) => c.headers.get("Authorization"))).toEqual(["Bearer a1", "Bearer a2"]);
    expect(api.calls(REFRESH)).toHaveLength(2);
  });

  it("shares one refresh between calls in the tab that get 401 together", async () => {
    const api = await loggedIn({
      "GET /v1/a": [apiError(401, "Unauthenticated"), new Response("1", { status: 200 })],
      "GET /v1/b": [apiError(401, "Unauthenticated"), new Response("2", { status: 200 })],
    });
    api.answer(REFRESH, token("a2"));

    await expect(Promise.all([session.authFetch("/v1/a"), session.authFetch("/v1/b")])).resolves.toEqual([1, 2]);

    expect(api.calls(REFRESH)).toHaveLength(2); // start + one shared refresh
  });

  it("retries without refreshing again when another call refreshed after this one was sent", async () => {
    let answerB!: (response: Response) => void;
    const api = await loggedIn({
      "GET /v1/a": [apiError(401, "Unauthenticated"), new Response("1", { status: 200 })],
      "GET /v1/b": [() => new Promise<Response>((resolve) => { answerB = resolve; }), new Response("2", { status: 200 })],
    });
    api.answer(REFRESH, token("a2"));
    const b = session.authFetch("/v1/b"); // sent with a1; its 401 arrives only after a's refresh is done
    await expect(session.authFetch("/v1/a")).resolves.toBe(1);

    answerB(apiError(401, "Unauthenticated"));

    await expect(b).resolves.toBe(2);
    expect(api.calls(REFRESH)).toHaveLength(2); // start + a's refresh only
    expect(api.calls("GET /v1/b").map((c) => c.headers.get("Authorization"))).toEqual(["Bearer a1", "Bearer a2"]);
  });

  it("ends the session when the refresh is refused", async () => {
    const api = await loggedIn({ "GET /v1/a": [apiError(401, "Unauthenticated")] });
    api.answer(REFRESH, apiError(401, "InvalidRefreshToken"));

    await expect(session.authFetch("/v1/a")).rejects.toMatchObject({ code: "Unauthenticated" });

    expect(session.getState()).toMatchObject({ status: "loggedOut", user: null, ended: true });
  });

  it("refreshes only once: a second 401 after the retry ends the session", async () => {
    const api = await loggedIn({ "GET /v1/a": [apiError(401, "Unauthenticated"), apiError(401, "Unauthenticated")] });
    api.answer(REFRESH, token("a2"));

    await expect(session.authFetch("/v1/a")).rejects.toMatchObject({ code: "Unauthenticated" });

    expect(api.calls(REFRESH)).toHaveLength(2);
    expect(session.getState()).toMatchObject({ status: "loggedOut", ended: true });
  });

  it("passes other errors through without refreshing", async () => {
    const api = await loggedIn({ "GET /v1/a": [apiError(403, "Forbidden")] });

    await expect(session.authFetch("/v1/a")).rejects.toBeInstanceOf(ApiError);

    expect(api.calls(REFRESH)).toHaveLength(1);
    expect(session.getState().status).toBe("loggedIn");
  });

  it("keeps the session when the refresh fails for another reason (e.g. no network)", async () => {
    const api = await loggedIn({ "GET /v1/a": [apiError(401, "Unauthenticated")] });
    api.answer(REFRESH, new TypeError("Failed to fetch"));

    await expect(session.authFetch("/v1/a")).rejects.toMatchObject({ code: "NetworkError" });

    expect(session.getState().status).toBe("loggedIn");
  });
});

describe("session.logOut", () => {
  it("logs out, forgets the token and tells the app's other tabs", async () => {
    const api = await loggedIn({ [LOGOUT]: [new Response(null, { status: 204 })] });
    const otherTab = new BroadcastChannel("booking-platform-session");
    const heard = new Promise((resolve) => { otherTab.onmessage = (e) => resolve(e.data); });

    await session.logOut();

    expect(api.calls(LOGOUT)[0]!.credentials).toBe("same-origin");
    expect(session.getState()).toMatchObject({ status: "loggedOut", user: null, ended: false });
    await expect(heard).resolves.toBe("loggedOut");
    otherTab.close();
  });

  it("stays logged in when the logout call fails", async () => {
    await loggedIn({ [LOGOUT]: [new TypeError("Failed to fetch")] });

    await expect(session.logOut()).rejects.toMatchObject({ code: "NetworkError" });

    expect(session.getState().status).toBe("loggedIn");
  });

  it("logs out when another tab logs out", async () => {
    await loggedIn();
    const otherTab = new BroadcastChannel("booking-platform-session");

    otherTab.postMessage("loggedOut");

    await vi.waitFor(() => expect(session.getState()).toMatchObject({ status: "loggedOut", ended: false }));
    otherTab.close();
  });
});

describe("rolesOf", () => {
  const user = (over: Partial<Me>): Me => ({
    id: "u1", email: "e@example.test", name: "N", timeZone: "Etc/UTC", isAdmin: false, ownedBusinessId: null,
    staff: [], ...over,
  });

  it.each<[string, Partial<Me>, ReturnType<typeof rolesOf>]>([
    ["a user with no other role is a customer", {}, { admin: false, owner: false, staff: false, customer: true }],
    ["an owner who is also staff is a customer too", { ownedBusinessId: "b1", staff: [{ staffId: "s1", businessId: "b1" }] },
      { admin: false, owner: true, staff: true, customer: true }],
    ["an admin is not a customer", { isAdmin: true }, { admin: true, owner: false, staff: false, customer: false }],
    ["an admin who owns a business is an owner, not a customer", { isAdmin: true, ownedBusinessId: "b1" },
      { admin: true, owner: true, staff: false, customer: false }],
  ])("%s", (_name, over, roles) => {
    expect(rolesOf(user(over))).toEqual(roles);
  });
});
