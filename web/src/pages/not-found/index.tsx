import type { RouteObject } from "react-router";
import { Page } from "@/shared/ui/Page";

// T33: any path no route matches. The wording is NotFound's (specs/09-ui.md "Errors").
function NotFoundPage() {
  return (
    <Page title="Page not found">
      <p>We couldn't find what you were looking for.</p>
    </Page>
  );
}

export const notFoundRoute: RouteObject = { path: "*", element: <NotFoundPage /> };
