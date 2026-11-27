import { render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router";
import { describe, expect, it, vi } from "vitest";
import { expectNoAxeViolations } from "../../test/axe";
import { call, json, mockFetch } from "../../test/fetch";
import { healthRoute } from "./index";

// T33: the development placeholder at / shows the API's health through the dev proxy; the login screen replaces
// it in T34 (decided 2026-11-26).
function renderApp() {
  const router = createMemoryRouter([healthRoute, { path: "/other", element: <p>Other page</p> }], {
    initialEntries: ["/"],
  });
  return { router, ...render(<RouterProvider router={router} />) };
}

describe("HealthPage", () => {
  it("shows the API's status from /health", async () => {
    const fetch = mockFetch(json(200, { status: "ok" }));

    renderApp();

    expect(await screen.findByText("Status: ok")).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 1, name: "API status" })).toBeInTheDocument();
    expect(document.title).toBe("API status · Booking platform");
    expect(call(fetch)[0]).toBe("/health");
  });

  it("shows the shared error message when the API can't be reached", async () => {
    mockFetch(new TypeError("Failed to fetch"));

    renderApp();

    expect(await screen.findByRole("alert")).toHaveTextContent("We couldn't reach the server.");
  });

  // The pattern for every loader: the request is cancelled when the user navigates away before it finishes.
  it("cancels the API call when the user navigates away before it answers", async () => {
    let signal: AbortSignal | undefined;
    vi.stubGlobal("fetch", vi.fn((_url: string, init: RequestInit) => {
      signal = init.signal ?? undefined;
      return new Promise<Response>(() => {});
    }));
    const { router } = renderApp();
    await waitFor(() => expect(signal).toBeDefined());
    expect(signal?.aborted).toBe(false);

    await router.navigate("/other");

    expect(await screen.findByText("Other page")).toBeInTheDocument();
    expect(signal?.aborted).toBe(true);
  });

  it("has no accessibility violations", async () => {
    mockFetch(json(200, { status: "ok" }));
    const { container } = renderApp();
    await screen.findByText("Status: ok");

    await expectNoAxeViolations(container);
  });
});
