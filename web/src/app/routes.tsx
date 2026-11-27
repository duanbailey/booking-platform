import type { RouteObject } from "react-router";
import { homeRoute } from "@/pages/home";
import { loginRoute } from "@/pages/login";
import { notFoundRoute } from "@/pages/not-found";
import { signUpRoute } from "@/pages/sign-up";
import { guestOnly, Loading, requireUser, RootError, RootLayout, UserLayout } from "./layouts";

/** The root route around `children`: focus on navigation, unhandled errors, and the not-found page last (T33). */
export function rootRoute(children: RouteObject[]): RouteObject {
  return {
    element: <RootLayout />,
    errorElement: <RootError />,
    HydrateFallback: Loading,
    children: [...children, notFoundRoute],
  };
}

/** Every route of the app (React Router 8, library data mode). Screen tasks add theirs to the right group. */
export const routes: RouteObject[] = [
  rootRoute([
    // Visitors only (T34).
    { loader: guestOnly, children: [loginRoute, signUpRoute] },
    // Logged-in users (T34).
    { loader: requireUser, element: <UserLayout />, children: [homeRoute] },
  ]),
];
