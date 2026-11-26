# Booking Platform

Appointment booking: C# Web API, MySQL, Node notification worker, React web frontend, AWS.

## Workflow rules
- Specs in /specs are the source of truth. If code and spec disagree, stop and ask.
- Work from /plan/tasks.md, one task at a time.
- Write tests from the acceptance criteria BEFORE implementing.
- Reference business rule IDs in tests and code comments (e.g. // R7).
- Do not add features listed as non-goals in specs/01-overview.md.

## Conventions
- C#: .NET Web API, xUnit tests, nullable reference types on
- Node: TypeScript, Jest
- React: TypeScript, React Router, feature-sliced design, Vitest + Testing Library; app in web/ (ADR 0012)
- All timestamps stored in UTC; business time zone is an IANA string
- Migrations only, never edit the schema by hand

## Key specs
@specs/01-overview.md
@specs/03-business-rules.md
@specs/09-ui.md
