import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach, vi } from "vitest";
import { session } from "@/entities/session";

afterEach(() => {
  cleanup();
  session.reset();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});
