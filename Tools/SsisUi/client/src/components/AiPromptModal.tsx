import { useEffect, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { startRun, type Workspace } from "../api/client";
import CodeBlock from "./CodeBlock";
import LoadingState from "./LoadingState";
import ErrorState from "./ErrorState";

type Assistant = "copilot" | "claude";

/** What a server instruction builder returns. Both Apply Fills and Apply Tests use this shape. */
export type AiInstructions = {
  command: string;
  allPackages: { name: string }[];
  body: string;
};

/**
 * Everything that differs between the Apply Fills and Apply Tests popups. All the
 * wording is built from `noun` and `refreshText`, so both popups read the same.
 */
export type AiPromptConfig = {
  /** Plural noun for what the AI writes: "fills" or "tests". */
  noun: string;
  /** The step this popup runs without AI, e.g. "apply-fills". */
  runStep: "apply-fills" | "apply-tests";
  /** Copilot slash command for this step, when one exists (e.g. "/ssisx-fill"). */
  copilotSlashCommand?: string;
  /** What the page refreshes after Done, e.g. "the Gaps and Fills pages". */
  refreshText: string;
  /** Explains what the footer's "copy existing" action does beyond copying. */
  copyDetail: string;
  /** Label of the Done button. */
  doneLabel: string;
  /** Fetches the server-built instructions for this step, optionally scoped. */
  fetchInstructions: (workspaceId: string, packages?: string[]) => Promise<AiInstructions>;
  /** Runs after Done is pressed. Throwing keeps the popup open with the error shown. */
  onDone: (workspace: Workspace) => Promise<void> | void;
};

/** Only the invocation line differs between the two tabs -- both paste the same
 * server-built body, so both assistants follow the same rules. */
function invocationFor(assistant: Assistant, slash: string | undefined): { heading: string; line: string } {
  if (assistant === "copilot") {
    return {
      heading: "GitHub Copilot",
      line: slash
        ? `In Copilot Chat (Agent mode), type ${slash}, then paste the block below as your message.`
        : "In Copilot Chat (Agent mode), paste the block below as your message.",
    };
  }
  return { heading: "Claude", line: "In Claude Code, paste the block below as your message." };
}

/**
 * A multiselect dropdown: a button summarising the current selection, opening
 * a panel of checkboxes. Closes on an outside click or Escape. The parent owns
 * the selection, so this stays a pure view.
 */
export function PackageMultiSelect({
  names,
  selected,
  onToggle,
  onSelectAll,
  onClear,
}: {
  names: string[];
  selected: string[];
  onToggle: (name: string) => void;
  onSelectAll: () => void;
  onClear: () => void;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    function onDown(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener("mousedown", onDown);
    return () => document.removeEventListener("mousedown", onDown);
  }, [open]);

  const summary =
    selected.length === names.length
      ? `All packages (${names.length})`
      : selected.length === 0
        ? "No packages"
        : `${selected.length} of ${names.length} selected`;

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
        className="flex cursor-pointer items-center gap-2 rounded border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50"
      >
        {summary}
        <span className="text-xs text-gray-400">{open ? "▴" : "▾"}</span>
      </button>
      {open && (
        <div
          role="listbox"
          aria-multiselectable="true"
          className="absolute left-0 top-full z-10 mt-1 w-72 rounded border border-gray-200 bg-white p-2 shadow-lg"
        >
          <div className="mb-1 flex gap-3 border-b border-gray-100 pb-1.5 text-xs">
            <button
              type="button"
              className="cursor-pointer text-blue-700 hover:underline"
              onClick={onSelectAll}
            >
              Select all
            </button>
            <button
              type="button"
              className="cursor-pointer text-blue-700 hover:underline"
              onClick={onClear}
            >
              Clear
            </button>
          </div>
          <div className="max-h-64 overflow-y-auto">
            {names.map((name) => (
              <label
                key={name}
                role="option"
                aria-selected={selected.includes(name)}
                className="flex cursor-pointer items-center gap-2 rounded px-2 py-1 text-sm hover:bg-gray-50"
              >
                <input
                  type="checkbox"
                  checked={selected.includes(name)}
                  onChange={() => onToggle(name)}
                  className="cursor-pointer"
                />
                <span className="truncate font-mono text-xs">{name}</span>
              </label>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

/** Icon-only copy button. `label` becomes its accessible name and tooltip;
 * it swaps to a check mark briefly after a successful copy. */
export function CopyButton({ text, label }: { text: string; label: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      title={copied ? "Copied" : label}
      aria-label={label}
      className="flex h-7 w-7 cursor-pointer items-center justify-center rounded border border-gray-300 bg-white text-gray-600 shadow-sm hover:bg-gray-50 hover:text-gray-900"
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(text);
          setCopied(true);
          setTimeout(() => setCopied(false), 1500);
        } catch {
          // Clipboard unavailable (insecure context, denied permission) --
          // the text is still selectable in the block below.
        }
      }}
    >
      {copied ? (
        <svg viewBox="0 0 20 20" fill="currentColor" className="h-4 w-4 text-green-600" aria-hidden="true">
          <path fillRule="evenodd" d="M16.7 5.3a1 1 0 0 1 0 1.4l-7.5 7.5a1 1 0 0 1-1.4 0L3.3 9.7a1 1 0 1 1 1.4-1.4l4 4 6.8-6.8a1 1 0 0 1 1.4 0z" clipRule="evenodd" />
        </svg>
      ) : (
        <svg viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.5" className="h-4 w-4" aria-hidden="true">
          <rect x="7" y="7" width="9" height="9" rx="1.5" />
          <path d="M4 12.5V4.5A1.5 1.5 0 0 1 5.5 3h8" strokeLinecap="round" />
        </svg>
      )}
    </button>
  );
}


/**
 * The AI-authored popup shared by Apply Fills and Apply Tests. The server builds
 * the instructions; the user pastes them into Copilot or Claude; the footer can
 * still run the step on what is already on disk (no AI).
 */
export default function AiPromptModal({
  workspace,
  onClose,
  config,
}: {
  workspace: Workspace;
  onClose: () => void;
  config: AiPromptConfig;
}) {
  const queryClient = useQueryClient();
  const [assistant, setAssistant] = useState<Assistant>("copilot");
  const [running, setRunning] = useState(false);
  const [runError, setRunError] = useState<string | null>(null);
  // null = every package (the default). Once the user changes the selection,
  // it's an explicit list, and the instruction body is scoped to it.
  const [selected, setSelected] = useState<string[] | null>(null);

  // Fetch the full list first (no scope) to learn every package name, then
  // re-fetch scoped whenever the selection is an explicit subset.
  const allQuery = useQuery({
    queryKey: ["artifacts", workspace.id, config.runStep, "instructions", "all"],
    queryFn: () => config.fetchInstructions(workspace.id),
  });
  const allNames = (allQuery.data?.allPackages ?? []).map((p) => p.name);
  const effective = selected ?? allNames;
  const scopedToSubset = selected !== null && selected.length < allNames.length;
  const scopedQuery = useQuery({
    queryKey: ["artifacts", workspace.id, config.runStep, "instructions", "scoped", effective.join(",")],
    queryFn: () => config.fetchInstructions(workspace.id, effective),
    enabled: scopedToSubset && effective.length > 0,
  });
  const data = scopedToSubset ? scopedQuery.data : allQuery.data;
  const isLoading = allQuery.isLoading || (scopedToSubset && scopedQuery.isLoading);
  const error = allQuery.error ?? (scopedToSubset ? scopedQuery.error : null);

  function togglePackage(name: string) {
    const next = effective.includes(name)
      ? effective.filter((n) => n !== name)
      : [...effective, name];
    // Keep the list in the picker's own order so the query key is stable.
    setSelected(allNames.filter((n) => next.includes(n)));
  }

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") onClose();
    }
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  async function runNow() {
    setRunning(true);
    setRunError(null);
    try {
      await startRun(workspace.id, config.runStep);
      await queryClient.invalidateQueries({ queryKey: ["artifacts", workspace.id] });
    } catch (e) {
      setRunError(e instanceof Error ? e.message : String(e));
    } finally {
      setRunning(false);
    }
  }

  async function done() {
    setRunError(null);
    try {
      await config.onDone(workspace);
    } catch (e) {
      setRunError(e instanceof Error ? e.message : String(e));
      return;
    }
    onClose();
  }

  const invocation = invocationFor(assistant, config.copilotSlashCommand);
  const title = `Apply ${config.noun}`;

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-gray-900/40 p-4"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="flex max-h-[90vh] w-full min-w-0 max-w-3xl flex-col rounded-lg border border-gray-200 bg-white shadow-xl"
      >
        <div className="flex shrink-0 items-center justify-between border-b border-gray-200 px-4 py-3">
          <h2 className="text-base font-semibold text-gray-800">{title}</h2>
          <button
            type="button"
            aria-label="Close"
            className="cursor-pointer rounded px-2 py-1 text-sm text-gray-500 hover:bg-gray-100"
            onClick={onClose}
          >
            ✕
          </button>
        </div>

        {/* min-h keeps room for the package dropdown to open even when the body is
            otherwise short. The header and footer are shrink-0, so the body is the
            only part that scrolls and the buttons stay in view. */}
        <div className="min-h-[14rem] min-w-0 flex-1 overflow-y-auto px-4 py-3 text-sm text-gray-700">
          <p className="mb-3 text-gray-500">
            Writing {config.noun} is done by an AI assistant, not by ssisx. Pick your assistant, paste
            the instructions into it, then come back and press Done so {config.refreshText} refresh.
          </p>

          {allNames.length > 0 && (
            <div className="mb-3 flex flex-wrap items-center gap-3">
              <span className="text-xs font-semibold uppercase tracking-wide text-gray-500">Packages</span>
              <PackageMultiSelect
                names={allNames}
                selected={effective}
                onToggle={togglePackage}
                onSelectAll={() => setSelected(null)}
                onClear={() => setSelected([])}
              />
              {effective.length === 0 && (
                <span className="text-xs text-amber-700">Select at least one package to get instructions.</span>
              )}
            </div>
          )}

          <div role="tablist" className="mb-3 flex gap-1 border-b border-gray-200">
            {(["copilot", "claude"] as Assistant[]).map((a) => (
              <button
                key={a}
                type="button"
                role="tab"
                aria-selected={assistant === a}
                className={`cursor-pointer -mb-px border-b-2 px-3 py-1.5 text-sm ${
                  assistant === a
                    ? "border-blue-600 font-semibold text-blue-700"
                    : "border-transparent text-gray-500 hover:text-gray-700"
                }`}
                onClick={() => setAssistant(a)}
              >
                {invocationFor(a, config.copilotSlashCommand).heading}
              </button>
            ))}
          </div>

          {isLoading && <LoadingState />}
          {error && <ErrorState error={error} />}
          {data && (
            <>
              <p className="mb-2">{invocation.line}</p>
              <div className="relative">
                <div className="absolute right-2 top-4 z-10">
                  <CopyButton text={data.body} label="Copy instructions" />
                </div>
                <CodeBlock language="markdown">{data.body}</CodeBlock>
              </div>
              <details className="mt-2 text-xs text-gray-500">
                <summary className="cursor-pointer select-none">Run it by hand instead</summary>
                <p className="mt-2 mb-1">
                  Runs {config.runStep} on whatever {config.noun} are already on disk. It copies every
                  package's {config.noun}, not just the ones selected above ({config.runStep} has no
                  per-package option).
                </p>
                <div className="mb-1 flex items-start justify-between gap-2">
                  <code className="break-all rounded bg-gray-100 px-2 py-1 font-mono text-[11px] text-gray-700">
                    {data.command}
                  </code>
                  <CopyButton text={data.command} label="Copy command" />
                </div>
              </details>
            </>
          )}
        </div>

        <div className="flex shrink-0 flex-col gap-2 border-t border-gray-200 px-4 py-3">
          {runError && <ErrorState error={runError} />}
          {/* Full-width line above the buttons, so it wraps inside the footer
              instead of running past the modal's right edge. */}
          <p className="text-xs leading-snug text-gray-500">
            <span className="font-medium text-gray-600">Copy existing {config.noun}:</span>{" "}
            copies {config.noun} already on disk into the generated project, for every package.{" "}
            {config.copyDetail} It ignores the package selection.
          </p>
          <div className="flex items-center justify-end gap-2">
            <button
              type="button"
              className="cursor-pointer rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
              onClick={runNow}
              disabled={running}
            >
              {running ? `Copying existing ${config.noun}...` : `Copy existing ${config.noun} (no AI)`}
            </button>
            <button
              type="button"
              className="cursor-pointer rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
              onClick={done}
            >
              {config.doneLabel}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
