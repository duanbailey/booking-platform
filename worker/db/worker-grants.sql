-- The notification worker's database privileges (specs/07-nfr.md; T26, decided 2026-11-24). Column-level, so the
-- worker can never read or write users.password_hash, and on user_invites and password_resets it may update only
-- token_hash and expires_at, never insert or delete rows. {{database}} and {{user}} are replaced by whoever applies
-- the file: the worker's Jest global setup for every test run, and the infrastructure in T27. Keep it in step with
-- every query the worker makes; the tests connect as a user made from this file, so a missing grant fails them.

-- Contact details read at send time (T23).
GRANT SELECT (id, email, name, time_zone) ON `{{database}}`.users TO {{user}};
-- Idempotent sending (T23): insert a Pending row, find it, mark it Sent or Failed.
GRANT SELECT, INSERT, UPDATE ON `{{database}}`.notifications TO {{user}};
-- Booking emails (T24) and reminders (T25).
GRANT SELECT (id, name, owner_user_id, time_zone) ON `{{database}}`.businesses TO {{user}};
GRANT SELECT (id, name, business_id) ON `{{database}}`.services TO {{user}};
GRANT SELECT (id, user_id) ON `{{database}}`.staff TO {{user}};
GRANT SELECT (id, staff_id, service_id, customer_id, start_utc, end_utc, status) ON `{{database}}`.bookings TO {{user}};
-- Invite and recovery links (T26): read which row is used or newest, write only the token hash and expiry.
GRANT SELECT (id, user_id, used_at, created_at), UPDATE (token_hash, expires_at) ON `{{database}}`.user_invites TO {{user}};
GRANT SELECT (id, user_id, used_at, created_at), UPDATE (token_hash, expires_at) ON `{{database}}`.password_resets TO {{user}};
