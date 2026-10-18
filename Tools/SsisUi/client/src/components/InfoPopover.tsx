import { useEffect, useRef, useState, type ReactNode } from "react";

/**
 * A small "i" icon that reveals a richer explanation in a floating panel on
 * click -- closes on an outside click. Deliberately separate from
 * StatCard.tsx's own inline icon+popover (which is tuned for a short,
 * one-line hint baked into a compact numeric tile); this one is for a
 * standalone icon positioned anywhere (e.g. a card's own corner) with
 * multi-paragraph content.
 */
export default function InfoPopover({
  label,
  // Callers that want this pinned somewhere specific (e.g. a card's own
  // corner) pass their own full positioning class list here, e.g.
  // "absolute right-2 top-2" -- NEVER append to a hardcoded "relative" base,
  // since Tailwind's own generated stylesheet order makes "relative" beat a
  // same-specificity "absolute" on one element regardless of which comes
  // first in the class string, silently breaking the intended pinning.
  className = "relative inline-block",
  panelClassName = "w-72",
  children,
}: {
  label: string;
  className?: string;
  panelClassName?: string;
  children: ReactNode;
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
    <div ref={ref} className={className}>
      <button
        type="button"
        aria-label={label}
        onClick={(e) => {
          e.stopPropagation();
          setOpen((o) => !o);
        }}
        className={`flex h-4 w-4 cursor-pointer items-center justify-center rounded-full border text-[10px] font-semibold leading-none transition-colors ${
          open
            ? "border-blue-300 bg-blue-50 text-blue-700"
            : "border-gray-300 bg-white text-gray-400 hover:bg-gray-100 hover:text-gray-600"
        }`}
      >
        i
      </button>
      {open && (
        <div
          className={`absolute right-0 top-full z-30 mt-1 rounded border border-gray-200 bg-white p-3 text-xs leading-snug text-gray-700 shadow-xl ${panelClassName}`}
          onClick={(e) => e.stopPropagation()}
        >
          {children}
        </div>
      )}
    </div>
  );
}
