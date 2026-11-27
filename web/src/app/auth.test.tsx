import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router";
import { describe, expect, it } from "vitest";
import { session } from "@/entities/session";
import { expectNoAxeViolations } from "../test/axe";
import { apiError, me, mockApi, pending, token } from "../test/api";
import { routes } from "./routes";

// T34: session, login and sign-up. Stories "Owner can log in", "Staff can log in", "Customer can log in",
// "Customer can sign up" (specs/02-requirements.md); login never reveals whether an account exists
// (specs/07-nfr.md); ADR 0013; screens and wording in specs/09-ui.md.

const REFRESH = "POST /v1/auth/refresh";
const LOGIN = "POST /v1/auth/login";
const REGISTER = "POST /v1/auth/register";
const LOGOUT = "POST /v1/auth/logout";
const ME = "GET /v1/me";
const loggedOut = () => apiError(401, "InvalidRefreshToken");

function renderApp(path = "/") {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const view = render(<RouterProvider router={router} />);
  const url = () => router.state.location.pathname + router.state.location.search;
  return { router, url, ...view };
}

async function logIn(email = "olive@example.test", password = "olive password 1") {
  const user = userEvent.setup();
  await user.type(await screen.findByLabelText("Email"), email);
  await user.type(screen.getByLabelText("Password"), password);
  await user.click(screen.getByRole("button", { name: "Log in" }));
  return user;
}

describe("login", () => {
  it.each([
    ["owner", { owner: true }, "Owner"],
    ["staff member", { staff: true }, "Staff"],
    ["customer", {}, "Customer"],
  ])("takes a %s with valid credentials to their account page", async (_who, roles, section) => {
    const api = mockApi({ [REFRESH]: [loggedOut()], [LOGIN]: [token("a1")], [ME]: [me(roles)] });
    const { url } = renderApp("/");

    expect(await screen.findByRole("heading", { level: 1, name: "Log in" })).toBeInTheDocument();
    await logIn();

    expect(await screen.findByRole("heading", { level: 2, name: section })).toBeInTheDocument();
    expect(url()).toBe("/home");
    expect(api.calls(LOGIN)[0]!.body).toEqual({ email: "olive@example.test", password: "olive password 1" });
    expect(screen.getByRole("banner")).toHaveTextContent("Olive Owner");
  });

  it("shows the sections of every role the user holds; an admin is not a customer", async () => {
    mockApi({ [REFRESH]: [token("a1")], [ME]: [me({ admin: true, owner: true })] });
    renderApp("/home");

    expect(await screen.findByRole("heading", { level: 2, name: "Admin" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 2, name: "Owner" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { level: 2, name: "Customer" })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { level: 2, name: "Staff" })).not.toBeInTheDocument();
  });

  it("says the same for a wrong password and an unknown email, without saying which", async () => {
    mockApi({ [REFRESH]: [loggedOut()], [LOGIN]: [apiError(401, "InvalidCredentials")] });
    const { url } = renderApp("/");

    await logIn();

    expect(await screen.findByRole("alert")).toHaveTextContent("The email or password is incorrect.");
    expect(url()).toBe("/");
    expect(screen.getByLabelText("Email")).toHaveValue("olive@example.test");
  });

  it("shows each invalid field next to it and moves focus to the first", async () => {
    mockApi({
      [REFRESH]: [loggedOut()],
      [LOGIN]: [apiError(400, "ValidationFailed", { fields: { email: "Is required.", password: "Is required." } })],
    });
    renderApp("/");
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Log in" }));

    const email = screen.getByLabelText("Email");
    await waitFor(() => expect(email).toHaveAttribute("aria-invalid", "true"));
    expect(email).toHaveAccessibleDescription("Is required.");
    expect(screen.getByLabelText("Password")).toHaveAccessibleDescription("Is required.");
    expect(email).toHaveFocus();
  });

  it("shows the shared message for other errors", async () => {
    mockApi({ [REFRESH]: [loggedOut()], [LOGIN]: [apiError(429, "RequestRejected")] });
    renderApp("/");

    await logIn();

    expect(await screen.findByRole("alert")).toHaveTextContent("Too many attempts.");
  });

  it("disables the button while logging in", async () => {
    mockApi({ [REFRESH]: [loggedOut()], [LOGIN]: [pending()] });
    renderApp("/");

    await logIn();

    expect(await screen.findByRole("button", { name: "Logging in…" })).toBeDisabled();
  });

  it("sends a logged-in user from / to their home page", async () => {
    mockApi({ [REFRESH]: [token("a1")], [ME]: [me()] });
    const { url } = renderApp("/");

    expect(await screen.findByRole("heading", { level: 2, name: "Customer" })).toBeInTheDocument();
    expect(url()).toBe("/home");
  });
});

describe("pages that need login", () => {
  it("send a visitor to log in, then back to the page they asked for", async () => {
    mockApi({ [REFRESH]: [loggedOut()], [LOGIN]: [token("a1")], [ME]: [me()] });
    const { url } = renderApp("/home?tab=1");

    await screen.findByRole("heading", { level: 1, name: "Log in" });
    expect(url()).toBe("/?next=%2Fhome%3Ftab%3D1");
    await logIn();

    await screen.findByRole("heading", { level: 2, name: "Customer" });
    expect(url()).toBe("/home?tab=1");
  });

  it("go to home instead of a next that leaves the app", async () => {
    mockApi({ [REFRESH]: [loggedOut()], [LOGIN]: [token("a1")], [ME]: [me()] });
    const { url } = renderApp("/?next=%2F%2Fevil.example");

    await logIn();

    await screen.findByRole("heading", { level: 2, name: "Customer" });
    expect(url()).toBe("/home");
  });
});

describe("sign up", () => {
  it("creates the account and takes the new customer to their account page", async () => {
    const api = mockApi({ [REFRESH]: [loggedOut()], [REGISTER]: [token("a1")], [ME]: [me({}, "Cara Customer")] });
    const { url } = renderApp("/");
    const user = userEvent.setup();

    await user.click(await screen.findByRole("link", { name: "Sign up" }));
    expect(await screen.findByRole("heading", { level: 1, name: "Sign up" })).toBeInTheDocument();
    const zone = screen.getByLabelText("Time zone");
    expect(zone).toHaveValue(Intl.DateTimeFormat().resolvedOptions().timeZone);
    await user.type(screen.getByLabelText("Name"), "Cara Customer");
    await user.type(screen.getByLabelText("Email"), "cara@example.test");
    await user.type(screen.getByLabelText("Password"), "cara password 1");
    await user.type(screen.getByLabelText("Confirm password"), "cara password 1");
    await user.selectOptions(zone, "Europe/London");
    await user.click(screen.getByRole("button", { name: "Sign up" }));

    expect(await screen.findByRole("heading", { level: 2, name: "Customer" })).toBeInTheDocument();
    expect(url()).toBe("/home");
    expect(api.calls(REGISTER)[0]!.body).toEqual({
      name: "Cara Customer", email: "cara@example.test", password: "cara password 1", timeZone: "Europe/London",
    });
  });

  it("checks that the two passwords match before sending anything", async () => {
    const api = mockApi({ [REFRESH]: [loggedOut()] });
    renderApp("/sign-up");
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText("Password"), "cara password 1");
    await user.type(screen.getByLabelText("Confirm password"), "cara password 2");
    await user.click(screen.getByRole("button", { name: "Sign up" }));

    const confirm = screen.getByLabelText("Confirm password");
    await waitFor(() => expect(confirm).toHaveAttribute("aria-invalid", "true"));
    expect(confirm).toHaveAccessibleDescription("The passwords don't match.");
    expect(confirm).toHaveFocus();
    expect(api.calls(REGISTER)).toHaveLength(0);
  });

  it("shows the API's field errors when the passwords match", async () => {
    const api = mockApi({
      [REFRESH]: [loggedOut()],
      [REGISTER]: [apiError(400, "ValidationFailed", { fields: { password: "Must be 8-128 characters." } })],
    });
    renderApp("/sign-up");
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText("Password"), "short");
    await user.type(screen.getByLabelText("Confirm password"), "short");
    await user.click(screen.getByRole("button", { name: "Sign up" }));

    const password = screen.getByLabelText("Password");
    await waitFor(() => expect(password).toHaveAccessibleDescription("Must be 8-128 characters."));
    expect(password).toHaveFocus();
    expect(screen.getByLabelText("Confirm password")).not.toHaveAttribute("aria-invalid");
    expect(api.calls(REGISTER)[0]!.body).not.toHaveProperty("confirmPassword");
  });

  it("says when the email already belongs to an account, with a link to log in that keeps next", async () => {
    mockApi({ [REFRESH]: [loggedOut()], [REGISTER]: [apiError(409, "EmailTaken")] });
    renderApp("/sign-up?next=%2Fb%2F1");
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText("Email"), "olive@example.test");
    await user.click(screen.getByRole("button", { name: "Sign up" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("An account with that email already exists.");
    expect(within(alert).getByRole("link", { name: "Log in" })).toHaveAttribute("href", "/?next=%2Fb%2F1");
  });

  it("asks for the password only once on login", async () => {
    mockApi({ [REFRESH]: [loggedOut()] });
    renderApp("/");

    await screen.findByLabelText("Password");
    expect(screen.queryByLabelText("Confirm password")).not.toBeInTheDocument();
  });

  it("keeps next on the link from login to sign up", async () => {
    mockApi({ [REFRESH]: [loggedOut()] });
    renderApp("/?next=%2Fb%2F1");

    expect(await screen.findByRole("link", { name: "Sign up" })).toHaveAttribute("href", "/sign-up?next=%2Fb%2F1");
  });

  it("sends a logged-in user to their home page", async () => {
    mockApi({ [REFRESH]: [token("a1")], [ME]: [me()] });
    const { url } = renderApp("/sign-up");

    await screen.findByRole("heading", { level: 2, name: "Customer" });
    expect(url()).toBe("/home");
  });
});

describe("logout", () => {
  it("logs out and goes back to login", async () => {
    const api = mockApi({ [REFRESH]: [token("a1")], [ME]: [me()], [LOGOUT]: [new Response(null, { status: 204 })] });
    const { url } = renderApp("/home");
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Log out" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Log in" })).toBeInTheDocument();
    expect(url()).toBe("/");
    expect(api.calls(LOGOUT)).toHaveLength(1);
    expect(screen.queryByText("Your session has ended. Please log in again.")).not.toBeInTheDocument();
  });

  it("shows the error and stays logged in when the call fails", async () => {
    mockApi({ [REFRESH]: [token("a1")], [ME]: [me()], [LOGOUT]: [new TypeError("Failed to fetch")] });
    const { url } = renderApp("/home");
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Log out" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("We couldn't reach the server.");
    expect(url()).toBe("/home");
  });

  it("follows a logout in another tab", async () => {
    mockApi({ [REFRESH]: [token("a1")], [ME]: [me()] });
    renderApp("/home");
    await screen.findByRole("button", { name: "Log out" });
    const otherTab = new BroadcastChannel("booking-platform-session");

    otherTab.postMessage("loggedOut");

    expect(await screen.findByRole("heading", { level: 1, name: "Log in" })).toBeInTheDocument();
    otherTab.close();
  });
});

describe("session", () => {
  it("shows a loading state while it finds out whether the user is logged in", async () => {
    mockApi({ [REFRESH]: [pending()] });
    renderApp("/");

    expect(await screen.findByRole("status")).toHaveTextContent("Loading…");
  });

  it("shows the shared error when the API can't be reached on load", async () => {
    mockApi({ [REFRESH]: [new TypeError("Failed to fetch")] });
    renderApp("/");

    expect(await screen.findByRole("alert")).toHaveTextContent("We couldn't reach the server.");
  });

  it("goes back to login, saying the session ended, when a refresh is refused mid-session", async () => {
    mockApi({
      [REFRESH]: [token("a1"), apiError(401, "InvalidRefreshToken")],
      [ME]: [me(), apiError(401, "Unauthenticated")],
    });
    const { url } = renderApp("/home");
    await screen.findByRole("button", { name: "Log out" });

    await act(() => session.authFetch("/v1/me").catch(() => undefined));

    expect(await screen.findByText("Your session has ended. Please log in again.")).toBeInTheDocument();
    expect(url()).toBe("/?next=%2Fhome");
  });

  it("moves focus to the new page's heading after navigating", async () => {
    mockApi({ [REFRESH]: [loggedOut()] });
    renderApp("/");
    const user = userEvent.setup();

    await user.click(await screen.findByRole("link", { name: "Sign up" }));

    const heading = await screen.findByRole("heading", { level: 1, name: "Sign up" });
    await waitFor(() => expect(heading).toHaveFocus());
  });
});

describe("accessibility", () => {
  it.each([
    ["login", "/", "Log in", loggedOut],
    ["sign-up", "/sign-up", "Sign up", loggedOut],
  ])("the %s page has no violations", async (_name, path, heading, refresh) => {
    mockApi({ [REFRESH]: [refresh()] });
    const { container } = renderApp(path);
    await screen.findByRole("heading", { level: 1, name: heading });

    await expectNoAxeViolations(container);
  });

  it("the home page has no violations", async () => {
    mockApi({ [REFRESH]: [token("a1")], [ME]: [me({ admin: true, owner: true, staff: true })] });
    const { container } = renderApp("/home");
    await screen.findByRole("heading", { level: 2, name: "Admin" });

    await expectNoAxeViolations(container);
  });
});
