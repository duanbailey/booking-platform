import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router";
import { describe, expect, it } from "vitest";
import { expectNoAxeViolations } from "../test/axe";
import { apiError, me, mockApi, token } from "../test/api";
import { routes } from "./routes";

// T35: invites, password recovery and profile. Stories "Invited user sets their password", "User can recover a
// forgotten password", "User can change their password" (specs/02-requirements.md); forgot-password says the same
// whether or not the account exists (specs/07-nfr.md); screens and wording in specs/09-ui.md.

const REFRESH = "POST /v1/auth/refresh";
const ME = "GET /v1/me";
const PATCH_ME = "PATCH /v1/me";
const ACCEPT = "POST /v1/auth/accept-invite";
const FORGOT = "POST /v1/auth/forgot-password";
const RESET = "POST /v1/auth/reset-password";
const CHANGE = "POST /v1/auth/change-password";
const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function renderApp(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const view = render(<RouterProvider router={router} />);
  const url = () => router.state.location.pathname + router.state.location.search;
  return { router, url, ...view };
}

async function setPassword(password = "new password 1", confirm = password) {
  const user = userEvent.setup();
  await user.type(await screen.findByLabelText("Password"), password);
  await user.type(screen.getByLabelText("Confirm password"), confirm);
  await user.click(screen.getByRole("button", { name: "Set password" }));
}

describe.each([
  {
    screen: "accept invite", path: "/accept-invite", call: ACCEPT, heading: "Set your password", invalid: "InviteInvalid",
    message: "This invite link is no longer valid. Ask whoever invited you to send a new one, or set your password with a recovery link.",
    link: "set your password with a recovery link",
  },
  {
    screen: "reset password", path: "/reset-password", call: RESET, heading: "Set a new password", invalid: "ResetLinkInvalid",
    message: "This link is no longer valid. Request a new one.", link: "Request a new one",
  },
])("$screen", ({ path, call, heading, invalid, message, link }) => {
  it("sets the password from the link's token and logs the user in", async () => {
    const api = mockApi({ [call]: [token("a1")], [ME]: [me({ staff: true })] });
    const { url } = renderApp(`${path}?token=emailed-9f2c`);

    expect(await screen.findByRole("heading", { level: 1, name: heading })).toBeInTheDocument();
    await setPassword();

    expect(await screen.findByRole("heading", { level: 2, name: "Staff" })).toBeInTheDocument();
    expect(url()).toBe("/home");
    expect(api.calls(call)[0]!.body).toEqual({ token: "emailed-9f2c", password: "new password 1" });
    expect(api.calls(REFRESH)).toHaveLength(0); // logged in by the answer, no refresh needed
  });

  it("says when the link is no longer valid, and offers a new one", async () => {
    mockApi({ [call]: [apiError(400, invalid)] });
    renderApp(`${path}?token=old`);

    await setPassword();

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(message);
    expect(within(alert).getByRole("link", { name: link })).toHaveAttribute("href", "/forgot-password");
    expect(screen.queryByLabelText("Password")).not.toBeInTheDocument();
  });

  it("says the same for a link without a token, without calling the API", async () => {
    mockApi({});
    renderApp(path);

    expect(await screen.findByRole("alert")).toHaveTextContent(message);
    expect(screen.queryByLabelText("Password")).not.toBeInTheDocument();
  });

  it("checks that the two passwords match before sending anything", async () => {
    const api = mockApi({});
    renderApp(`${path}?token=tok-1`);

    await setPassword("new password 1", "new password 2");

    const confirm = screen.getByLabelText("Confirm password");
    await waitFor(() => expect(confirm).toHaveAccessibleDescription("The passwords don't match."));
    expect(confirm).toHaveFocus();
    expect(api.calls(call)).toHaveLength(0);
  });

  it("shows the API's field errors", async () => {
    mockApi({ [call]: [apiError(400, "ValidationFailed", { fields: { password: "Must be 8-128 characters." } })] });
    renderApp(`${path}?token=tok-1`);

    await setPassword("short");

    await waitFor(() => expect(screen.getByLabelText("Password")).toHaveAccessibleDescription("Must be 8-128 characters."));
  });

  it("works for someone already logged in, logging in as the link's account", async () => {
    const api = mockApi({ [REFRESH]: [token("a1")], [ME]: [me({}, "Olive Owner"), me({ staff: true }, "Sam Staff")] });
    const { router } = renderApp("/home");
    await screen.findByRole("button", { name: "Log out" });
    api.answer(call, token("a2"));

    await router.navigate(`${path}?token=tok-1`);
    await setPassword();

    expect(await screen.findByRole("heading", { level: 2, name: "Staff" })).toBeInTheDocument();
    expect(screen.getByRole("banner")).toHaveTextContent("Sam Staff");
  });

  it("has no accessibility violations", async () => {
    mockApi({});
    const { container } = renderApp(`${path}?token=tok-1`);
    await screen.findByRole("heading", { level: 1, name: heading });

    await expectNoAxeViolations(container);
  });
});

describe("forgot password", () => {
  it("is reached from login, and says the same whether or not the account exists", async () => {
    const api = mockApi({ [REFRESH]: [apiError(401, "InvalidRefreshToken")], [FORGOT]: [new Response(null, { status: 202 })] });
    const { url } = renderApp("/");
    const user = userEvent.setup();

    await user.click(await screen.findByRole("link", { name: "Forgot your password?" }));
    expect(await screen.findByRole("heading", { level: 1, name: "Forgot password" })).toBeInTheDocument();
    expect(url()).toBe("/forgot-password");
    await user.type(screen.getByLabelText("Email"), "olive@example.test");
    await user.click(screen.getByRole("button", { name: "Send link" }));

    expect(await screen.findByText("If an account exists for that email, a link has been sent.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Email")).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Back to log in" })).toHaveAttribute("href", "/");
    expect(api.calls(FORGOT)[0]!.body).toEqual({ email: "olive@example.test" });
  });

  it("shows the API's field errors", async () => {
    mockApi({ [FORGOT]: [apiError(400, "ValidationFailed", { fields: { email: "Is not a valid email address." } })] });
    renderApp("/forgot-password");
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Send link" }));

    const email = screen.getByLabelText("Email");
    await waitFor(() => expect(email).toHaveAccessibleDescription("Is not a valid email address."));
    expect(email).toHaveFocus();
  });

  it("shows the shared message when rate limited", async () => {
    mockApi({ [FORGOT]: [apiError(429, "RequestRejected")] });
    renderApp("/forgot-password");
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText("Email"), "olive@example.test");

    await user.click(screen.getByRole("button", { name: "Send link" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Too many attempts.");
  });

  it("has no accessibility violations", async () => {
    mockApi({});
    const { container } = renderApp("/forgot-password");
    await screen.findByRole("heading", { level: 1, name: "Forgot password" });

    await expectNoAxeViolations(container);
  });
});

describe("profile", () => {
  it("is reached from the header, shows the email and saves name and time zone", async () => {
    const api = mockApi({
      [REFRESH]: [token("a1")], [ME]: [me()],
      [PATCH_ME]: [json(200, { ...(await me().json()), name: "Olive Oyl", timeZone: "Europe/London" })],
    });
    const { url } = renderApp("/home");
    const user = userEvent.setup();

    await user.click(await screen.findByRole("link", { name: "Profile" }));
    expect(await screen.findByRole("heading", { level: 1, name: "Profile" })).toBeInTheDocument();
    expect(url()).toBe("/profile");
    expect(screen.getByText("Email: olive@example.test")).toBeInTheDocument();
    const name = screen.getByLabelText("Name");
    expect(name).toHaveValue("Olive Owner");
    expect(screen.getByLabelText("Time zone")).toHaveValue("America/New_York");
    await user.clear(name);
    await user.type(name, "Olive Oyl");
    await user.selectOptions(screen.getByLabelText("Time zone"), "Europe/London");
    await user.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByRole("status")).toHaveTextContent("Your profile has been saved.");
    expect(screen.getByRole("banner")).toHaveTextContent("Olive Oyl");
    const patch = api.calls(PATCH_ME)[0]!;
    expect(patch.body).toEqual({ name: "Olive Oyl", timeZone: "Europe/London" });
    expect(patch.headers.get("Authorization")).toBe("Bearer a1");
  });

  it("preselects the user's saved zone even when the browser's list leaves it out", async () => {
    expect(Intl.supportedValuesOf("timeZone")).not.toContain("Etc/GMT+5");
    mockApi({ [REFRESH]: [token("a1")], [ME]: [json(200, { ...(await me().json()), timeZone: "Etc/GMT+5" })] });
    renderApp("/profile");

    expect(await screen.findByLabelText("Time zone")).toHaveValue("Etc/GMT+5");
  });

  it("shows the API's field errors", async () => {
    mockApi({
      [REFRESH]: [token("a1")], [ME]: [me()],
      [PATCH_ME]: [apiError(400, "ValidationFailed", { fields: { name: "Is required." } })],
    });
    renderApp("/profile");
    const user = userEvent.setup();
    await user.clear(await screen.findByLabelText("Name"));

    await user.click(screen.getByRole("button", { name: "Save" }));

    const name = screen.getByLabelText("Name");
    await waitFor(() => expect(name).toHaveAccessibleDescription("Is required."));
    expect(name).toHaveFocus();
    expect(screen.queryByText("Your profile has been saved.")).not.toBeInTheDocument();
  });

  it("has no accessibility violations", async () => {
    mockApi({ [REFRESH]: [token("a1")], [ME]: [me()] });
    const { container } = renderApp("/profile");
    await screen.findByRole("heading", { level: 1, name: "Profile" });

    await expectNoAxeViolations(container);
  });
});

describe("change password", () => {
  async function change(current: string, next: string, confirm = next) {
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText("Current password"), current);
    await user.type(screen.getByLabelText("New password"), next);
    await user.type(screen.getByLabelText("Confirm new password"), confirm);
    await user.click(screen.getByRole("button", { name: "Change password" }));
  }

  it("is reached from the profile, keeps the user logged in with the new tokens, and says so", async () => {
    const api = mockApi({
      [REFRESH]: [token("a1")], [ME]: [me()], [CHANGE]: [token("a2")],
      [PATCH_ME]: [json(200, await me().json())],
    });
    const { url } = renderApp("/profile");
    const user = userEvent.setup();

    await user.click(await screen.findByRole("link", { name: "Change password" }));
    expect(await screen.findByRole("heading", { level: 1, name: "Change password" })).toBeInTheDocument();
    await change("old password 1", "new password 1");

    expect(await screen.findByRole("status")).toHaveTextContent("Your password has been changed.");
    expect(url()).toBe("/profile");
    const changeCall = api.calls(CHANGE)[0]!;
    expect(changeCall.body).toEqual({ currentPassword: "old password 1", newPassword: "new password 1" });
    expect(changeCall.headers.get("Authorization")).toBe("Bearer a1");

    await user.click(screen.getByRole("button", { name: "Save" }));
    await screen.findByText("Your profile has been saved.");
    expect(api.calls(PATCH_ME)[0]!.headers.get("Authorization")).toBe("Bearer a2");
  });

  it("says when the current password is wrong, next to it, and nothing changes", async () => {
    mockApi({ [REFRESH]: [token("a1")], [ME]: [me()], [CHANGE]: [apiError(422, "CurrentPasswordIncorrect")] });
    const { url } = renderApp("/change-password");

    await change("wrong password", "new password 1");

    const current = screen.getByLabelText("Current password");
    await waitFor(() => expect(current).toHaveAccessibleDescription("The current password is incorrect."));
    expect(current).toHaveFocus();
    expect(url()).toBe("/change-password");
  });

  it("checks that the two new passwords match before sending anything", async () => {
    const api = mockApi({ [REFRESH]: [token("a1")], [ME]: [me()] });
    renderApp("/change-password");

    await change("old password 1", "new password 1", "new password 2");

    const confirm = screen.getByLabelText("Confirm new password");
    await waitFor(() => expect(confirm).toHaveAccessibleDescription("The passwords don't match."));
    expect(api.calls(CHANGE)).toHaveLength(0);
  });

  it("shows the API's field errors", async () => {
    mockApi({
      [REFRESH]: [token("a1")], [ME]: [me()],
      [CHANGE]: [apiError(400, "ValidationFailed", { fields: { newPassword: "Must be 8-128 characters." } })],
    });
    renderApp("/change-password");

    await change("old password 1", "short");

    await waitFor(() =>
      expect(screen.getByLabelText("New password")).toHaveAccessibleDescription("Must be 8-128 characters."));
  });

  it("needs login", async () => {
    mockApi({ [REFRESH]: [apiError(401, "InvalidRefreshToken")] });
    const { url } = renderApp("/change-password");

    await screen.findByRole("heading", { level: 1, name: "Log in" });
    expect(url()).toBe("/?next=%2Fchange-password");
  });

  it("has no accessibility violations", async () => {
    mockApi({ [REFRESH]: [token("a1")], [ME]: [me()] });
    const { container } = renderApp("/change-password");
    await screen.findByRole("heading", { level: 1, name: "Change password" });

    await expectNoAxeViolations(container);
  });
});
