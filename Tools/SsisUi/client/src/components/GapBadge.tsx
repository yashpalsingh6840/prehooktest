import type { GapTier } from "../api/client";
import Badge, { type BadgeColor } from "./Badge";

const TIER_LABELS: Record<GapTier, string> = {
  Advisory: "Advisory",
  MissingDatum: "Missing Datum",
  MissingLogic: "Missing Logic",
  MissingToolSupport: "No Tool Support",
};

// Blocking tiers (a human/AI must close this before the package builds) read
// warm; Advisory (informational, never blocks generation) reads cool.
// MissingToolSupport is a tool limitation, not a failure, so it gets its own
// (purple) color rather than being folded into red.
const TIER_COLORS: Record<GapTier, BadgeColor> = {
  Advisory: "blue",
  MissingDatum: "amber",
  MissingLogic: "red",
  MissingToolSupport: "purple",
};

export default function GapBadge({ tier }: { tier: GapTier }) {
  return <Badge color={TIER_COLORS[tier]}>{TIER_LABELS[tier]}</Badge>;
}
