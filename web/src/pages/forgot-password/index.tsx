import { useRef } from "react";
import type { ActionFunctionArgs, RouteObject } from "react-router";
import { Form, Link, useActionData, useNavigation } from "react-router";
import { apiFetch, ApiError } from "@/shared/api/client";
import { Alert, ErrorMessage } from "@/shared/ui/ErrorMessage";
import { fieldErrors, TextField, useFocusFirstInvalid } from "@/shared/ui/Field";
import { Page } from "@/shared/ui/Page";
import styles from "@/shared/ui/Form.module.css";

// T35: story "User can recover a forgotten password". The answer is the same whether or not the account exists
// (specs/07-nfr.md): the API always answers 202, and the page always says the same.

type ForgotAnswer = { sent: true } | { error: ApiError };

async function forgotAction({ request }: ActionFunctionArgs): Promise<ForgotAnswer> {
  const form = await request.formData();
  try {
    await apiFetch("/v1/auth/forgot-password", {
      method: "POST", body: { email: String(form.get("email") ?? "") }, signal: request.signal,
    });
  } catch (error) {
    if (error instanceof ApiError) return { error };
    throw error;
  }
  return { sent: true };
}

function ForgotPasswordPage() {
  const answer = useActionData<ForgotAnswer>();
  const submitting = useNavigation().state === "submitting";
  const form = useRef<HTMLFormElement>(null);
  useFocusFirstInvalid(form, answer);
  const error = answer && "error" in answer ? answer.error : undefined;
  const fields = fieldErrors(error);

  if (answer && "sent" in answer) {
    return (
      <Page title="Forgot password">
        <p role="status">If an account exists for that email, a link has been sent.</p>
        <p><Link to="/">Back to log in</Link></p>
      </Page>
    );
  }
  return (
    <Page title="Forgot password">
      <p>Enter your email and we'll send you a link to set a new password.</p>
      {error && error.code !== "ValidationFailed" && <ErrorMessage error={error} />}
      {fields.body && <Alert><p>{fields.body}</p></Alert>}
      <Form method="post" noValidate ref={form} className={styles.form}>
        <TextField label="Email" name="email" type="email" autoComplete="email" required error={fields.email} />
        <button type="submit" disabled={submitting}>{submitting ? "Sending…" : "Send link"}</button>
      </Form>
      <p><Link to="/">Back to log in</Link></p>
    </Page>
  );
}

export const forgotPasswordRoute: RouteObject = {
  path: "/forgot-password", action: forgotAction, element: <ForgotPasswordPage />,
};
