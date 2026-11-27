import { ApiError } from "../api/client";
import styles from "./ErrorMessage.module.css";

const unexpected = "Something went wrong on our side. Please try again.";

/** The shared wording (specs/09-ui.md "Errors"), and whether to show the correlation ID to quote. */
function describe(error: unknown): { text: string; reference?: string } {
  if (!(error instanceof ApiError)) return { text: unexpected };
  switch (error.code) {
    case "NetworkError":
      return { text: "We couldn't reach the server. Check your connection and try again." };
    case "NotFound":
      return { text: "We couldn't find what you were looking for." };
    case "Forbidden":
      return { text: "You don't have permission to do that." };
    case "RequestRejected":
      return error.status === 429
        ? { text: "Too many attempts. Please wait a minute and try again." }
        : { text: unexpected, reference: error.correlationId };
    case "InternalError":
      return { text: unexpected, reference: error.correlationId };
    default:
      return { text: error.message };
  }
}

/** Shows an error from an API call (or anything thrown) in an alert. Screens handle their own codes first. */
export function ErrorMessage({ error }: { error: unknown }) {
  const { text, reference } = describe(error);
  return (
    <div role="alert" className={styles.error}>
      <p>{text}</p>
      {reference && <p>If it keeps happening, quote reference {reference}.</p>}
    </div>
  );
}
