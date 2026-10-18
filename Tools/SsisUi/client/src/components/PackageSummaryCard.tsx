import { useEffect, useMemo, useState, type ReactNode } from "react";
import { summarizePackage, type DetailKey, type DetailRow, type PackageSummary } from "../utils/packageSummary";

type Tone = "slate" | "emerald" | "violet" | "sky" | "amber" | "orange" | "rose";

const TONE: Record<Tone, { bar: string; bg: string; value: string }> = {
  orange: { bar: "bg-orange-500", bg: "bg-orange-100", value: "text-orange-900" },
  slate: { bar: "bg-slate-400", bg: "bg-slate-50", value: "text-slate-800" },
  emerald: { bar: "bg-emerald-500", bg: "bg-emerald-50", value: "text-emerald-800" },
  violet: { bar: "bg-violet-500", bg: "bg-violet-50", value: "text-violet-800" },
  sky: { bar: "bg-sky-500", bg: "bg-sky-50", value: "text-sky-800" },
  amber: { bar: "bg-amber-500", bg: "bg-amber-50", value: "text-amber-800" },
  rose: { bar: "bg-rose-500", bg: "bg-rose-50", value: "text-rose-800" },
};

const TITLE: Record<DetailKey, string> = {
  executables: "Executables",
  dataFlows: "Data flows",
  components: "Components",
  sqlTasks: "SQL tasks",
  coverage: "Coverage",
  connections: "Connections",
  variables: "Variables",
  precedence: "Precedence links",
  parallel: "Parallel waves",
  disabled: "Disabled",
  scripts: "Script tasks & components",
};

const EMPTY_TEXT: Record<DetailKey, string> = {
  executables: "No executables.",
  dataFlows: "No data flows.",
  components: "No pipeline components.",
  sqlTasks: "No Execute SQL tasks.",
  coverage: "Nothing is unmapped -- every element was read into the spec.",
  connections: "No connection managers.",
  variables: "No variables declared.",
  precedence: "No precedence constraints.",
  parallel: "No tasks run in parallel.",
  disabled: "Nothing is disabled.",
  scripts: "No Script Tasks or Script Components.",
};

/** What the modal is currently showing. `groupBy` names the breakdown shown in
 * the header and how to read it from each row's note. */
interface ModalState {
  title: string;
  rows: DetailRow[];
  emptyText: string;
  groupBy?: { label: string; pick: (r: DetailRow) => string | undefined };
  /** Explanation shown above everything else (used by Coverage). */
  intro?: ReactNode;
}

/** Counts rows by the picked key, biggest first, for the modal's header. */
function groupRows(rows: DetailRow[], pick: (r: DetailRow) => string | undefined): [string, number][] {
  const counts = new Map<string, number>();
  for (const r of rows) {
    const k = pick(r);
    if (k) counts.set(k, (counts.get(k) ?? 0) + 1);
  }
  return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
}

/** The note's first "·"-separated part, e.g. "Microsoft.Pipeline · 3 components" -> "Microsoft.Pipeline". */
const firstPart = (r: DetailRow) => r.secondary?.split(" · ")[0];
/** The note's second part, e.g. "FlatFileSource · DFT_Load" -> "DFT_Load". */
const secondPart = (r: DetailRow) => r.secondary?.split(" · ")[1];

/** Compact tile: label on the left, number right-aligned on the same line. */
function Tile({
  label,
  value,
  tone,
  suffix,
  onOpen,
}: {
  label: string;
  value: number;
  tone: Tone;
  suffix?: string;
  onOpen: () => void;
}) {
  const t = TONE[tone];
  return (
    <button
      type="button"
      onClick={onOpen}
      aria-haspopup="dialog"
      title={`Show ${label.toLowerCase()}`}
      className={`relative flex cursor-pointer items-center justify-between gap-2 overflow-hidden rounded-md border border-gray-200 ${t.bg} py-1.5 pl-3 pr-2.5 text-left shadow-sm transition hover:border-gray-300 hover:shadow focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500`}
    >
      <span className={`absolute inset-y-0 left-0 w-1 ${t.bar}`} aria-hidden="true" />
      <span className="truncate text-[11px] font-semibold uppercase tracking-wide text-gray-500">{label}</span>
      <span className={`shrink-0 text-lg font-semibold leading-none tabular-nums ${t.value}`}>
        {value}
        {suffix && <span className="ml-0.5 text-sm font-medium">{suffix}</span>}
      </span>
    </button>
  );
}

/** Modal listing rows behind a stat tile or a breakdown item. Closes on Escape,
 * on a click outside the panel, or on the close button. */
function DetailModal({ state, onClose }: { state: ModalState; onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-gray-900/40 p-4" onMouseDown={onClose}>
      <div
        role="dialog"
        aria-modal="true"
        aria-label={state.title}
        onMouseDown={(e) => e.stopPropagation()}
        className="flex max-h-[80vh] w-full max-w-lg flex-col overflow-hidden rounded-lg bg-white shadow-xl"
      >
        <div className="flex items-center justify-between border-b border-gray-200 px-4 py-3">
          <h3 className="text-sm font-semibold text-gray-900">
            {state.title} <span className="font-normal text-gray-500">({state.rows.length})</span>
          </h3>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="cursor-pointer rounded px-2 py-0.5 text-lg leading-none text-gray-500 hover:bg-gray-100 hover:text-gray-800"
          >
            ×
          </button>
        </div>
        {state.intro && <div className="border-b border-gray-100 px-4 py-3 text-sm text-gray-700">{state.intro}</div>}
        {state.groupBy && state.rows.length > 0 && (
          <div className="border-b border-gray-100 bg-gray-50 px-4 py-2.5">
            <p className="mb-1.5 text-[11px] font-semibold uppercase tracking-wide text-gray-500">
              {state.groupBy.label}
            </p>
            <ul className="flex flex-wrap gap-1.5">
              {groupRows(state.rows, state.groupBy.pick).map(([name, count]) => (
                <li
                  key={name}
                  className="rounded-full border border-gray-200 bg-white px-2.5 py-0.5 text-xs text-gray-700"
                >
                  <span className="font-mono">{name}</span>{" "}
                  <span className="font-semibold text-gray-900">{count}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
        <div className="overflow-y-auto px-4 py-2">
          {state.rows.length === 0 ? (
            <p className="py-6 text-center text-sm text-gray-500">{state.emptyText}</p>
          ) : (
            <ul className="divide-y divide-gray-100">
              {state.rows.map((r, i) => (
                <li key={`${r.primary}-${i}`} className="py-2">
                  <p className="break-all font-mono text-xs text-gray-800">{r.primary}</p>
                  {r.secondary && <p className="mt-0.5 break-all text-xs text-gray-500">{r.secondary}</p>}
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </div>
  );
}

/** Explains the Coverage percentage and the element counts behind it. */
function CoverageIntro({ s }: { s: PackageSummary }) {
  const total = s.coverageTotal ?? 0;
  const excluded = s.coverageExcluded ?? 0;
  const unmapped = s.coverageUnmapped ?? 0;
  const counted = total - excluded;
  const read = counted - unmapped;
  return (
    <div className="space-y-2">
      <p>
        Coverage is the share of this package's XML elements that the extractor understood and wrote into
        the spec. <strong>{s.coveragePercent}%</strong> means {read} of {counted} counted elements were read.
      </p>
      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
        <dt className="text-gray-500">Total elements in the XML</dt>
        <dd className="font-semibold tabular-nums">{total}</dd>
        <dt className="text-gray-500">Excluded (design-time only, never affects the score)</dt>
        <dd className="font-semibold tabular-nums">{excluded}</dd>
        <dt className="text-gray-500">Counted for the score</dt>
        <dd className="font-semibold tabular-nums">{counted}</dd>
        <dt className="text-gray-500">Unmapped (the only ones that lower the score)</dt>
        <dd className="font-semibold tabular-nums">{unmapped}</dd>
      </dl>
      <p className="text-xs text-gray-500">
        Unmapped elements are listed below. None listed means nothing was lost.
      </p>
    </div>
  );
}

/** Colour by what a component does, so a glance tells sources from sinks. */
function componentTone(name: string): Tone {
  if (/Source/i.test(name)) return "emerald";
  if (/Destination/i.test(name)) return "sky";
  if (/Script|Command/i.test(name)) return "rose";
  return "violet";
}

/** A breakdown row (component or connection type). Clicking it opens the modal
 * filtered to just that type. */
function BarList({
  title,
  rows,
  tone,
  onSelect,
}: {
  title: string;
  rows: [string, number][];
  tone: (name: string) => Tone;
  onSelect: (name: string) => void;
}) {
  if (rows.length === 0) return null;
  const max = Math.max(...rows.map(([, n]) => n));
  return (
    <div className="min-w-0 rounded-lg border border-gray-200 bg-white p-3">
      <h4 className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-gray-500">{title}</h4>
      <ul className="space-y-1">
        {rows.map(([name, count]) => {
          const t = TONE[tone(name)];
          return (
            <li key={name}>
              <button
                type="button"
                onClick={() => onSelect(name)}
                aria-haspopup="dialog"
                title={`Show ${name}`}
                className="grid w-full cursor-pointer grid-cols-[minmax(0,10rem)_1fr_2rem] items-center gap-2 rounded px-1.5 py-1 text-left text-xs hover:bg-gray-50 focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              >
                <span className="truncate font-mono text-gray-700" title={name}>{name}</span>
                <span className="h-2 overflow-hidden rounded-full bg-gray-100">
                  <span className={`block h-full rounded-full ${t.bar}`} style={{ width: `${Math.max(6, (count / max) * 100)}%` }} />
                </span>
                <span className="text-right font-semibold tabular-nums text-gray-900">{count}</span>
              </button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

/** Headline counts for one extracted package, shown above its raw JSON tree.
 * Everything here is read straight from the spec.json already on screen. */
export default function PackageSummaryCard({ json }: { json: unknown }) {
  const s = useMemo(() => summarizePackage(json), [json]);
  const [modal, setModal] = useState<ModalState | null>(null);
  const coverageTone: Tone =
    s.coveragePercent == null ? "slate" : s.coveragePercent >= 100 ? "emerald" : "amber";

  // Each category's header breaks its rows down by the most useful field in the note.
  const GROUP: Partial<Record<DetailKey, ModalState["groupBy"]>> = {
    executables: { label: "By type", pick: firstPart },
    components: { label: "By data flow", pick: secondPart },
    connections: { label: "By scope", pick: secondPart },
    variables: { label: "By data type", pick: (r) => r.secondary },
    disabled: { label: "By container", pick: (r) => r.secondary?.replace(/^in /, "") },
    scripts: { label: "By kind", pick: firstPart },
  };

  const openKey = (k: DetailKey) =>
    setModal({
      title: TITLE[k],
      rows: s.details[k],
      emptyText: EMPTY_TEXT[k],
      groupBy: GROUP[k],
      intro: k === "coverage" ? <CoverageIntro s={s} /> : undefined,
    });

  // Component rows carry "<Type> · <data flow>" as their note.
  const openComponentType = (type: string) =>
    setModal({
      title: `Components: ${type}`,
      rows: s.details.components.filter((r) => r.secondary?.startsWith(`${type} · `)),
      emptyText: `No ${type} components.`,
      groupBy: GROUP.components,
    });

  // Connection rows carry "<type>[ · <scope>]" as their note.
  const openConnectionType = (type: string) =>
    setModal({
      title: `Connections: ${type}`,
      rows: s.details.connections.filter((r) => r.secondary === type || r.secondary?.startsWith(`${type} · `)),
      emptyText: `No ${type} connections.`,
      groupBy: GROUP.connections,
    });

  return (
    <section className="mb-4 space-y-3" aria-label="Package summary">
      <div>
        <h3 className="text-base font-semibold text-gray-900">{s.name ?? "Package"}</h3>
        {s.description && <p className="mt-0.5 text-sm text-gray-600">{s.description}</p>}
        <p className="mt-1 text-xs text-gray-500">
          {s.productVersion && <>Last saved by SSIS {s.productVersion}</>}
          {s.formatVersion && <> · package format v{s.formatVersion}</>}
        </p>
      </div>

      <div className="grid grid-cols-2 gap-2 md:grid-cols-5">
        <Tile label="Executables" value={s.executables} tone="slate" onOpen={() => openKey("executables")} />
        <Tile label="Data flows" value={s.dataFlows} tone="emerald" onOpen={() => openKey("dataFlows")} />
        <Tile label="Components" value={s.components} tone="emerald" onOpen={() => openKey("components")} />
        <Tile label="SQL tasks" value={s.sqlTasks} tone="violet" onOpen={() => openKey("sqlTasks")} />
        <Tile
          label="Scripts"
          value={s.scripts}
          tone={s.scripts > 0 ? "orange" : "slate"}
          onOpen={() => openKey("scripts")}
        />
        {s.coveragePercent != null && (
          <Tile label="Coverage" value={s.coveragePercent} suffix="%" tone={coverageTone} onOpen={() => openKey("coverage")} />
        )}
        <Tile label="Connections" value={s.connections} tone="sky" onOpen={() => openKey("connections")} />
        <Tile label="Variables" value={s.variables} tone="slate" onOpen={() => openKey("variables")} />
        <Tile label="Precedence links" value={s.precedenceConstraints} tone="violet" onOpen={() => openKey("precedence")} />
        <Tile label="Parallel waves" value={s.parallelWaves} tone="violet" onOpen={() => openKey("parallel")} />
        <Tile label="Disabled" value={s.disabled} tone={s.disabled > 0 ? "amber" : "slate"} onOpen={() => openKey("disabled")} />
      </div>

      <div className="grid gap-3 md:grid-cols-2">
        <BarList title="Component types" rows={s.componentTypes} tone={componentTone} onSelect={openComponentType} />
        <BarList title="Connection types" rows={s.connectionTypes} tone={() => "sky"} onSelect={openConnectionType} />
      </div>

      {modal && <DetailModal state={modal} onClose={() => setModal(null)} />}
    </section>
  );
}
