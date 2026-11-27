import { useMemo, useRef } from "react";
import type { ActionFunctionArgs, RouteObject } from "react-router";
import { Form, Link, redirect, useActionData, useNavigation, useSearchParams } from "react-router";
import { session, type AuthToken } from "@/entities/session";
import { apiFetch, ApiError } from "@/shared/api/client";
import { safeNext, withNext } from "@/shared/lib/safeNext";
import { browserTimeZone, timeZoneOptions } from "@/shared/lib/timeZones";
import { Alert, ErrorMessage } from "@/shared/ui/ErrorMessage";
import { fieldErrors, SelectField, TextField, useFocusFirstInvalid } from "@/shared/ui/Field";
import { Page } from "@/shared/ui/Page";
import styles from "@/shared/ui/Form.module.css";

// T34: story "Customer can sign up". Sign-up does say when an email is taken, as the story requires
// (specs/07-nfr.md). The password is asked twice; the API has no confirmation field, so the app checks the match
// before sending (specs/09-ui.md "Sign up").

/** The action's answer: the API's error, or the app's own field problems found before sending. */
type SignUpAnswer = { error: ApiError } | { fields: Record<string, string> };

async function signUpAction({ request }: ActionFunctionArgs): Promise<SignUpAnswer | Response> {
  const form = await request.formData();
  const text = (name: string) => String(form.get(name) ?? "");
  if (text("password") !== text("confirmPassword")) {
    return { fields: { confirmPassword: "The passwords don't match." } };
  }
  try {
    const tokens = await apiFetch<AuthToken>("/v1/auth/register", {
      method: "POST",
      body: { name: text("name"), email: text("email"), password: text("password"), timeZone: text("timeZone") },
      signal: request.signal,
    });
    await session.logIn(tokens);
  } catch (error) {
    if (error instanceof ApiError) return { error };
    throw error;
  }
  return redirect(safeNext(new URL(request.url).searchParams.get("next")) ?? "/home");
}

function SignUpError({ error, next }: { error: ApiError; next: string | null }) {
  if (error.code === "EmailTaken") {
    return (
      <Alert>
        <p>An account with that email already exists. <Link to={withNext("/", next)}>Log in</Link></p>
      </Alert>
    );
  }
  if (error.code === "ValidationFailed") {
    const body = fieldErrors(error).body;
    return body ? <Alert><p>{body}</p></Alert> : null;
  }
  return <ErrorMessage error={error} />;
}

function SignUpPage() {
  const answer = useActionData<SignUpAnswer>();
  const submitting = useNavigation().state === "submitting";
  const next = safeNext(useSearchParams()[0].get("next"));
  const zones = useMemo(() => timeZoneOptions(), []);
  const form = useRef<HTMLFormElement>(null);
  useFocusFirstInvalid(form, answer);
  const error = answer && "error" in answer ? answer.error : undefined;
  const fields = answer && "fields" in answer ? answer.fields : fieldErrors(error);

  return (
    <Page title="Sign up">
      {error && <SignUpError error={error} next={next} />}
      <Form method="post" noValidate ref={form} className={styles.form}>
        <TextField label="Name" name="name" autoComplete="name" required error={fields.name} />
        <TextField label="Email" name="email" type="email" autoComplete="email" required error={fields.email} />
        <TextField label="Password" name="password" type="password" autoComplete="new-password" required
          error={fields.password} />
        <TextField label="Confirm password" name="confirmPassword" type="password" autoComplete="new-password"
          required error={fields.confirmPassword} />
        <SelectField label="Time zone" name="timeZone" options={zones} defaultValue={browserTimeZone()} required
          error={fields.timeZone} />
        <button type="submit" disabled={submitting}>{submitting ? "Signing up…" : "Sign up"}</button>
      </Form>
      <p>Already have an account? <Link to={withNext("/", next)}>Log in</Link></p>
    </Page>
  );
}

export const signUpRoute: RouteObject = { path: "/sign-up", action: signUpAction, element: <SignUpPage /> };
