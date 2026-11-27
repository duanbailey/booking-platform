import { useEffect, useRef } from "react";
import type { ActionFunctionArgs, RouteObject } from "react-router";
import { Form, useActionData, useNavigate, useNavigation } from "react-router";
import { session, type AuthToken } from "@/entities/session";
import { ApiError } from "@/shared/api/client";
import { Alert, ErrorMessage } from "@/shared/ui/ErrorMessage";
import { fieldErrors, passwordMismatch, TextField, useFocusFirstInvalid } from "@/shared/ui/Field";
import { Page } from "@/shared/ui/Page";
import styles from "@/shared/ui/Form.module.css";

// T35: story "User can change their password". The API revokes the user's other logins and returns new tokens,
// so this tab stays logged in (the new refresh cookie is shared by the app's other tabs).

type ChangeAnswer = { changed: true } | { error: ApiError } | { fields: Record<string, string> };

async function changeAction({ request }: ActionFunctionArgs): Promise<ChangeAnswer> {
  const form = await request.formData();
  const mismatch = passwordMismatch(form.get("newPassword"), form.get("confirmPassword"));
  if (mismatch) return { fields: { confirmPassword: mismatch } };
  try {
    const tokens = await session.authFetch<AuthToken>("/v1/auth/change-password", {
      method: "POST",
      body: { currentPassword: String(form.get("currentPassword") ?? ""), newPassword: String(form.get("newPassword") ?? "") },
      signal: request.signal,
    });
    session.setTokens(tokens);
  } catch (error) {
    if (error instanceof ApiError) return { error };
    throw error;
  }
  return { changed: true };
}

function problems(answer: ChangeAnswer | undefined): Record<string, string> {
  if (!answer || "changed" in answer) return {};
  if ("fields" in answer) return answer.fields;
  if (answer.error.code === "CurrentPasswordIncorrect") return { currentPassword: "The current password is incorrect." };
  return fieldErrors(answer.error);
}

function ChangePasswordPage() {
  const answer = useActionData<ChangeAnswer>();
  const submitting = useNavigation().state === "submitting";
  const navigate = useNavigate();
  const form = useRef<HTMLFormElement>(null);
  useFocusFirstInvalid(form, answer);
  useEffect(() => {
    if (answer && "changed" in answer) void navigate("/profile", { state: { passwordChanged: true } });
  }, [answer, navigate]);
  const error = answer && "error" in answer ? answer.error : undefined;
  const fields = problems(answer);
  const shared = error && !["ValidationFailed", "CurrentPasswordIncorrect"].includes(error.code);

  return (
    <Page title="Change password">
      {shared && <ErrorMessage error={error} />}
      {fields.body && <Alert><p>{fields.body}</p></Alert>}
      <Form method="post" noValidate ref={form} className={styles.form}>
        <TextField label="Current password" name="currentPassword" type="password" autoComplete="current-password"
          required error={fields.currentPassword} />
        <TextField label="New password" name="newPassword" type="password" autoComplete="new-password" required
          error={fields.newPassword} />
        <TextField label="Confirm new password" name="confirmPassword" type="password" autoComplete="new-password"
          required error={fields.confirmPassword} />
        <button type="submit" disabled={submitting}>{submitting ? "Changing password…" : "Change password"}</button>
      </Form>
    </Page>
  );
}

export const changePasswordRoute: RouteObject = {
  path: "/change-password", action: changeAction, element: <ChangePasswordPage />,
};
