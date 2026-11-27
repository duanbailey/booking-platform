import { useState } from "react";
import { session } from "@/entities/session";
import { ErrorMessage } from "@/shared/ui/ErrorMessage";

/**
 * Logs out (T34). The app follows the session to login; if the call fails, the error is shown and the user stays
 * logged in, since the cookie would log them back in anyway.
 */
export function LogoutButton() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  async function logOut() {
    setBusy(true);
    setError(null);
    try {
      await session.logOut();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <button type="button" onClick={logOut} disabled={busy}>{busy ? "Logging out…" : "Log out"}</button>
      {error !== null && <ErrorMessage error={error} />}
    </>
  );
}
