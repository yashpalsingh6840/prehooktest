import Badge, { type BadgeColor } from "./Badge";

/** A count shown as a coloured pill ("Gaps: 13"), the style used for summary rows across
 * the app. The explanation goes in a tooltip, so the pill stays compact. */
export default function CountChip({
  label,
  value,
  color,
  hint,
}: {
  label: string;
  value: number;
  color: BadgeColor;
  hint?: string;
}) {
  return (
    <span title={hint}>
      <Badge color={color}>
        {label}: {value}
      </Badge>
    </span>
  );
}
