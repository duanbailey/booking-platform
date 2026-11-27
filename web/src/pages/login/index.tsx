import { useRef } from "react";
import type { ActionFunctionArgs, RouteObject } from "react-router";
import { Form, Link, redirect, useActionData, useLocation, useNavigation, useSearchParams } from "react-router";
import { session, type AuthToken } from "@/entities/session";
import { apiFetch, ApiError } from "@/shared/api/client";
import { safeNext, withNext } from "@/shared/lib/safeNext";
import { Alert, ErrorMessage } from "@/shared/ui/ErrorMessage";
import { fieldErrors, TextField, useFocusFirstInvalid } from "@/shared/ui/Field";
import { Page } from "@/shared/ui/Page";
import styles from "@/shared/ui/Form.module.css";

// T34: stories "Owner can log in", "Staff can log in", "Customer can log in". Login never reveals whether an
// account exists (specs/07-nfr.md): InvalidCredentials has one message.

interface LoginAnswer {
  error: ApiError;
}

async function loginAction({ request }: ActionFunctionArgs): Promise<LoginAnswer | Response> {
  const form = await request.formData();
  try {
    const tokens = await apiFetch<AuthToken>("/v1/auth/login", {
      method: "POST",
      body: { email: String(form.get("email") ?? ""), password: String(form.get("password") ?? "") },
      signal: request.signal,
    });
    await session.logIn(tokens);
  } catch (error) {
    if (error instanceof ApiError) return { error };
    throw error;
  }
  return redirect(safeNext(new URL(request.url).searchParams.get("next")) ?? "/home");
}

function LoginError({ error }: { error: ApiError }) {
  if (error.code === "InvalidCredentials") return <Alert><p>The email or password is incorrect.</p></Alert>;
  if (error.code === "ValidationFailed") {
    const body = fieldErrors(error).body;
    return body ? <Alert><p>{body}</p></Alert> : null;
  }
  return <ErrorMessage error={error} />;
}

function LoginPage() {
  const answer = useActionData<LoginAnswer>();
  const submitting = useNavigation().state === "submitting";
  const next = safeNext(useSearchParams()[0].get("next"));
  const sessionEnded = (useLocation().state as { sessionEnded?: boolean } | null)?.sessionEnded === true;
  const form = useRef<HTMLFormElement>(null);
  useFocusFirstInvalid(form, answer);
  const fields = fieldErrors(answer?.error);

  return (
    <Page title="Log in">
      {sessionEnded && !answer && <p role="status">Your session has ended. Please log in again.</p>}
      {answer && <LoginError error={answer.error} />}
      <Form method="post" noValidate ref={form} className={styles.form}>
        <TextField label="Email" name="email" type="email" autoComplete="email" required error={fields.email} />
        <TextField label="Password" name="password" type="password" autoComplete="current-password" required
          error={fields.password} />
        <button type="submit" disabled={submitting}>{submitting ? "Logging in…" : "Log in"}</button>
      </Form>
      <p>No account yet? <Link to={withNext("/sign-up", next)}>Sign up</Link></p>
    </Page>
  );
}

export const loginRoute: RouteObject = { path: "/", action: loginAction, element: <LoginPage /> };
