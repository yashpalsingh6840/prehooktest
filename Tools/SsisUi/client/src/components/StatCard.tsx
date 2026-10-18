import { useEffect, useRef, useState } from "react";

/**
 * A compact stat tile with an optional info icon (top-right corner) that
 * reveals a short explanation in a popover on click -- used anywhere a
 * screen shows a row of "Packages / Files / Gaps"-style numbers with no
 * other place to put an explanation of what each one actually counts.
 * Shared by GenerateReportView.tsx and PortfolioCharts.tsx rather than
 * each keeping its own near-identical copy.
 */
export default function StatCard({
  label,
  value,
  hint,
  className = "",
}: {
  label: string;
  value: number;
  hint?: string;
  /** Extra classes merged onto the card's own root -- e.g. "flex-1" so a row
   * of cards stretches to fill its container width instead of staying
   * content-sized. */
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    function onDocClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    document.addEventListener("mousedown", onDocClick);
    return () => document.removeEventListener("mousedown", onDocClick);
  }, [open]);

  return (
    <div
      ref={ref}
      className={`relative flex min-w-[96px] flex-col gap-0.5 rounded-lg border border-gray-200 bg-white px-2.5 py-1.5 shadow-sm ${className}`}
    >
      {hint && (
        <button
          type="button"
          aria-label={`About ${label}`}
          onClick={() => setOpen((o) => !o)}
          className={`absolute right-1 top-1 flex h-3.5 w-3.5 cursor-pointer items-center justify-center rounded-full border text-[9px] font-semibold leading-none transition-colors ${
            open
              ? "border-blue-300 bg-blue-50 text-blue-700"
              : "border-gray-300 text-gray-400 hover:bg-gray-100 hover:text-gray-600"
          }`}
        >
          i
        </button>
      )}
      <span className="pr-4 text-[10px] font-medium uppercase leading-tight tracking-wide text-gray-500">
        {label}
      </span>
      <span className="text-lg font-semibold leading-tight text-gray-900">{value}</span>

      {open && hint && (
        <div className="absolute right-0 top-full z-20 mt-1 w-56 rounded border border-gray-200 bg-white p-2 text-xs font-normal normal-case leading-snug text-gray-700 shadow-lg">
          {hint}
        </div>
      )}
    </div>
  );
}
