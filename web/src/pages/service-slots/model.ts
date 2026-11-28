import type { ActionFunctionArgs, LoaderFunctionArgs } from "react-router";
import { session } from "@/entities/session";
import { ApiError } from "@/shared/api/client";
import {
  allPages, pageQuery, type Booking, type Business, type Page, type Service, type Slot, type Staff,
} from "@/shared/api/pages";
import { addDays, todayIn } from "@/shared/lib/time";

// T36: data for a service's slots page and the booking action (specs/09-ui.md "Find a slot", "Book").

export interface SlotsData {
  business: Business;
  service: Service;
  /** Staff names by staffId (listStaff). */
  staffNames: Record<string, string>;
  /** The business-local dates shown, both inclusive (ADR 0005). */
  from: string;
  to: string;
  /** The open slots, or null when the range was refused (rangeError). */
  slots: Slot[] | null;
  /** RangeTooLong or ValidationFailed for the dates. */
  rangeError: ApiError | null;
}

export async function slotsLoader({ params, request }: LoaderFunctionArgs): Promise<SlotsData> {
  const businessId = encodeURIComponent(params.businessId ?? "");
  const serviceId = encodeURIComponent(params.serviceId ?? "");
  const signal = request.signal;
  const [business, service, staff] = await Promise.all([
    session.authFetch<Business>(`/v1/businesses/${businessId}`, { signal }),
    session.authFetch<Service>(`/v1/services/${serviceId}`, { signal }),
    allPages((cursor) => session.authFetch<Page<Staff>>(`/v1/businesses/${businessId}/staff${pageQuery(cursor)}`, { signal })),
  ]);
  const query = new URL(request.url).searchParams;
  // Default: today and the next 6 days in the business's zone (decided 2026-11-27).
  const from = query.get("from") || todayIn(business.timeZone);
  const to = query.get("to") || addDays(from, 6);
  let slots: Slot[] | null = null;
  let rangeError: ApiError | null = null;
  try {
    const answer = await session.authFetch<{ slots: Slot[] }>(
      `/v1/services/${serviceId}/slots?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`, { signal });
    slots = answer.slots;
  } catch (error) {
    if (!(error instanceof ApiError) || !["RangeTooLong", "ValidationFailed"].includes(error.code)) throw error;
    rangeError = error;
  }
  return {
    business, service, staffNames: Object.fromEntries(staff.map((s) => [s.staffId, s.name])), from, to, slots, rangeError,
  };
}

/** The booking action's answer, tagged with the review's Idempotency-Key so the screen can match it. */
export type BookAnswer = { key: string } & ({ booked: Booking } | { error: ApiError });

/** Books the reviewed slot with one Idempotency-Key per review (T29); `confirm` carries R4's confirmed ids. */
export async function bookAction({ params, request }: ActionFunctionArgs): Promise<BookAnswer> {
  const form = await request.formData();
  const key = String(form.get("key") ?? "");
  const confirm = String(form.get("confirm") ?? "");
  try {
    const booked = await session.authFetch<Booking>("/v1/bookings", {
      method: "POST",
      headers: { "Idempotency-Key": key },
      body: {
        serviceId: params.serviceId,
        staffId: String(form.get("staffId") ?? ""),
        startUtc: String(form.get("startUtc") ?? ""),
        ...(confirm ? { confirm: { affectedBookingIds: confirm.split(",") } } : {}),
      },
      signal: request.signal,
    });
    return { key, booked };
  } catch (error) {
    if (error instanceof ApiError) return { key, error };
    throw error;
  }
}
