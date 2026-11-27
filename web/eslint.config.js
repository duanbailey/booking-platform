import js from "@eslint/js";
import globals from "globals";
import jsxA11y from "eslint-plugin-jsx-a11y";
import reactHooks from "eslint-plugin-react-hooks";
import tseslint from "typescript-eslint";

// Feature-sliced design (T33): each layer imports only from the layers below it.
const layers = ["app", "pages", "widgets", "features", "entities", "shared"];

/** Forbids imports of the layers above `layer`, by alias (@/pages/...) or relative path (../../pages/...). */
function layerRule(layer) {
  const above = layers.slice(0, layers.indexOf(layer));
  if (above.length === 0) return [];
  return [
    {
      files: [`src/${layer}/**/*.{ts,tsx}`],
      rules: {
        "no-restricted-imports": [
          "error",
          {
            patterns: [
              {
                regex: String.raw`^(@/|(\.\./)+)(${above.join("|")})(/|$)`,
                message: `The ${layer} layer may import only from layers below it (${layers.slice(layers.indexOf(layer) + 1).join(", ") || "none"}); feature-sliced design, T33.`,
              },
            ],
          },
        ],
      },
    },
  ];
}

export default tseslint.config(
  { ignores: ["dist", "node_modules", "src/shared/api/schema.d.ts"] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ["**/*.{ts,tsx}"],
    languageOptions: { globals: { ...globals.browser } },
    plugins: { "react-hooks": reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      "@typescript-eslint/no-unused-vars": ["error", { argsIgnorePattern: "^_" }],
    },
  },
  { files: ["src/**/*.tsx"], ...jsxA11y.flatConfigs.recommended },
  { files: ["vite.config.ts", "eslint.config.js", "scripts/**"], languageOptions: { globals: { ...globals.node } } },
  ...layers.flatMap(layerRule),
);
