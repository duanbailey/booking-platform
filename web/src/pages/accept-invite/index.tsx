import type { RouteObject } from "react-router";
import { Link } from "react-router";
import { setPasswordAction, SetPasswordPage } from "@/features/set-password";

// T35: story "Invited user sets their password". The route is fixed by the invite email (worker/src/accountEmails.ts).
// A dead invite points to password recovery, which also works for an account without a password.
function AcceptInvitePage() {
  return (
    <SetPasswordPage
      title="Set your password"
      invalidCode="InviteInvalid"
      invalidMessage={<>
        This invite link is no longer valid. Ask whoever invited you to send a new one, or{" "}
        <Link to="/forgot-password">set your password with a recovery link</Link>.
      </>}
    />
  );
}

export const acceptInviteRoute: RouteObject = {
  path: "/accept-invite",
  action: setPasswordAction("/v1/auth/accept-invite"),
  element: <AcceptInvitePage />,
};
