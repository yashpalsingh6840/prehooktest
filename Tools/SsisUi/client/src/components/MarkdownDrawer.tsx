import { useEffect } from "react";
import MarkdownView from "./MarkdownView";

/** Right-hand drawer that shows one markdown document in full. Closes on Escape,
 * on the backdrop, or on the close button. Reusable for any generated .md report. */
export default function MarkdownDrawer({
  title,
  label,
  markdown,
  onClose,
}: {
  title: string;
  label: string;
  markdown: string;
  onClose: () => void;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div className="fixed inset-0 z-50 flex justify-end bg-gray-900/40" onMouseDown={onClose}>
      <aside
        role="dialog"
        aria-modal="true"
        aria-label={title}
        onMouseDown={(e) => e.stopPropagation()}
        className="flex h-full w-full max-w-3xl flex-col bg-white shadow-xl"
      >
        <header className="flex items-center justify-between gap-3 border-b border-gray-200 px-5 py-3">
          <div className="min-w-0">
            <p className="text-[11px] font-semibold uppercase tracking-wide text-gray-500">{label}</p>
            <h3 className="truncate text-sm font-semibold text-gray-900">{title}</h3>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="cursor-pointer rounded px-2 py-0.5 text-lg leading-none text-gray-500 hover:bg-gray-100 hover:text-gray-800"
          >
            ×
          </button>
        </header>
        <div className="flex-1 overflow-y-auto px-5 py-4">
          <div className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
            <MarkdownView markdown={markdown} />
          </div>
        </div>
      </aside>
    </div>
  );
}
