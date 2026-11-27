import axe from "axe-core";
import { expect } from "vitest";

/**
 * Fails with the list of axe violations in `container` (WCAG 2.2 AA rules, specs/07-nfr.md). Colour contrast is
 * left out: jsdom doesn't lay out or paint, so axe can't compute it; it is checked in the manual pass (T44).
 */
export async function expectNoAxeViolations(container: Element): Promise<void> {
  const results = await axe.run(container, {
    runOnly: { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"] },
    rules: { "color-contrast": { enabled: false }, region: { enabled: false } },
  });
  const violations = results.violations.map(
    (v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(" ")).join(", ")})`,
  );
  expect(violations).toEqual([]);
}
