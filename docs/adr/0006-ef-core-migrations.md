# ADR 0006: Use EF Core migrations for the schema

- **Status:** Accepted
- **Date:** 2026-11-16
- **Related rules:** R1, R7 ([specs/03-business-rules.md](../../specs/03-business-rules.md))
- **Related specs:** [specs/05-data-model.md](../../specs/05-data-model.md), [ADR 0002](0002-booking-concurrency-row-lock.md)

## Context

CLAUDE.md requires every schema change to go through a migration. No migration tool or data-access library had been chosen. The design relies on SQL that ORMs do not generate on their own: `SELECT ... FOR UPDATE` on the staff row (ADR 0002), MySQL enums, and many unique constraints that guard business rules.

## Options considered

1. **EF Core migrations** (chosen). Migrations are generated from C# entity classes and run against MySQL through an EF Core provider. This is the most common choice in .NET teams.
2. **DbUp with Dapper.** Plain `.sql` migration files and hand-written queries. Full control of SQL, but no ORM.
3. **FluentMigrator.** C# migrations with a fluent API that can be rolled back. More ceremony, and MySQL-specific features still need raw SQL.

## Decision

The schema is managed with EF Core migrations, and EF Core is the main data-access library. Where EF Core cannot express something, the migration or query uses raw SQL (`migrationBuilder.Sql`, `FromSql`/`ExecuteSql`). Raw SQL is expected for the ADR 0002 row lock and may be needed for enums and check constraints.

## Consequences

- The entity model and the migrations must stay in step with specs/05-data-model.md. Migration tests (T2) check the constraints that guard business rules, rather than trusting the generated SQL.
- The ADR 0002 lock is taken with raw SQL inside an EF Core transaction, and a real concurrent test against MySQL (T14) shows it works.
- Migrations are applied by the EF Core migration bundle or `Database.Migrate()`; how this runs in deployment is decided with the API hosting (T27).
