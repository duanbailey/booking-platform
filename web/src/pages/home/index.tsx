import type { RouteObject } from "react-router";
import { rolesOf, useSession } from "@/entities/session";
import { Page } from "@/shared/ui/Page";

// T34: where login and sign-up land: a section for each role the user holds (specs/09-ui.md "Home"); every user
// who isn't an admin is a customer. Each screen task adds its links to its role's section.

const sections = [
  { role: "admin", heading: "Admin", text: "Create owners and their businesses." },
  { role: "owner", heading: "Owner", text: "Manage your business, its services and its staff." },
  { role: "staff", heading: "Staff", text: "See your schedule and manage your availability and time off." },
  { role: "customer", heading: "Customer", text: "Book appointments and manage your bookings." },
] as const;

function HomePage() {
  const { user } = useSession();
  if (!user) return null;
  const roles = rolesOf(user);
  return (
    <Page title="Home">
      {sections.filter((s) => roles[s.role]).map((s) => (
        <section key={s.role} aria-labelledby={`home-${s.role}`}>
          <h2 id={`home-${s.role}`}>{s.heading}</h2>
          <p>{s.text}</p>
        </section>
      ))}
    </Page>
  );
}

export const homeRoute: RouteObject = { path: "/home", element: <HomePage /> };
