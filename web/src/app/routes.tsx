import type { RouteObject } from "react-router";
import { acceptInviteRoute } from "@/pages/accept-invite";
import { changePasswordRoute } from "@/pages/change-password";
import { forgotPasswordRoute } from "@/pages/forgot-password";
import { homeRoute } from "@/pages/home";
import { loginRoute } from "@/pages/login";
import { notFoundRoute } from "@/pages/not-found";
import { profileRoute } from "@/pages/profile";
import { resetPasswordRoute } from "@/pages/reset-password";
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
    // Anyone: the emailed links work whether or not someone is logged in (T35).
    forgotPasswordRoute,
    acceptInviteRoute,
    resetPasswordRoute,
    // Logged-in users (T34).
    { loader: requireUser, element: <UserLayout />, children: [homeRoute, profileRoute, changePasswordRoute] },
  ]),
];
