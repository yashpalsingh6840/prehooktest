import type { ReactNode } from "react";

export type BadgeColor = "green" | "amber" | "red" | "gray" | "blue" | "purple";

// The one status-color vocabulary used everywhere a status appears in this
// app: run status, gap tier, fill status, claim status. Keep this mapping as
// the single source of truth rather than re-deriving colors per screen.
const COLOR_CLASSES: Record<BadgeColor, string> = {
  green: "bg-green-100 text-green-800",
  amber: "bg-amber-100 text-amber-800",
  red: "bg-red-100 text-red-800",
  gray: "bg-gray-200 text-gray-700",
  blue: "bg-blue-100 text-blue-800",
  purple: "bg-purple-100 text-purple-800",
};

export default function Badge({
  color,
  children,
  className = "",
}: {
  color: BadgeColor;
  children: ReactNode;
  className?: string;
}) {
  return (
    <span
      className={`inline-flex items-center rounded px-2 py-0.5 text-xs font-semibold whitespace-nowrap ${COLOR_CLASSES[color]} ${className}`}
    >
      {children}
    </span>
  );
}
