import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { getOutputFile, revealOutputFile, type OutputFileName, type Workspace } from "../api/client";
import JsonTree from "./JsonTree";
import LogViewer from "./LogViewer";
import MarkdownView from "./MarkdownView";
import EmptyState from "./EmptyState";
import LoadingState from "./LoadingState";
import ErrorState from "./ErrorState";
import { FillsTable, GapsTable, type FillRecord, type GapRecord } from "./OutputJsonTables";

type TableKind = "gaps" | "fills";
type ViewMode = "table" | "raw";

// generate-report.md is deliberately absent: it already has its own page at
// /reports/generate, so repeating it here would just duplicate that view.
const FILES: { name: OutputFileName; label: string; hint: string; table?: TableKind }[] = [
  {
    name: "gaps",
    label: "Gaps (gaps.json)",
    hint: "Every gap, its tier, and the work packet it points at.",
    table: "gaps",
  },
  {
    name: "fills-applied",
    label: "Fills applied",
    hint: "Each fill apply-fills found and whether it was applied, stale, or orphaned.",
    table: "fills",
  },
  {
    name: "apply-fills-log",
    label: "Apply-fills log",
    hint: "The step-by-step trace of the last apply-fills run.",
  },
];

function RawText({ text }: { text: string }) {
  return (
    <pre className="max-h-[28rem] overflow-auto rounded-lg border border-gray-200 bg-gray-50 p-3 font-mono text-xs leading-relaxed text-gray-800 whitespace-pre">
      {text}
    </pre>
  );
}

/** JSON with no table view configured: a collapsible tree. */
function JsonBody({ text }: { text: string }) {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    // Not valid JSON (e.g. a half-written file) -- show it raw rather than
    // hiding it.
    return <RawText text={text} />;
  }
  return (
    <div className="max-h-[28rem] overflow-auto rounded-lg border border-gray-200 bg-white p-3">
      <JsonTree data={parsed} />
    </div>
  );
}

function TableBody({ table, text }: { table: TableKind; text: string }) {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    parsed = undefined;
  }
  if (!Array.isArray(parsed)) {
    return (
      <>
        <p className="mb-2 text-xs text-amber-700">
          Couldn't read this file as a list of records (it may be half-written), so the raw text is
          shown instead.
        </p>
        <RawText text={text} />
      </>
    );
  }
  return table === "gaps" ? (
    <GapsTable gaps={parsed as GapRecord[]} />
  ) : (
    <FillsTable fills={parsed as FillRecord[]} />
  );
}

/** Renders one output file in the form that suits it: markdown as prose,
 * JSON as a table (or the raw text), anything else as monospace text. */
function FileBody({
  workspaceId,
  name,
  table,
  view,
}: {
  workspaceId: string;
  name: OutputFileName;
  table?: TableKind;
  view: ViewMode;
}) {
  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspaceId, "output-file", name],
    queryFn: () => getOutputFile(workspaceId, name),
  });

  if (isLoading) return <LoadingState />;
  if (error) return <ErrorState error={error} />;
  if (!data || !data.available) {
    return <EmptyState message="Not produced yet -- run the step that writes this file." />;
  }

  return (
    <div>
      <div className="mb-2 flex items-center gap-2">
        <p className="min-w-0 flex-1 break-all font-mono text-xs text-gray-500" title={data.path}>
          {data.path}
        </p>
        <OpenLocationButton workspaceId={workspaceId} name={name} />
      </div>
      {data.kind === "markdown" && <MarkdownView markdown={data.markdown} />}
      {data.kind === "json" && !table && <JsonBody text={data.markdown} />}
      {data.kind === "json" && table && view === "table" && (
        <TableBody table={table} text={data.markdown} />
      )}
      {data.kind === "json" && table && view === "raw" && <RawText text={data.markdown} />}
      {data.kind === "text" && <LogViewer text={data.markdown} />}
    </div>
  );
}

/** Opens Windows Explorer with the file highlighted. Shows a brief error
 * (rather than failing silently) if the file is missing. */
function OpenLocationButton({ workspaceId, name }: { workspaceId: string; name: OutputFileName }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function open() {
    setBusy(true);
    setError(null);
    try {
      await revealOutputFile(workspaceId, name);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <span className="flex shrink-0 items-center gap-2">
      <button
        type="button"
        onClick={open}
        disabled={busy}
        title="Open file location"
        aria-label="Open file location"
        className="flex h-7 w-7 cursor-pointer items-center justify-center rounded border border-gray-300 bg-white text-gray-600 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
      >
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" className="h-4 w-4" aria-hidden="true">
          <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
          <path d="M12 11v5M9.5 13.5 12 11l2.5 2.5" />
        </svg>
      </button>
      {error && <span className="text-xs text-red-700">{error}</span>}
    </span>
  );
}

/** The four generate/apply-fills output files, each in a tab, so a user can
 * see what the last run actually wrote without opening the folder. */
export default function OutputFilesPanel({ workspace }: { workspace: Workspace }) {
  const [active, setActive] = useState<OutputFileName>("gaps");
  const [view, setView] = useState<ViewMode>("table");
  const current = FILES.find((f) => f.name === active)!;

  return (
    <section className="mt-6">
      <h2 className="mb-2 text-sm font-semibold uppercase tracking-wide text-gray-500">
        Output files
      </h2>
      <div role="tablist" className="mb-3 flex flex-wrap gap-1 border-b border-gray-200">
        {FILES.map((f) => (
          <button
            key={f.name}
            type="button"
            role="tab"
            aria-selected={active === f.name}
            className={`cursor-pointer -mb-px border-b-2 px-3 py-1.5 text-sm ${
              active === f.name
                ? "border-blue-600 font-semibold text-blue-700"
                : "border-transparent text-gray-500 hover:text-gray-700"
            }`}
            onClick={() => setActive(f.name)}
          >
            {f.label}
          </button>
        ))}
      </div>
      <p className="mb-3 text-xs text-gray-500">{current.hint}</p>
      {current.table && (
        <div
          role="group"
          aria-label="View format"
          className="mb-3 inline-flex overflow-hidden rounded border border-gray-300 text-xs"
        >
          {(["table", "raw"] as const).map((mode) => (
            <button
              key={mode}
              type="button"
              aria-pressed={view === mode}
              onClick={() => setView(mode)}
              className={`cursor-pointer px-3 py-1 ${
                view === mode ? "bg-blue-600 text-white" : "bg-white text-gray-600 hover:bg-gray-50"
              }`}
            >
              {mode === "table" ? "Table" : "Raw JSON"}
            </button>
          ))}
        </div>
      )}
      <FileBody
        workspaceId={workspace.id}
        name={active}
        table={current.table}
        view={view}
      />
    </section>
  );
}
