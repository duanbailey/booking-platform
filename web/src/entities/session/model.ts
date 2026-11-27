import { apiFetch, ApiError, type ApiPath, type ApiRequest } from "@/shared/api/client";
import type { components } from "@/shared/api/schema";

export type Me = components["schemas"]["Me"];
export type AuthToken = components["schemas"]["AuthToken"];

export interface SessionState {
  /** "unknown" until the first refresh on load has answered. */
  status: "unknown" | "loggedIn" | "loggedOut";
  user: Me | null;
  /** The session ended because a refresh was refused, not because the user logged out (T34). */
  ended: boolean;
}

/** One refresh at a time across the app's tabs (T34): two tabs sending the same cookie would look like reuse and revoke the login (ADR 0010). */
const refreshLock = "booking-platform-refresh";
/** Tells the app's other tabs about a logout (T34). */
const channelName = "booking-platform-session";

const initial: SessionState = { status: "unknown", user: null, ended: false };

let state = initial;
/** The access token, in memory only (ADR 0013). */
let accessToken: string | null = null;
let starting: Promise<void> | null = null;
let refreshing: Promise<void> | null = null;
let channel: BroadcastChannel | null = null;
const listeners = new Set<() => void>();

function setState(next: SessionState) {
  state = next;
  listeners.forEach((listener) => listener());
}

function openChannel() {
  if (channel || typeof BroadcastChannel === "undefined") return;
  channel = new BroadcastChannel(channelName);
  channel.onmessage = (event: MessageEvent) => {
    if (event.data === "loggedOut") forget(false);
  };
}

function forget(ended: boolean) {
  accessToken = null;
  setState({ status: "loggedOut", user: null, ended });
}

function withRefreshLock<T>(work: () => Promise<T>): Promise<T> {
  const locks = typeof navigator === "undefined" ? undefined : navigator.locks;
  return locks ? (locks.request(refreshLock, work) as Promise<T>) : work();
}

/** Calls refreshToken under the cross-tab lock; the browser sends the cookie (ADR 0013). */
async function refreshAccessToken(): Promise<void> {
  const tokens = await withRefreshLock(() => apiFetch<AuthToken>("/v1/auth/refresh", { method: "POST" }));
  accessToken = tokens.accessToken;
}

/** Refreshes once for every caller in this tab that needs it at the same moment. */
function sharedRefresh(): Promise<void> {
  refreshing ??= refreshAccessToken().finally(() => { refreshing = null; });
  return refreshing;
}

async function loadUser(): Promise<void> {
  const user = await apiFetch<Me>("/v1/me", { accessToken: accessToken ?? undefined });
  openChannel();
  setState({ status: "loggedIn", user, ended: false });
}

const isRefused = (error: unknown) => error instanceof ApiError && error.code === "InvalidRefreshToken";

export const session = {
  getState: (): SessionState => state,

  subscribe(listener: () => void): () => void {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },

  /**
   * Finds out on load whether the user is logged in: refreshToken with the cookie, then getMe (specs/09-ui.md).
   * Runs once; a refused refresh means logged out, and any other failure rejects so the next call tries again.
   */
  start(): Promise<void> {
    starting ??= (async () => {
      try {
        await sharedRefresh();
      } catch (error) {
        if (isRefused(error)) {
          forget(false);
          return;
        }
        throw error;
      }
      await loadUser();
    })().catch((error: unknown) => {
      starting = null;
      throw error;
    });
    return starting;
  },

  /**
   * Starts a session from a response that logs in (login, sign-up, accept-invite, reset-password), replacing any
   * current one. The session counts as started, so no refresh on load follows.
   */
  async logIn(tokens: AuthToken): Promise<void> {
    accessToken = tokens.accessToken;
    await loadUser();
    starting = Promise.resolve();
  },

  /** Keeps the new access token from a response that renews the login (change-password, T35). */
  setTokens(tokens: AuthToken): void {
    accessToken = tokens.accessToken;
  },

  /** Replaces the user after updateMe (T35). */
  setUser(user: Me): void {
    if (state.status === "loggedIn") setState({ ...state, user });
  },

  /**
   * Calls the API with the access token. On 401 Unauthenticated it refreshes once (shared with other calls in the
   * tab) and retries; if the refresh is refused or the retry gets 401 again, the session ends (specs/09-ui.md).
   */
  async authFetch<T = unknown>(path: ApiPath, request: ApiRequest = {}): Promise<T> {
    const sentWith = accessToken;
    try {
      return await apiFetch<T>(path, { ...request, accessToken: sentWith ?? undefined });
    } catch (error) {
      if (!(error instanceof ApiError) || error.code !== "Unauthenticated") throw error;
      try {
        // Another call in this tab may already have refreshed since this one was sent.
        if (accessToken === sentWith) await sharedRefresh();
      } catch (refreshError) {
        if (isRefused(refreshError)) forget(true);
        throw isRefused(refreshError) ? error : refreshError;
      }
      try {
        return await apiFetch<T>(path, { ...request, accessToken: accessToken ?? undefined });
      } catch (retryError) {
        if (retryError instanceof ApiError && retryError.code === "Unauthenticated") forget(true);
        throw retryError;
      }
    }
  },

  /** Logs out (the API clears the cookie) and tells the other tabs. If the call fails, the user stays logged in. */
  async logOut(): Promise<void> {
    await apiFetch("/v1/auth/logout", { method: "POST" });
    forget(false);
    channel?.postMessage("loggedOut");
  },

  /** Back to the state on load (tests). */
  reset(): void {
    channel?.close();
    channel = null;
    accessToken = null;
    starting = null;
    refreshing = null;
    state = initial;
    listeners.clear();
  },
};

export interface Roles {
  admin: boolean;
  owner: boolean;
  staff: boolean;
  customer: boolean;
}

/** The roles a user holds; every user who isn't an admin is a customer (specs/09-ui.md, decided 2026-11-27). */
export function rolesOf(user: Me): Roles {
  return {
    admin: user.isAdmin,
    owner: user.ownedBusinessId !== null,
    staff: user.staff.length > 0,
    customer: !user.isAdmin,
  };
}
