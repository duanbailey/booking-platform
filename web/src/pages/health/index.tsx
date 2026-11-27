import type { LoaderFunctionArgs, RouteObject } from "react-router";
import { useLoaderData, useRouteError } from "react-router";
import { apiFetch } from "@/shared/api/client";
import { ErrorMessage } from "@/shared/ui/ErrorMessage";
import { Page } from "@/shared/ui/Page";

// Development placeholder (T33): shows the API's health through the dev proxy, which forwards /health. The login
// screen replaces it at / in T34; production routes only /v1/* to the API (ADR 0012).

interface Health {
  status: string;
}

// Every loader passes the request's signal, so React Router cancels the call when the user navigates away.
async function healthLoader({ request }: LoaderFunctionArgs): Promise<Health> {
  return apiFetch<Health>("/health", { signal: request.signal });
}

function HealthPage() {
  const health = useLoaderData<Health>();
  return (
    <Page title="API status">
      <p>Status: {health.status}</p>
    </Page>
  );
}

function HealthError() {
  return (
    <Page title="API status">
      <ErrorMessage error={useRouteError()} />
    </Page>
  );
}

export const healthRoute: RouteObject = {
  path: "/",
  loader: healthLoader,
  element: <HealthPage />,
  errorElement: <HealthError />,
};
