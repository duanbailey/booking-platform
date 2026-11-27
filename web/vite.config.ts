import { defineConfig } from "vitest/config";
import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";

// The API's local address (the "http" launch profile); override with BOOKING_API_URL.
const api = process.env.BOOKING_API_URL ?? "http://localhost:5107";

export default defineConfig({
  plugins: [react()],
  resolve: { alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) } },
  server: {
    // Same origin as in production (ADR 0012): the app calls relative /v1 URLs and the dev server forwards them.
    proxy: {
      "/v1": { target: api, changeOrigin: true },
    },
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test/setup.ts"],
    css: { modules: { classNameStrategy: "non-scoped" } },
  },
});
