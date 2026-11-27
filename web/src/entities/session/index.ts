import { useSyncExternalStore } from "react";
import { session, type SessionState } from "./model";

export { rolesOf, session, type AuthToken, type Me, type Roles, type SessionState } from "./model";

/** The session state, re-rendering when it changes. */
export function useSession(): SessionState {
  return useSyncExternalStore(session.subscribe, session.getState);
}
