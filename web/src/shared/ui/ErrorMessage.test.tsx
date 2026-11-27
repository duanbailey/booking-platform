import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { expectNoAxeViolations } from "../../test/axe";
import { ApiError } from "../api/client";
import { ErrorMessage } from "./ErrorMessage";

// T33: the generic error display (specs/09-ui.md "Errors"). Screens handle their own business-rule codes; this
// covers what every screen shares. The wording is in specs/09-ui.md.
const error = (status: number, code: ApiError["code"], message = "API message.", correlationId?: string) =>
  new ApiError({ status, code, message, rule: null, details: {}, correlationId });

describe("ErrorMessage", () => {
  it.each([
    [error(0, "NetworkError"), "We couldn't reach the server. Check your connection and try again."],
    [error(404, "NotFound"), "We couldn't find what you were looking for."],
    [error(403, "Forbidden"), "You don't have permission to do that."],
    [error(429, "RequestRejected"), "Too many attempts. Please wait a minute and try again."],
  ])("shows the shared wording for %s", (e, text) => {
    render(<ErrorMessage error={e} />);

    expect(screen.getByRole("alert")).toHaveTextContent(text);
  });

  it("shows a retry message with the correlation ID to quote for an InternalError", () => {
    render(<ErrorMessage error={error(500, "InternalError", "An unexpected error occurred.", "corr-9")} />);

    const alert = screen.getByRole("alert");
    expect(alert).toHaveTextContent("Something went wrong on our side. Please try again.");
    expect(alert).toHaveTextContent("If it keeps happening, quote reference corr-9.");
  });

  it("shows the correlation ID for a request refused for another reason (e.g. 415)", () => {
    render(<ErrorMessage error={error(415, "RequestRejected", "Unsupported media type.", "corr-3")} />);

    expect(screen.getByRole("alert")).toHaveTextContent("Something went wrong on our side. Please try again.");
    expect(screen.getByRole("alert")).toHaveTextContent("quote reference corr-3");
  });

  it("shows the API's own message for a code the screen didn't handle", () => {
    render(<ErrorMessage error={error(422, "MinimumNotice", "The booking starts within the minimum notice period.")} />);

    expect(screen.getByRole("alert")).toHaveTextContent("The booking starts within the minimum notice period.");
  });

  it("treats anything that isn't an ApiError as an unexpected error, without a reference", () => {
    render(<ErrorMessage error={new Error("boom")} />);

    expect(screen.getByRole("alert")).toHaveTextContent("Something went wrong on our side. Please try again.");
    expect(screen.getByRole("alert")).not.toHaveTextContent("reference");
  });

  it("has no accessibility violations", async () => {
    const { container } = render(<ErrorMessage error={error(500, "InternalError", "x", "corr-1")} />);

    await expectNoAxeViolations(container);
  });
});
