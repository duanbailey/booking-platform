import { useMemo, useRef } from "react";
import type { ActionFunctionArgs, RouteObject } from "react-router";
import { Form, Link, useActionData, useLocation, useNavigation } from "react-router";
import { session, useSession, type Me } from "@/entities/session";
import { ApiError } from "@/shared/api/client";
import { timeZoneOptions } from "@/shared/lib/timeZones";
import { Alert, ErrorMessage } from "@/shared/ui/ErrorMessage";
import { fieldErrors, SelectField, TextField, useFocusFirstInvalid } from "@/shared/ui/Field";
import { Page } from "@/shared/ui/Page";
import styles from "@/shared/ui/Form.module.css";

// T35: the profile (getMe, updateMe): name and time zone; the email is shown but the API can't change it.

type ProfileAnswer = { saved: true } | { error: ApiError };

async function profileAction({ request }: ActionFunctionArgs): Promise<ProfileAnswer> {
  const form = await request.formData();
  try {
    const user = await session.authFetch<Me>("/v1/me", {
      method: "PATCH",
      body: { name: String(form.get("name") ?? ""), timeZone: String(form.get("timeZone") ?? "") },
      signal: request.signal,
    });
    session.setUser(user);
  } catch (error) {
    if (error instanceof ApiError) return { error };
    throw error;
  }
  return { saved: true };
}

function ProfilePage() {
  const { user } = useSession();
  const answer = useActionData<ProfileAnswer>();
  const submitting = useNavigation().state === "submitting";
  const passwordChanged = (useLocation().state as { passwordChanged?: boolean } | null)?.passwordChanged === true;
  const zones = useMemo(() => timeZoneOptions(user?.timeZone), [user?.timeZone]);
  const form = useRef<HTMLFormElement>(null);
  useFocusFirstInvalid(form, answer);
  if (!user) return null;
  const error = answer && "error" in answer ? answer.error : undefined;
  const fields = fieldErrors(error);

  return (
    <Page title="Profile">
      {answer && "saved" in answer && <p role="status">Your profile has been saved.</p>}
      {!answer && passwordChanged && <p role="status">Your password has been changed.</p>}
      {error && error.code !== "ValidationFailed" && <ErrorMessage error={error} />}
      {fields.body && <Alert><p>{fields.body}</p></Alert>}
      <p>Email: {user.email}</p>
      <Form method="post" noValidate ref={form} className={styles.form}>
        <TextField label="Name" name="name" autoComplete="name" required defaultValue={user.name} error={fields.name} />
        <SelectField label="Time zone" name="timeZone" options={zones} defaultValue={user.timeZone} required
          error={fields.timeZone} />
        <button type="submit" disabled={submitting}>{submitting ? "Saving…" : "Save"}</button>
      </Form>
      <p><Link to="/change-password">Change password</Link></p>
    </Page>
  );
}

export const profileRoute: RouteObject = { path: "/profile", action: profileAction, element: <ProfilePage /> };
