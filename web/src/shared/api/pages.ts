import type { components } from "./schema";

export type Business = components["schemas"]["Business"];
export type Service = components["schemas"]["Service"];
export type Staff = components["schemas"]["Staff"];
export type Slot = components["schemas"]["Slot"];
export type Booking = components["schemas"]["Booking"];
export type AffectedBooking = components["schemas"]["AffectedBooking"];

/** A page of a list (PageBase). */
export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

/** Every item of a paged list, reading pages until `nextCursor` is null. */
export async function allPages<T>(fetchPage: (cursor: string | null) => Promise<Page<T>>): Promise<T[]> {
  const items: T[] = [];
  let cursor: string | null = null;
  do {
    const page: Page<T> = await fetchPage(cursor);
    items.push(...page.items);
    cursor = page.nextCursor;
  } while (cursor !== null);
  return items;
}

/** "?limit=200", plus the cursor when there is one: the largest page, to read a list in few calls. */
export function pageQuery(cursor: string | null): string {
  return `?limit=200${cursor === null ? "" : `&cursor=${encodeURIComponent(cursor)}`}`;
}
