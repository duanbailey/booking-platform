import type { ReactNode } from "react";
import styles from "./Page.module.css";

const appName = "Booking platform";

/**
 * A page's frame: its main landmark, its level-1 heading, and the document title (WCAG 2.4.2; React 19 moves the
 * <title> into the document head). Every screen renders one.
 */
export function Page({ title, children }: { title: string; children: ReactNode }) {
  return (
    <main className={styles.page}>
      <title>{`${title} · ${appName}`}</title>
      <h1>{title}</h1>
      {children}
    </main>
  );
}
