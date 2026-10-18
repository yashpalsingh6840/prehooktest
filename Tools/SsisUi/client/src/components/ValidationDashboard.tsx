import { useMemo, useState, type ReactNode } from "react";
import { Link } from "react-router-dom";
import type { ValidationOverview } from "../api/client";
import { countProblems, isClean, PROBLEM_VERDICTS } from "../utils/validationStatus";
import CountChip from "./CountChip";
import StackedBar, { type BarSegment } from "./StackedBar";
import ValidationStatusBadge from "./ValidationStatusBadge";

const GATES = ["1", "2", "3"] as const;
type Gate = (typeof GATES)[number];
const GATE_NAME: Record<Gate, string> = { "1": "Gate 1 (SQL)", "2": "Gate 2 (graph)", "3": "Gate 3 (schema)" };
const MATCH_VERDICTS = ["Match", "NormalizedMatch"];

const BUCKET_SEGMENTS = (o: ValidationOverview["bucketCounts"]): BarSegment[] => [
  { label: "Tool-Verified", value: o.toolVerified, className: "bg-green-500" },
  { label: "Exception", value: o.exception, className: "bg-gray-400" },
  { label: "AI-Verified", value: o.aiVerified, className: "bg-teal-500" },
  { label: "Needs review", value: o.humanNeedsReview, className: "bg-amber-500" },
];

function problemsIn(counts: Record<string, number> | null): number {
  if (!counts) return 0;
  return PROBLEM_VERDICTS.reduce((s, v) => s + (counts[v] ?? 0), 0);
}

/** Worst first: most human work, then most real problems. */
function attentionScore(o: ValidationOverview): number {
  return o.bucketCounts.humanNeedsReview + countProblems(GATES.map((g) => o.gateCounts[g]));
}

function gateChipClass(counts: Record<string, number> | null): string {
  if (!counts) return "border border-dashed border-gray-300 text-gray-400";
  if (problemsIn(counts) > 0) return "bg-red-100 text-red-800";
  const matched = MATCH_VERDICTS.reduce((s, v) => s + (counts[v] ?? 0), 0);
  return matched > 0 ? "bg-green-100 text-green-800" : "bg-gray-100 text-gray-600";
}

function gateChipTitle(counts: Record<string, number> | null): string {
  if (!counts) return "Not run";
  const parts = Object.entries(counts)
    .filter(([, n]) => n > 0)
    .map(([k, n]) => `${k} ${n}`);
  return parts.length > 0 ? parts.join(" · ") : "No findings";
}

const STAGE_LABELS: { key: keyof ValidationOverview["stages"]; label: string }[] = [
  { key: "validated", label: "Gates" },
  { key: "packet", label: "Evidence packet" },
  { key: "review", label: "AI review" },
  { key: "finalSummary", label: "Final summary" },
];

type Filter = "all" | "attention" | "clean";
type View = "cards" | "table";

export default function ValidationDashboard({
  rows,
  base,
  table,
}: {
  rows: ValidationOverview[];
  base: string;
  /** The existing per-package table, shown when the Table view is picked. */
  table: ReactNode;
}) {
  const [filter, setFilter] = useState<Filter>("all");
  const [search, setSearch] = useState("");
  const [view, setView] = useState<View>("cards");

  const sorted = useMemo(
    () => [...rows].sort((a, b) => attentionScore(b) - attentionScore(a)),
    [rows],
  );
  const total = rows.length;
  const cleanCount = rows.filter(isClean).length;
  const bucketSum = rows.reduce(
    (acc, o) => ({
      toolVerified: acc.toolVerified + o.bucketCounts.toolVerified,
      exception: acc.exception + o.bucketCounts.exception,
      aiVerified: acc.aiVerified + o.bucketCounts.aiVerified,
      humanNeedsReview: acc.humanNeedsReview + o.bucketCounts.humanNeedsReview,
    }),
    { toolVerified: 0, exception: 0, aiVerified: 0, humanNeedsReview: 0 },
  );
  const realProblems = rows.reduce((s, o) => s + countProblems(GATES.map((g) => o.gateCounts[g])), 0);

  const shown = sorted
    .filter((o) =>
      filter === "all" ? true : filter === "attention" ? !isClean(o) : isClean(o),
    )
    .filter((o) => o.package.toLowerCase().includes(search.trim().toLowerCase()));

  if (total === 0) return null;

  return (
    <div className="space-y-5">
      {/* Headline */}
      <div className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
        <div className="mb-2 flex flex-wrap items-baseline justify-between gap-2">
          <p className="text-lg font-semibold text-gray-900">
            {cleanCount} of {total} packages clean
          </p>
          <span
            className={`rounded px-2 py-0.5 text-xs font-medium ${
              cleanCount === total ? "bg-green-100 text-green-800" : "bg-amber-100 text-amber-800"
            }`}
          >
            {cleanCount === total ? "All clear" : `${total - cleanCount} need review`}
          </span>
        </div>
        <StackedBar height="h-3" segments={BUCKET_SEGMENTS(bucketSum)} />
        <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-600">
          {BUCKET_SEGMENTS(bucketSum).map((s) => (
            <span key={s.label} className="flex items-center gap-1">
              <span className={`inline-block h-2 w-2 rounded-sm ${s.className}`} />
              {s.label} <strong className="text-gray-800">{s.value}</strong>
            </span>
          ))}
        </div>
      </div>

      {/* KPI tiles */}
      <div className="flex flex-wrap items-center gap-2">
        <CountChip label="Clean packages" value={cleanCount} color="green" hint="Packages with no real problems and nothing waiting for a person." />
        <CountChip label="Need review" value={total - cleanCount} color="amber" hint="Packages with at least one real problem or a Human-Needs-Review item." />
        <CountChip label="Real problems" value={realProblems} color="red" hint="Mismatch, LeftOnly, RightOnly or Unrecognized findings across all three gates." />
        <CountChip label="Human-Needs-Review" value={bucketSum.humanNeedsReview} color="amber" hint="The human work list across every package." />
        <CountChip label="AI-Verified" value={bucketSum.aiVerified} color="purple" hint="Checks the validator could not settle alone that an AI review then confirmed." />
      </div>

      {/* Pipeline progress */}
      <section className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
        <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-500">Pipeline progress</h3>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
          {STAGE_LABELS.map(({ key, label }) => {
            const done = rows.filter((o) => o.stages[key]).length;
            const pct = (done / total) * 100;
            return (
              <div key={key} className="min-w-0">
                <div className="mb-1 flex justify-between text-xs text-gray-600">
                  <span>{label}</span>
                  <span className="font-semibold text-gray-800">
                    {done} / {total}
                  </span>
                </div>
                <div className="h-2 w-full overflow-hidden rounded bg-gray-100">
                  <div className="h-full bg-blue-500" style={{ width: `${pct}%` }} />
                </div>
              </div>
            );
          })}
        </div>
      </section>

      {/* Gate health */}
      <section className="grid grid-cols-1 gap-3 lg:grid-cols-3">
        {GATES.map((g) => {
          let notRun = 0;
          const totals: Record<string, number> = {};
          for (const o of rows) {
            const c = o.gateCounts[g];
            if (!c) {
              notRun++;
              continue;
            }
            for (const [k, n] of Object.entries(c)) totals[k] = (totals[k] ?? 0) + n;
          }
          const sumOf = (keys: readonly string[]) => keys.reduce((s, k) => s + (totals[k] ?? 0), 0);
          const match = sumOf(MATCH_VERDICTS);
          const problems = sumOf(PROBLEM_VERDICTS);
          const unverifiable = totals["Unverifiable"] ?? 0;
          const other = Object.values(totals).reduce((s, n) => s + n, 0) - match - problems - unverifiable;
          const worst = rows
            .filter((o) => problemsIn(o.gateCounts[g]) > 0)
            .sort((a, b) => problemsIn(b.gateCounts[g]) - problemsIn(a.gateCounts[g]))
            .slice(0, 3);

          return (
            <div key={g} className="min-w-0 rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
              <div className="mb-2 flex items-baseline justify-between">
                <h3 className="text-sm font-semibold text-gray-800">{GATE_NAME[g]}</h3>
                {notRun > 0 && <span className="text-xs text-gray-400">{notRun} not run</span>}
              </div>
              <StackedBar
                segments={[
                  { label: "Matched", value: match, className: "bg-green-500" },
                  { label: "Problems", value: problems, className: "bg-red-500" },
                  { label: "Unverifiable", value: unverifiable, className: "bg-gray-300" },
                  { label: "Other", value: Math.max(other, 0), className: "bg-amber-400" },
                ]}
              />
              <div className="mt-2 flex flex-wrap gap-x-3 text-xs text-gray-600">
                <span>Matched <strong>{match}</strong></span>
                <span>Problems <strong className="text-red-700">{problems}</strong></span>
                <span>Unverifiable <strong>{unverifiable}</strong></span>
              </div>
              {worst.length > 0 ? (
                <ul className="mt-3 space-y-1 text-xs">
                  {worst.map((o) => (
                    <li key={o.package} className="flex justify-between gap-2">
                      <Link
                        to={`${base}/${encodeURIComponent(o.package)}/compare`}
                        className="truncate text-blue-700 hover:underline"
                      >
                        {o.package}
                      </Link>
                      <span className="shrink-0 text-red-700">{problemsIn(o.gateCounts[g])} problems</span>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="mt-3 text-xs text-green-700">No problems in this gate.</p>
              )}
            </div>
          );
        })}
      </section>

      {/* Package grid */}
      <section>
        <div className="mb-3 flex flex-wrap items-center gap-2">
          <h3 className="mr-2 text-sm font-semibold text-gray-800">Packages</h3>
          {(["all", "attention", "clean"] as Filter[]).map((f) => (
            <button
              key={f}
              type="button"
              onClick={() => setFilter(f)}
              className={`cursor-pointer rounded-full border px-3 py-1 text-xs ${
                filter === f
                  ? "border-blue-600 bg-blue-50 font-semibold text-blue-700"
                  : "border-gray-300 bg-white text-gray-600 hover:bg-gray-50"
              }`}
            >
              {f === "all" ? `All ${total}` : f === "attention" ? `Needs attention ${total - cleanCount}` : `Clean ${cleanCount}`}
            </button>
          ))}
          <input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search packages..."
            className="rounded border border-gray-300 px-2 py-1 text-xs"
          />
          <div className="ml-auto flex overflow-hidden rounded border border-gray-300 text-xs">
            {(["cards", "table"] as View[]).map((v) => (
              <button
                key={v}
                type="button"
                onClick={() => setView(v)}
                className={`cursor-pointer px-3 py-1 ${
                  view === v ? "bg-blue-600 text-white" : "bg-white text-gray-600 hover:bg-gray-50"
                }`}
              >
                {v === "cards" ? "Cards" : "Table"}
              </button>
            ))}
          </div>
        </div>

        {view === "table" ? (
          table
        ) : shown.length === 0 ? (
          <p className="text-sm text-gray-500">No packages match these filters.</p>
        ) : (
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
            {shown.map((o) => (
              <PackageCard key={o.package} o={o} base={base} />
            ))}
          </div>
        )}
      </section>
    </div>
  );
}

function PackageCard({ o, base }: { o: ValidationOverview; base: string }) {
  const problems = countProblems(GATES.map((g) => o.gateCounts[g]));
  // Problems open the Compare tab, review-only items open Rollup.
  const target = problems > 0 ? "compare" : "rollup";
  return (
    <Link
      to={`${base}/${encodeURIComponent(o.package)}/${target}`}
      className="group min-w-0 rounded-lg border border-gray-200 bg-white p-4 shadow-sm transition-colors hover:border-blue-300"
    >
      <div className="mb-3 flex items-center justify-between gap-2">
        <span className="flex min-w-0 items-center">
          <span className="truncate font-semibold text-gray-900 group-hover:text-blue-700">{o.package}</span>
          <ValidationStatusBadge kind={isClean(o) ? "clean" : "review"} />
        </span>
        <span className="shrink-0 text-xs text-gray-500">
          {o.bucketCounts.humanNeedsReview} to review
        </span>
      </div>

      <StackedBar segments={BUCKET_SEGMENTS(o.bucketCounts)} />

      <div className="mt-3 flex flex-wrap gap-1.5">
        {GATES.map((g) => (
          <span
            key={g}
            title={`${GATE_NAME[g]}: ${gateChipTitle(o.gateCounts[g])}`}
            className={`rounded px-2 py-0.5 text-[11px] font-medium ${gateChipClass(o.gateCounts[g])}`}
          >
            G{g}
          </span>
        ))}
      </div>

      <div className="mt-3 flex items-center gap-3 text-[11px] text-gray-500">
        {STAGE_LABELS.map(({ key, label }) => (
          <span key={key} className="flex items-center gap-1" title={label}>
            <span className={`inline-block h-2 w-2 rounded-full ${o.stages[key] ? "bg-green-500" : "bg-gray-300"}`} />
            {label}
          </span>
        ))}
      </div>
    </Link>
  );
}
