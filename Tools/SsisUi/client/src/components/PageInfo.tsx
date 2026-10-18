import { useState, type ReactNode } from "react";

/**
 * A page-level "what is this and where did it come from" block -- a short
 * summary always visible, plus an info-icon toggle (same affordance as
 * `StatCard`'s own "i" button) revealing a longer explanation BELOW it,
 * inline and full-width. Used so every artifact-viewer page is
 * self-explanatory without needing CLAUDE.md or the underlying CLI's own
 * --help text open beside it.
 *
 * Deliberately NOT `InfoPopover.tsx` (a floating, fixed-width panel pinned
 * to one icon) -- that's the right shape for a compact, corner-pinned note
 * (e.g. one Pipeline step card's own explanation), but a page-level
 * description with several paragraphs/a bullet list reads better inline,
 * spanning the page's own full width, than clipped inside a narrow overlay.
 */
export default function PageInfo({
  summary,
  details,
}: {
  summary: string;
  details: ReactNode;
}) {
  const [open, setOpen] = useState(false);

  return (
    <div className="mb-3">
      <div className="flex items-start justify-between gap-2">
        <p className="flex-1 text-sm text-gray-500">{summary}</p>
        <button
          type="button"
          onClick={() => setOpen((o) => !o)}
          aria-expanded={open}
          aria-label={open ? "Hide details" : "More details"}
          className={`flex h-5 w-5 shrink-0 cursor-pointer items-center justify-center rounded-full border text-xs font-semibold leading-none transition-colors ${
            open
              ? "border-blue-300 bg-blue-50 text-blue-700"
              : "border-gray-300 text-gray-400 hover:bg-gray-100 hover:text-gray-600"
          }`}
        >
          i
        </button>
      </div>
      {open && (
        <div className="mt-2 space-y-2 rounded-lg border border-gray-200 bg-gray-50 p-3 text-xs leading-relaxed text-gray-600">
          {details}
        </div>
      )}
    </div>
  );
}
