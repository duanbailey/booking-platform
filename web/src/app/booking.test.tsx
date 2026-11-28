import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { expectNoAxeViolations } from "../test/axe";
import { always, apiError, json, me, mockApi, pending, token, type Answer } from "../test/api";
import { routes } from "./routes";

// T36: a customer finds and books a slot. Stories "Customer views open slots for a service and date range" and
// "Customer books a slot and gets a confirmation" (specs/02-requirements.md); R2 (both zones, elapsed duration across
// a DST change), R3 (MinimumNotice), R4 (ConfirmationRequired), R7 (SlotConflict), R8 / ADR 0005 (30-day range);
// screens and wording in specs/09-ui.md.

const REFRESH = "POST /v1/auth/refresh";
const ME = "GET /v1/me";
const BUSINESS = "GET /v1/businesses/b1";
const SERVICES = "GET /v1/businesses/b1/services?limit=200";
const SERVICE = "GET /v1/services/svc1";
const STAFF = "GET /v1/businesses/b1/staff?limit=200";
const slotsKey = (from: string, to: string) => `GET /v1/services/svc1/slots?from=${from}&to=${to}`;
const DEFAULT_SLOTS = slotsKey("2026-11-30", "2026-12-06");
const BOOK = "POST /v1/bookings";

const business = {
  id: "b1", name: "Cut & Dry", timeZone: "America/New_York", minNoticeMinutes: 120, cancellationWindowHours: 24,
  maxAdvanceDays: 21, slotStepMinutes: 30,
};
const haircut = {
  id: "svc1", businessId: "b1", name: "Haircut", description: "Wash, cut and dry.", durationMinutes: 60,
  bufferBeforeMinutes: 10, bufferAfterMinutes: 5, price: "45.00",
};
const staff = {
  items: [
    { staffId: "s1", businessId: "b1", userId: "u1", name: "Olive Owner", removedAt: null },
    { staffId: "s2", businessId: "b1", userId: "u2", name: "Sam Staff", removedAt: null },
    { staffId: "s3", businessId: "b1", userId: "u3", name: "Nora Noslots", removedAt: null },
  ],
  nextCursor: null,
};
const oliveSlot = { staffId: "s1", startUtc: "2026-11-30T19:00:00Z", endUtc: "2026-11-30T20:00:00Z" };
const samSlot = { staffId: "s2", startUtc: "2026-12-01T15:00:00Z", endUtc: "2026-12-01T16:00:00Z" };
const slots = (...list: object[]) => json(200, { businessTimeZone: "America/New_York", slots: list });
const booking = {
  id: "bk1", businessId: "b1", businessTimeZone: "America/New_York", serviceId: "svc1",
  customer: { userId: "u1", name: "Olive Owner", email: "olive@example.test" }, ...oliveSlot, status: "Confirmed",
  cancelledAt: null, cancellationReason: null,
};

/** A logged-in customer (zone `zone`) and the business's data; slot answers per range key. */
function api(slotAnswers: Record<string, Answer[]> = {}, zone = "America/New_York", extra: Record<string, Answer[]> = {}) {
  return mockApi({
    [REFRESH]: [token("a1")],
    [ME]: [me({}, "Cara Customer", zone)],
    [BUSINESS]: [always(() => json(200, business))],
    [SERVICES]: [always(() => json(200, { items: [haircut], nextCursor: null }))],
    [SERVICE]: [always(() => json(200, haircut))],
    [STAFF]: [always(() => json(200, staff))],
    [DEFAULT_SLOTS]: [always(() => slots(oliveSlot, samSlot))],
    ...slotAnswers,
    ...extra,
  });
}

function renderApp(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const view = render(<RouterProvider router={router} />);
  const url = () => router.state.location.pathname + router.state.location.search;
  return { router, url, ...view };
}

const SLOTS_PAGE = "/b/b1/services/svc1";

beforeEach(() => {
  // Monday 30 November 2026, 07:00 in New York.
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(new Date("2026-11-30T12:00:00Z"));
});

afterEach(() => {
  vi.useRealTimers();
});

describe("business page", () => {
  it("lists the business's services, each linking to its slots", async () => {
    const calls = api();
    renderApp("/b/b1");

    expect(await screen.findByRole("heading", { level: 1, name: "Cut & Dry" })).toBeInTheDocument();
    const item = screen.getByRole("listitem");
    expect(within(item).getByRole("link", { name: "Haircut" })).toHaveAttribute("href", "/b/b1/services/svc1");
    expect(item).toHaveTextContent("Wash, cut and dry.");
    expect(item).toHaveTextContent("Duration: 1 hour");
    expect(item).toHaveTextContent("Price: 45.00");
    expect(calls.calls(BUSINESS)[0]!.headers.get("Authorization")).toBe("Bearer a1");
  });

  it("reads every page of services", async () => {
    const colour = { ...haircut, id: "svc2", name: "Colour", description: null };
    const calls = api({}, "America/New_York", {
      [SERVICES]: [json(200, { items: [haircut], nextCursor: "c2" })],
      [`${SERVICES}&cursor=c2`]: [json(200, { items: [colour], nextCursor: null })],
    });
    renderApp("/b/b1");

    expect(await screen.findByRole("link", { name: "Colour" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Haircut" })).toBeInTheDocument();
    expect(calls.calls(`${SERVICES}&cursor=c2`)).toHaveLength(1);
  });

  it("says when there are no services yet", async () => {
    api({}, "America/New_York", { [SERVICES]: [json(200, { items: [], nextCursor: null })] });
    renderApp("/b/b1");

    expect(await screen.findByText("Cut & Dry has no services to book yet.")).toBeInTheDocument();
  });

  it("shows the shared message for an unknown business", async () => {
    mockApi({ [REFRESH]: [token("a1")], [ME]: [me()], [BUSINESS]: [apiError(404, "NotFound")],
      [SERVICES]: [apiError(404, "NotFound")] });
    renderApp("/b/b1");

    expect(await screen.findByRole("alert")).toHaveTextContent("We couldn't find what you were looking for.");
  });

  it("sends a visitor to log in, then back to the link", async () => {
    mockApi({ [REFRESH]: [apiError(401, "InvalidRefreshToken")] });
    const { url } = renderApp("/b/b1");

    await screen.findByRole("heading", { level: 1, name: "Log in" });
    expect(url()).toBe("/?next=%2Fb%2Fb1");
  });
});

describe("slots page", () => {
  it("shows the open slots for the next 7 days in the business's zone, grouped by date", async () => {
    const calls = api();
    renderApp(SLOTS_PAGE);

    expect(await screen.findByRole("heading", { level: 1, name: "Haircut" })).toBeInTheDocument();
    expect(screen.getByLabelText("From")).toHaveValue("2026-11-30");
    expect(screen.getByLabelText("To")).toHaveValue("2026-12-06");
    expect(calls.calls(DEFAULT_SLOTS)).toHaveLength(1);
    const monday = screen.getByRole("region", { name: "Monday 30 November 2026" });
    expect(within(monday).getByRole("button", { name: /14:00-15:00 with Olive Owner/ })).toBeInTheDocument();
    const tuesday = screen.getByRole("region", { name: "Tuesday 1 December 2026" });
    expect(within(tuesday).getByRole("button", { name: /10:00-11:00 with Sam Staff/ })).toBeInTheDocument();
    expect(screen.queryByText(/Your time/)).not.toBeInTheDocument();
    expect(screen.getByText(/Times are in America\/New_York/)).toBeInTheDocument();
  });

  it("defaults to today in the business's zone, not UTC's", async () => {
    vi.setSystemTime(new Date("2026-11-30T03:00:00Z")); // 22:00 on Sunday 29 November in New York
    const calls = api({ [slotsKey("2026-11-29", "2026-12-05")]: [always(() => slots())] });
    renderApp(SLOTS_PAGE);

    expect(await screen.findByLabelText("From")).toHaveValue("2026-11-29");
    expect(calls.calls(slotsKey("2026-11-29", "2026-12-05"))).toHaveLength(1);
  });

  it("R2: also shows the customer's own time when their zone differs", async () => {
    api({}, "Europe/London");
    renderApp(SLOTS_PAGE);

    const button = await screen.findByRole("button", { name: /14:00-15:00 with Olive Owner/ });
    expect(button).toHaveTextContent("Your time: Monday 30 November 2026, 19:00-20:00 Europe/London (UTC+00:00)");
  });

  it("shows the range the customer picks", async () => {
    const calls = api({ [slotsKey("2026-12-10", "2026-12-12")]: [always(() => slots())] });
    const { url } = renderApp(SLOTS_PAGE);
    const user = userEvent.setup();
    const from = await screen.findByLabelText("From");

    await user.clear(from);
    await user.type(from, "2026-12-10");
    await user.clear(screen.getByLabelText("To"));
    await user.type(screen.getByLabelText("To"), "2026-12-12");
    await user.click(screen.getByRole("button", { name: "Show times" }));

    expect(await screen.findByText("No open times in these dates. Try later dates.")).toBeInTheDocument();
    expect(url()).toBe(`${SLOTS_PAGE}?from=2026-12-10&to=2026-12-12`);
    expect(calls.calls(slotsKey("2026-12-10", "2026-12-12"))).toHaveLength(1);
  });

  it("moves to the next and previous ranges of the same length", async () => {
    api({
      [slotsKey("2026-12-07", "2026-12-13")]: [always(() => slots())],
    });
    const { url } = renderApp(SLOTS_PAGE);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("link", { name: "Later" }));
    await screen.findByText("No open times in these dates. Try later dates.");
    expect(url()).toBe(`${SLOTS_PAGE}?from=2026-12-07&to=2026-12-13`);
    expect(screen.getByRole("link", { name: "Earlier" })).toHaveAttribute("href", `${SLOTS_PAGE}?from=2026-11-30&to=2026-12-06`);
  });

  it("shifts a shorter range by its own length", async () => {
    api({ [slotsKey("2026-12-01", "2026-12-03")]: [always(() => slots())] });
    renderApp(`${SLOTS_PAGE}?from=2026-12-01&to=2026-12-03`);

    expect(await screen.findByRole("link", { name: "Later" })).toHaveAttribute("href", `${SLOTS_PAGE}?from=2026-12-04&to=2026-12-06`);
    expect(screen.getByRole("link", { name: "Earlier" })).toHaveAttribute("href", `${SLOTS_PAGE}?from=2026-11-28&to=2026-11-30`);
  });

  it("ADR 0005: says the maximum range is 30 days when a longer range is asked for", async () => {
    api({ [slotsKey("2026-11-30", "2027-01-30")]: [always(() => apiError(400, "RangeTooLong", { maxDays: 30 }))] });
    renderApp(`${SLOTS_PAGE}?from=2026-11-30&to=2027-01-30`);

    const to = await screen.findByLabelText("To");
    await waitFor(() => expect(to).toHaveAccessibleDescription("The maximum range is 30 days."));
    expect(screen.queryByRole("button", { name: /with/ })).not.toBeInTheDocument();
  });

  it("shows the API's field problems with the dates", async () => {
    api({ [slotsKey("2026-12-06", "2026-11-30")]: [always(() =>
      apiError(400, "ValidationFailed", { fields: { to: "Must not be before from." } }))] });
    renderApp(`${SLOTS_PAGE}?from=2026-12-06&to=2026-11-30`);

    await waitFor(() => expect(screen.getByLabelText("To")).toHaveAccessibleDescription("Must not be before from."));
  });

  it("filters by staff member, listing only staff who have slots in the range", async () => {
    api();
    renderApp(SLOTS_PAGE);
    const user = userEvent.setup();
    const filter = await screen.findByLabelText("Staff");

    expect(within(filter).getAllByRole("option").map((o) => o.textContent)).toEqual(["Anyone", "Olive Owner", "Sam Staff"]);
    await user.selectOptions(filter, "Sam Staff");

    expect(screen.getByRole("button", { name: /with Sam Staff/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /with Olive Owner/ })).not.toBeInTheDocument();
  });

  it("has no accessibility violations", async () => {
    api({}, "Europe/London");
    const { container } = renderApp(SLOTS_PAGE);
    await screen.findByRole("button", { name: /with Olive Owner/ });

    await expectNoAxeViolations(container);
  });
});

describe("booking", () => {
  async function pick(name: RegExp = /14:00-15:00 with Olive Owner/) {
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name }));
    return user;
  }

  it("reviews the slot, books it with an idempotency key and confirms", async () => {
    const calls = api({}, "Europe/London", { [BOOK]: [json(201, booking)] });
    renderApp(SLOTS_PAGE);
    const user = await pick();

    const review = await screen.findByRole("region", { name: "Review your booking" });
    expect(review).toHaveTextContent("Haircut with Olive Owner");
    expect(review).toHaveTextContent("When: Monday 30 November 2026, 14:00-15:00 America/New_York (UTC-05:00)");
    expect(review).toHaveTextContent("Your time: Monday 30 November 2026, 19:00-20:00 Europe/London (UTC+00:00)");
    expect(review).toHaveTextContent("Duration: 1 hour");
    expect(review).toHaveTextContent("Price: 45.00");
    await user.click(within(review).getByRole("button", { name: "Book" }));

    expect(await screen.findByRole("status")).toHaveTextContent("Your booking is confirmed. A confirmation email is on its way.");
    const call = calls.calls(BOOK)[0]!;
    expect(call.body).toEqual({ serviceId: "svc1", staffId: "s1", startUtc: "2026-11-30T19:00:00Z" });
    expect(call.headers.get("Idempotency-Key")).toMatch(/^[0-9a-f-]{36}$/);
    expect(call.headers.get("Authorization")).toBe("Bearer a1");
  });

  it("R2: shows the elapsed duration for a slot across a spring-forward change", async () => {
    const dst = { staffId: "s1", startUtc: "2026-03-08T06:00:00Z", endUtc: "2026-03-08T08:00:00Z" };
    api({ [slotsKey("2026-03-08", "2026-03-08")]: [always(() => slots(dst))] });
    renderApp(`${SLOTS_PAGE}?from=2026-03-08&to=2026-03-08`);
    await pick(/01:00 \(UTC-05:00\) - 04:00 \(UTC-04:00\)/);

    const review = await screen.findByRole("region", { name: "Review your booking" });
    expect(review).toHaveTextContent("When: Sunday 8 March 2026, 01:00 (UTC-05:00) - 04:00 (UTC-04:00) America/New_York");
    expect(review).toHaveTextContent("Duration: 2 hours");
  });

  it("goes back to the slots with Choose another time", async () => {
    api();
    renderApp(SLOTS_PAGE);
    const user = await pick();

    await user.click(await screen.findByRole("button", { name: "Choose another time" }));

    expect(screen.queryByRole("region", { name: "Review your booking" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /with Sam Staff/ })).toBeInTheDocument();
  });

  it.each([
    ["R7: SlotConflict", apiError(409, "SlotConflict", oliveSlot),
      "Sorry, that time was just taken: 14:00-15:00 with Olive Owner on Monday 30 November 2026. Please choose another time."],
    ["R3: MinimumNotice", apiError(422, "MinimumNotice", { minNoticeMinutes: 120 }),
      "That time is too soon to book. Cut & Dry needs at least 2 hours' notice."],
    ["R3: MinimumNotice, singular", apiError(422, "MinimumNotice", { minNoticeMinutes: 60 }),
      "That time is too soon to book. Cut & Dry needs at least 1 hour's notice."],
    ["R8: SlotUnavailable", apiError(422, "SlotUnavailable"), "That time is no longer available. Please choose another time."],
  ])("%s: says why and goes back to the reloaded slots", async (_name, refusal, message) => {
    const calls = api({}, "America/New_York", { [BOOK]: [refusal] });
    renderApp(SLOTS_PAGE);
    const user = await pick();

    await user.click(await screen.findByRole("button", { name: "Book" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(message);
    expect(screen.queryByRole("region", { name: "Review your booking" })).not.toBeInTheDocument();
    await waitFor(() => expect(calls.calls(DEFAULT_SLOTS).length).toBeGreaterThan(1));
  });

  it("R4: warns that the original booking still stands, and books on Book anyway with the same key", async () => {
    const original = { bookingId: "old1", staffId: "s2", startUtc: "2026-11-30T21:00:00Z", endUtc: "2026-11-30T22:00:00Z" };
    const calls = api({}, "America/New_York", {
      [BOOK]: [apiError(409, "ConfirmationRequired", { affectedBookings: [original] }), json(201, booking)],
    });
    renderApp(SLOTS_PAGE);
    const user = await pick();
    await user.click(await screen.findByRole("button", { name: "Book" }));

    const review = screen.getByRole("region", { name: "Review your booking" });
    await within(review).findByText(
      "You already have a booking with Cut & Dry that is too close to cancel online. It still stands if you book this one:");
    expect(within(review).getByRole("listitem")).toHaveTextContent(
      "Monday 30 November 2026, 16:00-17:00 America/New_York (UTC-05:00) with Sam Staff");
    expect(review).toHaveTextContent("To cancel it, contact Cut & Dry.");
    await user.click(within(review).getByRole("button", { name: "Book anyway" }));

    expect(await screen.findByRole("status")).toHaveTextContent("Your booking is confirmed.");
    const [first, second] = calls.calls(BOOK);
    expect(second!.body).toEqual({
      serviceId: "svc1", staffId: "s1", startUtc: "2026-11-30T19:00:00Z", confirm: { affectedBookingIds: ["old1"] },
    });
    expect(second!.headers.get("Idempotency-Key")).toBe(first!.headers.get("Idempotency-Key"));
  });

  it("R4: shows the customer's own time for each original booking when their zone differs", async () => {
    const original = { bookingId: "old1", staffId: "s2", startUtc: "2026-11-30T21:00:00Z", endUtc: "2026-11-30T22:00:00Z" };
    api({}, "Europe/London", { [BOOK]: [apiError(409, "ConfirmationRequired", { affectedBookings: [original] })] });
    renderApp(SLOTS_PAGE);
    const user = await pick();
    await user.click(await screen.findByRole("button", { name: "Book" }));

    expect(await screen.findByRole("listitem")).toHaveTextContent(
      "with Sam Staff. Your time: Monday 30 November 2026, 21:00-22:00 Europe/London (UTC+00:00)");
  });

  it("R4: words several bookings in the plural, and shows a changed list again", async () => {
    const a = { bookingId: "old1", staffId: "s2", startUtc: "2026-11-30T21:00:00Z", endUtc: "2026-11-30T22:00:00Z" };
    const b = { bookingId: "old2", staffId: "s1", startUtc: "2026-12-01T14:00:00Z", endUtc: "2026-12-01T15:00:00Z" };
    const calls = api({}, "America/New_York", {
      [BOOK]: [apiError(409, "ConfirmationRequired", { affectedBookings: [a] }),
        apiError(409, "ConfirmationRequired", { affectedBookings: [a, b] })],
    });
    renderApp(SLOTS_PAGE);
    const user = await pick();
    await user.click(await screen.findByRole("button", { name: "Book" }));
    await user.click(await screen.findByRole("button", { name: "Book anyway" }));

    await screen.findByText(
      "You already have bookings with Cut & Dry that are too close to cancel online. They still stand if you book this one:");
    expect(screen.getAllByRole("listitem")).toHaveLength(2);
    expect(screen.getByText("To cancel them, contact Cut & Dry.")).toBeInTheDocument();
    expect(calls.calls(BOOK)[1]!.body).toMatchObject({ confirm: { affectedBookingIds: ["old1"] } });
  });

  it("shows other errors in the review, and a retry reuses the key", async () => {
    const calls = api({}, "America/New_York", { [BOOK]: [new TypeError("Failed to fetch"), json(201, booking)] });
    renderApp(SLOTS_PAGE);
    const user = await pick();
    await user.click(await screen.findByRole("button", { name: "Book" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("We couldn't reach the server.");
    await user.click(screen.getByRole("button", { name: "Book" }));

    expect(await screen.findByRole("status")).toHaveTextContent("Your booking is confirmed.");
    const [first, second] = calls.calls(BOOK);
    expect(second!.headers.get("Idempotency-Key")).toBe(first!.headers.get("Idempotency-Key"));
  });

  it("uses a new key for a new review", async () => {
    const calls = api({}, "America/New_York", {
      [BOOK]: [apiError(422, "SlotUnavailable"), json(201, booking)],
    });
    renderApp(SLOTS_PAGE);
    let user = await pick();
    await user.click(await screen.findByRole("button", { name: "Book" }));
    await screen.findByRole("alert");

    user = await pick(/10:00-11:00 with Sam Staff/);
    await user.click(await screen.findByRole("button", { name: "Book" }));

    await screen.findByRole("status");
    const [first, second] = calls.calls(BOOK);
    expect(second!.headers.get("Idempotency-Key")).not.toBe(first!.headers.get("Idempotency-Key"));
  });

  it("disables Book while booking", async () => {
    api({}, "America/New_York", { [BOOK]: [pending()] });
    renderApp(SLOTS_PAGE);
    const user = await pick();

    await user.click(await screen.findByRole("button", { name: "Book" }));

    expect(await screen.findByRole("button", { name: "Booking…" })).toBeDisabled();
  });

  it("moves focus to the review when a slot is picked", async () => {
    api();
    renderApp(SLOTS_PAGE);
    await pick();

    await waitFor(() => expect(screen.getByRole("heading", { level: 2, name: "Review your booking" })).toHaveFocus());
  });

  it("has no accessibility violations in the review and the R4 warning", async () => {
    const original = { bookingId: "old1", staffId: "s2", startUtc: "2026-11-30T21:00:00Z", endUtc: "2026-11-30T22:00:00Z" };
    api({}, "Europe/London", { [BOOK]: [apiError(409, "ConfirmationRequired", { affectedBookings: [original] })] });
    const { container } = renderApp(SLOTS_PAGE);
    const user = await pick();
    await user.click(await screen.findByRole("button", { name: "Book" }));
    await screen.findByRole("button", { name: "Book anyway" });

    await expectNoAxeViolations(container);
  });
});
