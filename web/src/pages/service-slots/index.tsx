import { useEffect, useRef, useState } from "react";
import type { RouteObject } from "react-router";
import { Form, Link, useFetcher, useLoaderData } from "react-router";
import { useSession } from "@/entities/session";
import type { ApiError } from "@/shared/api/client";
import type { AffectedBooking, Booking, Slot } from "@/shared/api/pages";
import {
  addDays, formatDate, formatDuration, formatMinutes, formatRange, formatTimes, localDateOf, rangeDays,
} from "@/shared/lib/time";
import { Alert, ErrorMessage } from "@/shared/ui/ErrorMessage";
import { fieldErrors, TextField } from "@/shared/ui/Field";
import { Page } from "@/shared/ui/Page";
import formStyles from "@/shared/ui/Form.module.css";
import { bookAction, slotsLoader, type BookAnswer, type SlotsData } from "./model";
import styles from "./ServiceSlots.module.css";

// T36: a service's slots and booking. Stories "Customer views open slots for a service and date range" and
// "Customer books a slot and gets a confirmation"; R2 (both zones, elapsed duration), R3, R4, R7, R8 / ADR 0005.
// Wording in specs/09-ui.md.

interface Review {
  slot: Slot;
  /** One Idempotency-Key per review: a retry and the R4 confirmation reuse it (T29). */
  key: string;
}

/** Codes that close the review and send the customer back to the (reloaded) slots with a message. */
const refusals = ["SlotConflict", "MinimumNotice", "SlotUnavailable"];

/** E.g. "2 hours' notice", "1 hour's notice". */
function notice(minutes: number): string {
  const text = formatMinutes(minutes);
  return `${text}${text.endsWith("s") ? "'" : "'s"} notice`;
}

function SlotsPage() {
  const data = useLoaderData<SlotsData>();
  const { business, service, staffNames, slots } = data;
  const myZone = useSession().user?.timeZone ?? business.timeZone;
  const fetcher = useFetcher<BookAnswer>();
  const [review, setReview] = useState<Review | null>(null);
  const [staffFilter, setStaffFilter] = useState("");
  const reviewHeading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    if (review) reviewHeading.current?.focus();
  }, [review]); // review changes only when a slot is picked

  const staffName = (staffId: string) => staffNames[staffId] ?? "a staff member";
  const yourTime = (slot: { startUtc: string; endUtc: string }) =>
    myZone === business.timeZone ? null : formatRange(slot.startUtc, slot.endUtc, myZone);

  // The answer to the current review's latest booking attempt, if any.
  const answer = review && fetcher.data?.key === review.key && fetcher.state === "idle" ? fetcher.data : null;
  const booked = answer && "booked" in answer ? answer.booked : null;
  const error = answer && "error" in answer ? answer.error : null;
  const refused = error && refusals.includes(error.code) ? error : null;

  function refusalMessage(refusal: ApiError, slot: Slot): string {
    if (refusal.code === "SlotConflict") {
      return `Sorry, that time was just taken: ${formatTimes(slot.startUtc, slot.endUtc, business.timeZone)} with `
        + `${staffName(slot.staffId)} on ${formatDate(localDateOf(slot.startUtc, business.timeZone))}. Please choose another time.`;
    }
    if (refusal.code === "MinimumNotice") {
      return `That time is too soon to book. ${business.name} needs at least ${notice(Number(refusal.details.minNoticeMinutes))}.`;
    }
    return "That time is no longer available. Please choose another time.";
  }

  function book(slot: Slot, key: string, confirm: AffectedBooking[] = []) {
    void fetcher.submit(
      { staffId: slot.staffId, startUtc: slot.startUtc, key, confirm: confirm.map((b) => b.bookingId).join(",") },
      { method: "post" },
    );
  }

  if (booked) {
    return (
      <SlotsFrame data={data}>
        <section className={styles.panel} aria-labelledby="booked-heading">
          <h2 id="booked-heading">Booked</h2>
          <p role="status">Your booking is confirmed. A confirmation email is on its way.</p>
          <BookingLines booking={booked} serviceName={service.name} staffName={staffName(booked.staffId)}
            yourTime={yourTime(booked)} />
          <button type="button" className={styles.secondary} onClick={() => setReview(null)}>Book another time</button>
        </section>
      </SlotsFrame>
    );
  }

  if (review && !refused) {
    const affected = error?.code === "ConfirmationRequired"
      ? (error.details.affectedBookings as AffectedBooking[] | undefined) ?? []
      : [];
    const submitting = fetcher.state !== "idle";
    return (
      <SlotsFrame data={data}>
        <section className={styles.panel} aria-labelledby="review-heading">
          <h2 id="review-heading" tabIndex={-1} ref={reviewHeading}>Review your booking</h2>
          <p>{service.name} with {staffName(review.slot.staffId)}</p>
          <p>When: {formatRange(review.slot.startUtc, review.slot.endUtc, business.timeZone)}</p>
          {yourTime(review.slot) && <p>Your time: {yourTime(review.slot)}</p>}
          <p>Duration: {formatDuration(review.slot.startUtc, review.slot.endUtc)}</p>
          <p>Price: {service.price}</p>
          {affected.length > 0
            ? (
              <RebookWarning businessName={business.name} bookings={affected} describe={(b) => {
                const own = yourTime(b);
                return `${formatRange(b.startUtc, b.endUtc, business.timeZone)} with ${staffName(b.staffId)}`
                  + (own ? `. Your time: ${own}` : "");
              }} />
            )
            : error && <ErrorMessage error={error} />}
          <div className={styles.actions}>
            <button type="button" className={styles.primary} disabled={submitting}
              onClick={() => book(review.slot, review.key, affected)}>
              {submitting ? "Booking…" : affected.length > 0 ? "Book anyway" : "Book"}
            </button>
            <button type="button" className={styles.secondary} onClick={() => setReview(null)}>Choose another time</button>
          </div>
        </section>
      </SlotsFrame>
    );
  }

  // Staff who have slots in the range, in listStaff's order (decided 2026-11-27).
  const withSlots = new Set((slots ?? []).map((s) => s.staffId));
  const staffOptions = Object.keys(staffNames).filter((id) => withSlots.has(id));
  const shownStaff = staffOptions.includes(staffFilter) ? staffFilter : "";
  const shown = (slots ?? []).filter((s) => !shownStaff || s.staffId === shownStaff);
  const byDate = new Map<string, Slot[]>();
  for (const slot of shown) {
    const date = localDateOf(slot.startUtc, business.timeZone);
    byDate.set(date, [...(byDate.get(date) ?? []), slot]);
  }

  return (
    <SlotsFrame data={data}>
      {refused && review && <Alert><p>{refusalMessage(refused, review.slot)}</p></Alert>}
      {slots && slots.length === 0 && <p>No open times in these dates. Try later dates.</p>}
      {staffOptions.length > 0 && (
        <div className={styles.filter}>
          <label htmlFor="staff-filter">Staff</label>
          <select id="staff-filter" value={shownStaff} onChange={(e) => setStaffFilter(e.target.value)}>
            <option value="">Anyone</option>
            {staffOptions.map((id) => <option key={id} value={id}>{staffNames[id]}</option>)}
          </select>
        </div>
      )}
      {[...byDate].map(([date, daySlots]) => (
        <section key={date} aria-labelledby={`date-${date}`} className={styles.day}>
          <h2 id={`date-${date}`}>{formatDate(date)}</h2>
          <ul className={styles.slots}>
            {daySlots.map((slot) => (
              <li key={`${slot.staffId}-${slot.startUtc}`}>
                <button type="button" className={styles.slot}
                  onClick={() => setReview({ slot, key: crypto.randomUUID() })}>
                  <span>{formatTimes(slot.startUtc, slot.endUtc, business.timeZone)} with {staffName(slot.staffId)}</span>
                  {yourTime(slot) && <span className={styles.yourTime}>Your time: {yourTime(slot)}</span>}
                </button>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </SlotsFrame>
  );
}

/** The page frame: heading, the business's zone, and the date range with Earlier and Later (ADR 0005). */
function SlotsFrame({ data, children }: { data: SlotsData; children: React.ReactNode }) {
  const { business, service, from, to, rangeError } = data;
  const length = Math.max(rangeDays(from, to), 1);
  const fields = rangeError?.code === "RangeTooLong" ? { to: "The maximum range is 30 days." } : fieldErrors(rangeError);
  const shift = (days: number) => `?from=${addDays(from, days)}&to=${addDays(to, days)}`;
  return (
    <Page title={service.name}>
      <p><Link to={`/b/${business.id}`}>All services at {business.name}</Link></p>
      <p>Times are in {business.timeZone}, the business's time zone.</p>
      <Form method="get" className={`${formStyles.form} ${styles.range}`} key={`${from}/${to}`}>
        <TextField label="From" name="from" type="date" autoComplete="off" defaultValue={from} error={fields.from} />
        <TextField label="To" name="to" type="date" autoComplete="off" defaultValue={to} error={fields.to} />
        <button type="submit">Show times</button>
      </Form>
      <p className={styles.paging}>
        <Link to={shift(-length)}>Earlier</Link>
        <Link to={shift(length)}>Later</Link>
      </p>
      {children}
    </Page>
  );
}

/** R4: the bookings that still stand, and that the customer must contact the business to cancel them. */
function RebookWarning({ businessName, bookings, describe }: {
  businessName: string; bookings: AffectedBooking[]; describe: (b: AffectedBooking) => string;
}) {
  const several = bookings.length > 1;
  return (
    <div className={styles.warning}>
      <p>
        {several
          ? `You already have bookings with ${businessName} that are too close to cancel online. They still stand if you book this one:`
          : `You already have a booking with ${businessName} that is too close to cancel online. It still stands if you book this one:`}
      </p>
      <ul>{bookings.map((b) => <li key={b.bookingId}>{describe(b)}</li>)}</ul>
      <p>{several ? `To cancel them, contact ${businessName}.` : `To cancel it, contact ${businessName}.`}</p>
    </div>
  );
}

function BookingLines({ booking, serviceName, staffName, yourTime }: {
  booking: Booking; serviceName: string; staffName: string; yourTime: string | null;
}) {
  return (
    <>
      <p>{serviceName} with {staffName}</p>
      <p>When: {formatRange(booking.startUtc, booking.endUtc, booking.businessTimeZone)}</p>
      {yourTime && <p>Your time: {yourTime}</p>}
      <p>Duration: {formatDuration(booking.startUtc, booking.endUtc)}</p>
    </>
  );
}

export const serviceSlotsRoute: RouteObject = {
  path: "/b/:businessId/services/:serviceId",
  loader: slotsLoader,
  action: bookAction,
  element: <SlotsPage />,
};
