import { useEffect, useRef } from "react";
import type { LoaderFunctionArgs } from "react-router";
import { Outlet, redirect, useLocation, useNavigate, useRouteError } from "react-router";
import { session, useSession } from "@/entities/session";
import { safeNext, withNext } from "@/shared/lib/safeNext";
import { ErrorMessage } from "@/shared/ui/ErrorMessage";
import { Page } from "@/shared/ui/Page";
import { AppHeader } from "@/widgets/app-header";

/** Loader for pages that need login: a visitor goes to log in, with ?next= back to this page (T34). */
export async function requireUser({ request }: LoaderFunctionArgs) {
  await session.start();
  if (session.getState().status !== "loggedIn") {
    const url = new URL(request.url);
    throw redirect(withNext("/", url.pathname + url.search));
  }
  return null;
}

/** Loader for login and sign-up: a logged-in user goes on to ?next= or home (T34). */
export async function guestOnly({ request }: LoaderFunctionArgs) {
  await session.start();
  if (session.getState().status === "loggedIn") {
    throw redirect(safeNext(new URL(request.url).searchParams.get("next")) ?? "/home");
  }
  return null;
}

/**
 * Every page: moves focus to the new page's heading when the path changes, so the page is announced (T34). Not on
 * a form's answer on the same path, where the form moves focus to its first invalid field.
 */
export function RootLayout() {
  const { pathname } = useLocation();
  const shown = useRef<string | null>(null);
  useEffect(() => {
    if (shown.current === pathname) return;
    if (shown.current !== null) document.querySelector<HTMLElement>("main h1")?.focus();
    shown.current = pathname;
  }, [pathname]);
  return <Outlet />;
}

/** While the app finds out on load whether the user is logged in. */
export function Loading() {
  return (
    <main>
      <p role="status">Loading…</p>
    </main>
  );
}

/** Shown for an error no screen handled (a screen's own errorElement comes first). */
export function RootError() {
  return (
    <Page title="Something went wrong">
      <ErrorMessage error={useRouteError()} />
    </Page>
  );
}

/**
 * Logged-in pages: the header, and back to login when the session ends: after a logout here or in another tab,
 * or, saying so, when a refresh is refused (specs/09-ui.md "Errors", Unauthenticated).
 */
export function UserLayout() {
  const { status, ended } = useSession();
  const navigate = useNavigate();
  const { pathname, search } = useLocation();
  useEffect(() => {
    if (status !== "loggedOut") return;
    if (ended) void navigate(withNext("/", pathname + search), { replace: true, state: { sessionEnded: true } });
    else void navigate("/", { replace: true });
  }, [status, ended, navigate, pathname, search]);

  if (status !== "loggedIn") return null;
  return (
    <>
      <AppHeader />
      <Outlet />
    </>
  );
}
