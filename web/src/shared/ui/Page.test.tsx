import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { expectNoAxeViolations } from "../../test/axe";
import { Page } from "./Page";

// T33: every page names itself in the document title (WCAG 2.4.2) and in its one level-1 heading.
describe("Page", () => {
  it("sets the document title and the heading from its title", () => {
    render(<Page title="Page not found"><p>Body</p></Page>);

    expect(document.title).toBe("Page not found · Booking platform");
    expect(screen.getByRole("heading", { level: 1, name: "Page not found" })).toBeInTheDocument();
    expect(screen.getByRole("main")).toHaveTextContent("Body");
  });

  it("has no accessibility violations", async () => {
    const { container } = render(<Page title="Title"><p>Body</p></Page>);

    await expectNoAxeViolations(container);
  });
});
