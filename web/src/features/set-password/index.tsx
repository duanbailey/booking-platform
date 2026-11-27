import { useRef, type ReactNode } from "react";
import type { ActionFunctionArgs } from "react-router";
import { Form, redirect, useActionData, useNavigation, useSearchParams } from "react-router";
import { session, type AuthToken } from "@/entities/session";
import { apiFetch, ApiError, type ApiPath } from "@/shared/api/client";
import { Alert, ErrorMessage } from "@/shared/ui/ErrorMessage";
import { fieldErrors, passwordMismatch, TextField, useFocusFirstInvalid } from "@/shared/ui/Field";
import { Page } from "@/shared/ui/Page";
import styles from "@/shared/ui/Form.module.css";

// T35: setting a password from an emailed link (accept-invite, reset-password). The link's token is sent with the
// password; success logs in as the link's account, replacing any current session (specs/09-ui.md).

type SetPasswordAnswer = { error: ApiError } | { fields: Record<string, string> };

/** The action for a link screen: checks the two passwords match, sends the token and password, logs in. */
export function setPasswordAction(path: ApiPath) {
  return async ({ request }: ActionFunctionArgs): Promise<SetPasswordAnswer | Response> => {
    const form = await request.formData();
    const mismatch = passwordMismatch(form.get("password"), form.get("confirmPassword"));
    if (mismatch) return { fields: { confirmPassword: mismatch } };
    try {
      const tokens = await apiFetch<AuthToken>(path, {
        method: "POST",
        body: { token: new URL(request.url).searchParams.get("token") ?? "", password: String(form.get("password") ?? "") },
        signal: request.signal,
      });
      await session.logIn(tokens);
    } catch (error) {
      if (error instanceof ApiError) return { error };
      throw error;
    }
    return redirect("/home");
  };
}

interface SetPasswordPageProps {
  title: string;
  /** The error code for a dead link. */
  invalidCode: ApiError["code"];
  /** What a dead link (or one without a token) says. */
  invalidMessage: ReactNode;
}

/** A link screen: the new password asked twice, or the dead-link message. */
export function SetPasswordPage({ title, invalidCode, invalidMessage }: SetPasswordPageProps) {
  const answer = useActionData<SetPasswordAnswer>();
  const submitting = useNavigation().state === "submitting";
  const hasToken = Boolean(useSearchParams()[0].get("token"));
  const form = useRef<HTMLFormElement>(null);
  useFocusFirstInvalid(form, answer);
  const error = answer && "error" in answer ? answer.error : undefined;
  const fields = answer && "fields" in answer ? answer.fields : fieldErrors(error);

  if (!hasToken || error?.code === invalidCode) {
    return <Page title={title}><Alert><p>{invalidMessage}</p></Alert></Page>;
  }
  return (
    <Page title={title}>
      {error && error.code !== "ValidationFailed" && <ErrorMessage error={error} />}
      {fields.body && <Alert><p>{fields.body}</p></Alert>}
      <Form method="post" noValidate ref={form} className={styles.form}>
        <TextField label="Password" name="password" type="password" autoComplete="new-password" required
          error={fields.password} />
        <TextField label="Confirm password" name="confirmPassword" type="password" autoComplete="new-password"
          required error={fields.confirmPassword} />
        <button type="submit" disabled={submitting}>{submitting ? "Setting password…" : "Set password"}</button>
      </Form>
    </Page>
  );
}
