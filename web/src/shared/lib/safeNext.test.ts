import { describe, expect, it } from "vitest";
import { safeNext } from "./safeNext";

// T34: login and sign-up go on to ?next= only when it is a path on this origin, so a link can't send a user to
// another site after they log in.
describe("safeNext", () => {
  it.each(["/home", "/b/123?x=1#y", "/"])("accepts the app path %s", (next) => {
    expect(safeNext(next)).toBe(next);
  });

  it.each([null, "", "home", "//evil.example", "/\\evil.example", "https://evil.example/home", "javascript:alert(1)"])(
    "refuses %s", (next) => {
      expect(safeNext(next)).toBeNull();
    },
  );
});
