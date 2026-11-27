// T33: fails when src/shared/api/schema.d.ts is not what openapi-typescript generates from
// specs/04-api.openapi.yaml, so the app's API types can't drift from the spec. Fix with: npm run generate:api
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const committed = "src/shared/api/schema.d.ts";
const dir = mkdtempSync(join(tmpdir(), "api-types-"));
const fresh = join(dir, "schema.d.ts");
try {
  execFileSync(process.execPath, ["node_modules/openapi-typescript/bin/cli.js", "../specs/04-api.openapi.yaml", "-o", fresh], { stdio: "pipe" });
  const normalise = (path) => readFileSync(path, "utf8").replace(/\r\n/g, "\n");
  if (normalise(fresh) !== normalise(committed)) {
    console.error(`${committed} is out of date with specs/04-api.openapi.yaml. Run: npm run generate:api`);
    process.exit(1);
  }
  console.log(`${committed} matches specs/04-api.openapi.yaml.`);
} finally {
  rmSync(dir, { recursive: true, force: true });
}
