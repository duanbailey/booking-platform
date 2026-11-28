import { useEffect, useId, type ReactNode, type RefObject } from "react";
import { ApiError } from "../api/client";
import styles from "./Field.module.css";

/** A ValidationFailed error's field problems (`details.fields`), or none (specs/04-api.openapi.yaml, Error). */
export function fieldErrors(error: unknown): Record<string, string> {
  if (!(error instanceof ApiError) || error.code !== "ValidationFailed") return {};
  const fields = error.details.fields;
  return fields && typeof fields === "object" ? (fields as Record<string, string>) : {};
}

interface FrameProps {
  label: string;
  error: string | undefined;
  children: (control: { id: string; "aria-invalid"?: true; "aria-describedby"?: string }) => ReactNode;
}

/** A label, a control and the control's error, linked for assistive technology. */
function FieldFrame({ label, error, children }: FrameProps) {
  const id = useId();
  const errorId = `${id}-error`;
  return (
    <div className={styles.field}>
      <label htmlFor={id}>{label}</label>
      {children({ id, ...(error ? { "aria-invalid": true, "aria-describedby": errorId } : {}) })}
      {error && <p id={errorId} className={styles.error}>{error}</p>}
    </div>
  );
}

interface TextFieldProps {
  label: string;
  name: string;
  type?: "text" | "email" | "password" | "date";
  autoComplete: string;
  error?: string;
  required?: boolean;
  defaultValue?: string;
}

/** A text input with its label and error. Forms don't validate in the browser; the API's answer is shown (T34). */
export function TextField({ label, name, type = "text", autoComplete, error, required, defaultValue }: TextFieldProps) {
  return (
    <FieldFrame label={label} error={error}>
      {(control) => (
        <input {...control} name={name} type={type} autoComplete={autoComplete} required={required}
          defaultValue={defaultValue} />
      )}
    </FieldFrame>
  );
}

interface SelectFieldProps {
  label: string;
  name: string;
  options: string[];
  defaultValue: string;
  error?: string;
  required?: boolean;
}

export function SelectField({ label, name, options, defaultValue, error, required }: SelectFieldProps) {
  return (
    <FieldFrame label={label} error={error}>
      {(control) => (
        <select {...control} name={name} defaultValue={defaultValue} required={required}>
          {options.map((option) => <option key={option} value={option}>{option}</option>)}
        </select>
      )}
    </FieldFrame>
  );
}

/** The mismatch problem when a new password and its confirmation differ (the API has no confirmation field). */
export function passwordMismatch(password: FormDataEntryValue | null, confirm: FormDataEntryValue | null) {
  return password === confirm ? null : "The passwords don't match.";
}

/** Moves focus to the form's first invalid field whenever `answer` (the action's result) changes. */
export function useFocusFirstInvalid(form: RefObject<HTMLFormElement | null>, answer: unknown) {
  useEffect(() => {
    if (answer) form.current?.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus();
  }, [form, answer]);
}
