import type { RouteObject } from "react-router";
import { Link } from "react-router";
import { setPasswordAction, SetPasswordPage } from "@/features/set-password";

// T35: story "User can recover a forgotten password", from the recovery email's link (route fixed by
// worker/src/accountEmails.ts).
function ResetPasswordPage() {
  return (
    <SetPasswordPage
      title="Set a new password"
      invalidCode="ResetLinkInvalid"
      invalidMessage={<>This link is no longer valid. <Link to="/forgot-password">Request a new one</Link>.</>}
    />
  );
}

export const resetPasswordRoute: RouteObject = {
  path: "/reset-password",
  action: setPasswordAction("/v1/auth/reset-password"),
  element: <ResetPasswordPage />,
};
