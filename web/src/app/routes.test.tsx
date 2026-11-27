import { render, screen } from "@testing-library/react";
import type { RouteObject } from "react-router";
import { createMemoryRouter, RouterProvider } from "react-router";
import { describe, expect, it } from "vitest";
import { expectNoAxeViolations } from "../test/axe";
import { rootRoute } from "./routes";

// T33: the root route catches what no screen handles: an unknown path, and an error a screen didn't catch.
function renderAt(path: string, children: RouteObject[] = []) {
  const router = createMemoryRouter([rootRoute(children)], { initialEntries: [path] });
  return render(<RouterProvider router={router} />);
}

describe("root route", () => {
  it("shows the not-found page for a path no route matches", async () => {
    renderAt("/no-such-page");

    expect(await screen.findByRole("heading", { level: 1, name: "Page not found" })).toBeInTheDocument();
    expect(screen.getByText("We couldn't find what you were looking for.")).toBeInTheDocument();
    expect(document.title).toBe("Page not found · Booking platform");
  });

  it("shows the shared error message for an error a screen doesn't handle", async () => {
    const boom = { path: "/boom", loader: () => { throw new Error("boom"); }, element: <p>Never</p> };

    renderAt("/boom", [boom]);

    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on our side. Please try again.");
    expect(screen.getByRole("heading", { level: 1, name: "Something went wrong" })).toBeInTheDocument();
  });

  it("has no accessibility violations on the not-found page", async () => {
    const { container } = renderAt("/no-such-page");
    await screen.findByRole("heading", { level: 1 });

    await expectNoAxeViolations(container);
  });
});
