import type { RouteObject } from "react-router";
import { useRouteError } from "react-router";
import { healthRoute } from "@/pages/health";
import { notFoundRoute } from "@/pages/not-found";
import { ErrorMessage } from "@/shared/ui/ErrorMessage";
import { Page } from "@/shared/ui/Page";

/** Shown for an error no screen handled (a screen's own errorElement comes first). */
function RootError() {
  return (
    <Page title="Something went wrong">
      <ErrorMessage error={useRouteError()} />
    </Page>
  );
}

/** The root route around `children`: catches unhandled errors and ends with the not-found page (T33). */
export function rootRoute(children: RouteObject[]): RouteObject {
  return { errorElement: <RootError />, children: [...children, notFoundRoute] };
}

/** Every route of the app (React Router 8, library data mode; T33). Screen tasks add theirs here. */
export const routes: RouteObject[] = [rootRoute([healthRoute])];
