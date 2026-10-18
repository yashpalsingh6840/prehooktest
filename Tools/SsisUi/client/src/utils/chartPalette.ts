/**
 * Chart color palette -- a validated default categorical set plus a fixed
 * status set, scoped to this app's light-only UI (see the dataviz skill's
 * `references/palette.md` for the full method; this file is just the
 * parameters that method takes, trimmed to what this app actually uses).
 */

/** Fixed hue order -- never cycled. A chart with more categories than slots
 * folds extras into a single accent or "Other" rather than reusing early
 * slots. Order itself is the CVD-safety mechanism; don't reorder casually. */
export const CATEGORICAL: readonly string[] = [
  "#2a78d6", // 1 blue
  "#eb6834", // 2 orange
  "#1baf7a", // 3 aqua
  "#eda100", // 4 yellow
  "#e87ba4", // 5 magenta
  "#008300", // 6 green
  "#6250d6", // 7 violet
  "#e34948", // 8 red
];

/** Reserved for severity -- never reused as a plain series color. */
export const STATUS = {
  good: "#0ca30c",
  warning: "#fab219",
  serious: "#ec835a",
  critical: "#d03b3b",
} as const;

/** The default single hue for a magnitude-by-category chart with no
 * per-category identity need (an unbounded or purely-ranked category set,
 * e.g. "coverage % by package"). Same hex as categorical slot 1. */
export const CHART_ACCENT = CATEGORICAL[0];

/** Chart chrome, matched to this app's existing white/gray Tailwind surface
 * rather than the skill's own reference hex -- same roles, different values. */
export const CHART_CHROME = {
  gridline: "#e5e7eb", // tailwind gray-200
  baseline: "#9ca3af", // tailwind gray-400
  axisText: "#6b7280", // tailwind gray-500
  valueText: "#374151", // tailwind gray-700
};
