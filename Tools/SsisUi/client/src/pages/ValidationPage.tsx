import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Link, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  getPackageValidation,
  getValidation,
  type GateFinding,
  type GateReport,
  type PackageValidation,
  type SummaryItem,
  type ValidationOverview,
  type Workspace,
} from "../api/client";
import { countProblems, isClean } from "../utils/validationStatus";
import MarkdownView from "../components/MarkdownView";
import ValidationDashboard from "../components/ValidationDashboard";
import ValidationStatusBadge from "../components/ValidationStatusBadge";
import TabNav from "../components/TabNav";
import EmptyState from "../components/EmptyState";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";

/** Verdicts that mean "something differs between the .dtsx and the generated code". */
const PROBLEM_VERDICTS = new Set(["Mismatch", "LeftOnly", "RightOnly", "Unrecognized"]);
const MATCH_VERDICTS = new Set(["Match", "NormalizedMatch"]);

const TABS = ["rollup", "compare", "tier2", "files"] as const;
type Tab = (typeof TABS)[number];
const TAB_LABEL: Record<Tab, string> = {
  rollup: "Rollup",
  compare: "Compare",
  tier2: "Tier 2",
  files: "Files",
};

const INTRO =
  "Each package's .dtsx (what SSIS declares) is checked against its generated C# (what the rewrite does). " +
  "Gate 1 compares SQL, Gate 2 the control and data-flow graph, Gate 3 each component's columns. " +
  "Unverifiable is shown as its own state, not as a pass or a fail.";

function verdictClass(verdict: string | undefined | null): string {
  if (!verdict) return "bg-gray-100 text-gray-600";
  if (MATCH_VERDICTS.has(verdict)) return "bg-green-100 text-green-800";
  if (PROBLEM_VERDICTS.has(verdict)) return "bg-red-100 text-red-800";
  if (verdict.startsWith("Ai")) return "bg-teal-100 text-teal-800";
  if (verdict === "Unverifiable") return "bg-gray-200 text-gray-700";
  return "bg-amber-100 text-amber-800";
}

function VerdictBadge({ verdict }: { verdict: string | undefined | null }) {
  return (
    <span className={`inline-block rounded px-1.5 py-0.5 text-[11px] font-medium ${verdictClass(verdict)}`}>
      {verdict ?? "—"}
    </span>
  );
}

/** Renders any value a gate finding carries (string, object, array) readably. */
function show(value: unknown): string {
  if (value === null || value === undefined) return "—";
  if (typeof value === "string") return value;
  return JSON.stringify(value, null, 2);
}

function countsText(counts: Record<string, number> | null): string {
  if (!counts) return "not run";
  const parts = Object.entries(counts)
    .filter(([, n]) => n > 0)
    .map(([k, n]) => `${k} ${n}`);
  return parts.length > 0 ? parts.join(" · ") : "no findings";
}

export default function ValidationPage({ workspace }: { workspace: Workspace }) {
  const { pkg } = useParams<{ pkg?: string }>();
  return pkg ? (
    <PackageView workspace={workspace} pkg={pkg} />
  ) : (
    <Overview workspace={workspace} />
  );
}

function Overview({ workspace }: { workspace: Workspace }) {
  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "validation"],
    queryFn: () => getValidation(workspace.id),
  });
  const base = `/workspaces/${workspace.id}/validation`;

  if (isLoading) {
    return (
      <div>
        <LoadingState label="Loading validation..." />
      </div>
    );
  }
  if (error) {
    return (
      <div>
        <ErrorState error={error} />
      </div>
    );
  }
  if (!data || data.length === 0) {
    return (
      <div>
        <h2 className="mb-1 text-base font-semibold">Validation</h2>
        <p className="mb-3 text-sm text-gray-500">{INTRO}</p>
        <EmptyState message="Nothing validated yet -- run Phase 2 on the Pipeline page first (Generate must have run)." />
      </div>
    );
  }

  // Worst first: the packages with the most human work or real problems on top.
  const problems = (o: ValidationOverview) =>
    (["1", "2", "3"] as const).reduce(
      (sum, g) =>
        sum +
        Object.entries(o.gateCounts[g] ?? {})
          .filter(([k]) => PROBLEM_VERDICTS.has(k))
          .reduce((s, [, n]) => s + n, 0),
      0,
    );
  const rows = [...data].sort(
    (a, b) => b.bucketCounts.humanNeedsReview + problems(b) - (a.bucketCounts.humanNeedsReview + problems(a)),
  );
  return (
    <div>
      <h2 className="mb-1 text-base font-semibold">Validation</h2>
      <p className="mb-3 text-sm text-gray-500">{INTRO}</p>

      <ValidationDashboard
        rows={rows}
        base={base}
        table={
          <div className="overflow-x-auto rounded-lg border border-gray-200 bg-white shadow-sm">
            <table className="min-w-full text-left text-sm">
              <thead className="bg-gray-50 text-xs uppercase tracking-wide text-gray-500">
                <tr>
                  <th className="px-3 py-2">Package</th>
                  <th className="px-3 py-2">Tool-Verified</th>
                  <th className="px-3 py-2">Exception</th>
                  <th className="px-3 py-2">AI-Verified</th>
                  <th className="px-3 py-2">Needs review</th>
                  <th className="px-3 py-2">Gate 1</th>
                  <th className="px-3 py-2">Gate 2</th>
                  <th className="px-3 py-2">Gate 3</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((o) => (
                  <tr key={o.package} className="border-t border-gray-100 align-top">
                    <td className="px-3 py-2">
                      <Link
                        to={`${base}/${encodeURIComponent(o.package)}`}
                        className="font-medium text-blue-700 hover:underline"
                      >
                        {o.package}
                      </Link>
                      <ValidationStatusBadge kind={isClean(o) ? "clean" : "review"} />
                    </td>
                    <td className="px-3 py-2 text-green-700">{o.bucketCounts.toolVerified}</td>
                    <td className="px-3 py-2 text-gray-500">{o.bucketCounts.exception}</td>
                    <td className="px-3 py-2 text-teal-700">{o.bucketCounts.aiVerified}</td>
                    <td className="px-3 py-2 font-semibold text-amber-700">{o.bucketCounts.humanNeedsReview}</td>
                    <td className="px-3 py-2 text-xs text-gray-600">{countsText(o.gateCounts["1"])}</td>
                    <td className="px-3 py-2 text-xs text-gray-600">{countsText(o.gateCounts["2"])}</td>
                    <td className="px-3 py-2 text-xs text-gray-600">{countsText(o.gateCounts["3"])}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        }
      />
    </div>
  );
}

function PackageView({ workspace, pkg }: { workspace: Workspace; pkg: string }) {
  const params = useParams<{ tab?: string }>();
  const tab: Tab = (TABS as readonly string[]).includes(params.tab ?? "")
    ? (params.tab as Tab)
    : "rollup";
  const base = `/workspaces/${workspace.id}/validation/${encodeURIComponent(pkg)}`;

  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "validation", pkg],
    queryFn: () => getPackageValidation(workspace.id, pkg),
  });

  const header = (
    <>
      <div className="mb-1 flex items-center gap-2">
        <h2 className="text-base font-semibold">{pkg}</h2>
      </div>
    </>
  );

  if (isLoading) {
    return (
      <div>
        {header}
        <LoadingState label={`Loading ${pkg}...`} />
      </div>
    );
  }
  if (error) {
    return (
      <div>
        {header}
        <ErrorState error={error} />
      </div>
    );
  }
  if (!data || !data.stages.validated) {
    return (
      <div>
        {header}
        <EmptyState message={`${pkg} has no gate reports yet -- run Validate on the Pipeline page.`} />
      </div>
    );
  }

  return (
    <div>
      {header}
      <PackageBanner data={data} />

      <p className="mb-3 text-sm text-gray-500">{INTRO}</p>

      <div className="mb-3 flex flex-wrap gap-2 text-xs">
        <StageChip label="Gates" on={data.stages.validated} />
        <StageChip label="Evidence packet" on={data.stages.packet} />
        <StageChip label="AI review" on={data.stages.review} />
        <StageChip label="Final summary" on={data.stages.finalSummary} />
      </div>

      <div className="mb-4 flex gap-1 border-b border-gray-200">
        {TABS.map((t) => (
          <Link
            key={t}
            to={`${base}/${t}`}
            className={`-mb-px border-b-2 px-3 py-2 text-sm ${
              t === tab
                ? "border-blue-600 font-semibold text-blue-700"
                : "border-transparent text-gray-600 hover:text-gray-900"
            }`}
          >
            {TAB_LABEL[t]}
          </Link>
        ))}
      </div>

      <TabInfo tab={tab} />

      {tab === "rollup" && <RollupTab data={data} />}
      {tab === "compare" && <CompareTab data={data} />}
      {tab === "tier2" && <Tier2Tab data={data} />}
      {tab === "files" && <FilesTab data={data} workspace={workspace} />}
    </div>
  );
}

const GATE_HELP = {
  1: {
    name: "Gate 1 — SQL",
    summary: "Every SQL statement in the .dtsx against the SQL the generated C# runs.",
    body: (
      <>
        <p>
          Covers each Execute SQL Task, OLE DB / ADO.NET source and destination query, Lookup reference
          query and OLE DB Command. Items are matched by name first (the SSIS task or component name against
          the generated C# member), then each pair is compared as <em>parsed SQL</em> (structure, not raw
          text), so whitespace and formatting differences do not count as mismatches.
        </p>
        <p className="mt-2">
          <strong>Verdicts:</strong> <em>Match</em> / <em>NormalizedMatch</em> (same SQL after normalizing
          formatting), <em>Mismatch</em> (the SQL differs), <em>LeftOnly</em> (in the .dtsx, not generated),{" "}
          <em>RightOnly</em> (generated, not in the .dtsx), <em>Unrecognized</em> (a SQL shape the parser could
          not read), <em>Unverifiable</em> (no counterpart to check).
        </p>
      </>
    ),
  },
  2: {
    name: "Gate 2 — Graph",
    summary: "The control-flow order and parallel groups of tasks and containers.",
    body: (
      <>
        <p>
          Walks the .dtsx control-flow graph level by level: what must run before what, and what may run in
          parallel. The generated call order and its concurrent groups (the <code>Task.WhenAll</code> blocks)
          are checked against that. Components inside one Data Flow Task only get a name-presence check, since
          a name match alone is weak evidence. If Gate 3 already compared that component's columns, Gate 2
          defers to Gate 3's stronger answer.
        </p>
        <p className="mt-2">
          <strong>Verdicts:</strong> <em>Match</em>, <em>Mismatch</em> (order or grouping differs),{" "}
          <em>LeftOnly</em> (a node in the .dtsx with no generated counterpart), <em>RightOnly</em> (a generated
          node with no .dtsx counterpart), <em>Unverifiable</em> (name-only evidence).
        </p>
      </>
    ),
  },
  3: {
    name: "Gate 3 — Schema",
    summary: "Each data-flow component's input and output columns: names and types.",
    body: (
      <>
        <p>
          For every component with a real declared column shape, the validator finds its generated counterpart:
          a destination by its table identity (reusing Gate 1's match), a source by its generated factory method,
          a flat-file destination by its enclosing method. It then compares column names and the .NET types
          implied by the SSIS data types (using Microsoft's SSIS-to-.NET mapping).
        </p>
        <p className="mt-2">
          <strong>Verdicts:</strong> <em>Match</em>, <em>Mismatch</em> (a column's type or name differs),{" "}
          <em>LeftOnly</em> (an SSIS column with no generated property), <em>RightOnly</em> (a generated
          property with no SSIS column), <em>Unverifiable</em> (no counterpart could be correlated).
        </p>
      </>
    ),
  },
} as const;

const TAB_HELP: Record<Tab, { summary: string; details: ReactNode }> = {
  rollup: {
    summary:
      "The package's final tally: every finding sorted into four buckets. Human-Needs-Review is the work list for a person.",
    details: (
      <>
        <p>
          The Rollup merges the Gate 1, 2 and 3 reports with the AI review (if one was run) into four buckets:
        </p>
        <ul className="ml-5 mt-2 list-disc space-y-1">
          <li><strong>Tool-Verified</strong>: the gates proved the match on their own. No AI involved.</li>
          <li><strong>Exception</strong>: a finding that is accepted by design, with its reason shown.</li>
          <li><strong>AI-Verified</strong>: the gates could not settle it, and an AI review confirmed it. Advisory only.</li>
          <li><strong>Human-Needs-Review</strong>: a real problem, an unresolved gap, or an AI verdict that needs a second look.</li>
        </ul>
        <p className="mt-2">
          Nothing here is auto-approved. A bucket only moves because a gate or a recorded review says so. The final
          summary is produced by <code>ssis-final-summary</code>, which runs after the gates and any AI review.
        </p>
      </>
    ),
  },
  compare: {
    summary:
      "Side by side: what the .dtsx declares (SSIS side) against what the generated C# does (Generated side), one row per finding.",
    details: (
      <>
        <p>
          Each row pairs the SSIS side with the Generated side and shows the verdict. Use the Gate 1 / 2 / 3 tabs to
          switch between the three checks. Rows are sorted so problems come first, then Unverifiable, then verified
          matches. Untick a verdict in the Verdict list to hide it; every verdict starts ticked.
        </p>
        <p className="mt-2 font-semibold text-gray-800">The three gates</p>
        <div className="mt-1 space-y-3">
          {([1, 2, 3] as const).map((g) => (
            <div key={g} className="rounded border border-gray-200 bg-gray-50 p-3">
              <p className="font-semibold text-gray-800">{GATE_HELP[g].name}</p>
              <p className="mb-1 text-gray-600">{GATE_HELP[g].summary}</p>
              {GATE_HELP[g].body}
            </div>
          ))}
        </div>
      </>
    ),
  },
  tier2: {
    summary:
      "The AI review of findings the gates could not settle alone. It is advisory: a person confirms each verdict before it counts.",
    details: (
      <>
        <p>
          Some findings have no deterministic answer: a component the gates could not correlate, or a shape the
          parser could not read. For those, the validator builds an evidence packet (the .dtsx fragment, the
          generated code excerpt and any known correlate). An AI assistant judges only that packet and writes a
          review with a verdict per component, citing its evidence.
        </p>
        <p className="mt-2">
          Verdicts: <em>AiConfirmedMatch</em>, <em>AiSuspectedDiscrepancy</em>, <em>AiInconclusive</em> (not enough
          evidence, never a guessed match), <em>AiNotApplicable</em>. An <em>AiSuspectedDiscrepancy</em> is a
          prompt for a human, not a verdict. The review never edits the gate reports.
        </p>
        <p className="mt-2">
          To produce a review, run <code>/ssis-tier2-review &lt;Package&gt;</code> in the validator folder (or use
          the AI review dialog on the Pipeline page).
        </p>
      </>
    ),
  },
  files: {
    summary:
      "The raw report files on disk for this package. Open them to read the full evidence behind any number on this page.",
    details: (
      <>
        <p>
          The Files tab lists where each report lives: the three gate reports (JSON and Markdown), the evidence
          packet, the AI review and the final summary. Everything shown in the other tabs is read from these files,
          so they are the source of truth. Nothing here is edited by the app.
        </p>
      </>
    ),
  },
};

/** One short line under the tab bar, with a "More details" dialog that explains the tab in depth. */
function TabInfo({ tab }: { tab: Tab }) {
  const [open, setOpen] = useState(false);
  const help = TAB_HELP[tab];

  useEffect(() => {
    if (!open) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [open]);

  return (
    <>
      <div className="mb-4 flex flex-wrap items-start gap-x-3 gap-y-1 rounded border border-blue-100 bg-blue-50 px-3 py-2 text-sm text-gray-700">
        <p className="min-w-0 flex-1">{help.summary}</p>
        <button
          type="button"
          className="shrink-0 cursor-pointer text-sm font-medium text-blue-700 hover:underline"
          onClick={() => setOpen(true)}
        >
          More details
        </button>
      </div>

      {open && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-gray-900/40 p-4"
          onMouseDown={(e) => {
            if (e.target === e.currentTarget) setOpen(false);
          }}
        >
          <div
            role="dialog"
            aria-modal="true"
            aria-label={`About the ${TAB_LABEL[tab]} tab`}
            className="flex max-h-[90vh] w-full min-w-0 max-w-2xl flex-col rounded-lg border border-gray-200 bg-white shadow-xl"
          >
            <div className="flex shrink-0 items-center justify-between border-b border-gray-200 px-4 py-3">
              <h2 className="text-base font-semibold text-gray-800">About the {TAB_LABEL[tab]} tab</h2>
              <button
                type="button"
                aria-label="Close"
                className="cursor-pointer rounded px-2 py-1 text-sm text-gray-500 hover:bg-gray-100"
                onClick={() => setOpen(false)}
              >
                ✕
              </button>
            </div>
            <div className="min-w-0 flex-1 overflow-y-auto px-4 py-3 text-sm text-gray-700">
              <p className="mb-3 font-medium text-gray-800">{help.summary}</p>
              {help.details}
            </div>
            <div className="flex shrink-0 items-center justify-end border-t border-gray-200 px-4 py-3">
              <button
                type="button"
                className="cursor-pointer rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
                onClick={() => setOpen(false)}
              >
                Close
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

/** The first thing on a package page: green when nothing needs a person, amber otherwise. */
function PackageBanner({ data }: { data: PackageValidation }) {
  if (!data.finalSummary) {
    return (
      <div className="mb-3 rounded border border-gray-200 bg-gray-50 p-3 text-sm text-gray-600">
        Final summary not run yet -- run Final Summary on the Pipeline page to see what needs review.
      </div>
    );
  }
  const problems = countProblems(Object.values(data.gates).map((g) => g?.counts ?? null));
  const toolVerified = data.finalSummary.toolVerified.length;
  const aiVerified = data.finalSummary.aiVerified.length;
  const needsReview = data.finalSummary.humanNeedsReview.length;

  if (problems === 0 && needsReview === 0) {
    const aiNote =
      aiVerified > 0
        ? `, and ${aiVerified} ${aiVerified === 1 ? "was" : "were"} confirmed by the AI review`
        : "";
    return (
      <div className="mb-3 flex items-start gap-2 rounded border border-green-200 bg-green-50 p-3 text-sm text-green-900">
        <span className="mt-0.5 inline-flex h-4 w-4 shrink-0 items-center justify-center rounded-full bg-green-600 text-white">
          <svg viewBox="0 0 20 20" fill="currentColor" className="h-2.5 w-2.5" aria-hidden="true">
            <path
              fillRule="evenodd"
              d="M16.7 5.3a1 1 0 0 1 0 1.4l-7.5 7.5a1 1 0 0 1-1.4 0L3.3 9.7a1 1 0 1 1 1.4-1.4l4 4 6.8-6.8a1 1 0 0 1 1.4 0z"
              clipRule="evenodd"
            />
          </svg>
        </span>
        <span>
          All {toolVerified} checks passed. Nothing needs review{aiNote}.
        </span>
      </div>
    );
  }

  const parts = [
    needsReview > 0 ? `${needsReview} ${needsReview === 1 ? "item needs" : "items need"} review` : null,
    problems > 0 ? `${problems} real ${problems === 1 ? "problem" : "problems"} found across the gates` : null,
  ].filter(Boolean);
  return (
    <div className="mb-3 rounded border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900">
      {parts.join(", and ")}. Start with Human-Needs-Review in the Rollup tab.
    </div>
  );
}

function StageChip({ label, on }: { label: string; on: boolean }) {
  return (
    <span
      className={`rounded-full px-2 py-0.5 ${
        on ? "bg-green-100 text-green-800" : "bg-gray-100 text-gray-500"
      }`}
    >
      {on ? "✓" : "–"} {label}
    </span>
  );
}

const BUCKETS: { key: keyof PackageValidationBuckets; title: string; hint: string; open: boolean }[] = [
  { key: "humanNeedsReview", title: "Human-Needs-Review", hint: "Work for a person: start here.", open: true },
  { key: "aiVerified", title: "AI-Verified", hint: "The validator could not settle these alone; the AI review confirmed them.", open: false },
  { key: "toolVerified", title: "Tool-Verified", hint: "Proved correct by the validator alone.", open: false },
  { key: "exception", title: "Exception", hint: "Nothing to check: the SSIS task is disabled, so nothing was generated for it.", open: false },
];

type PackageValidationBuckets = {
  toolVerified: SummaryItem[];
  exception: SummaryItem[];
  aiVerified: SummaryItem[];
  humanNeedsReview: SummaryItem[];
};

function RollupTab({ data }: { data: PackageValidation }) {
  if (!data.finalSummary) {
    return (
      <EmptyState message="No final summary yet -- run Final Summary on the Pipeline page after Validate." />
    );
  }
  return (
    <div className="space-y-3">
      {BUCKETS.map((b) => {
        const items = data.finalSummary![b.key];
        return (
          <details key={b.key} open={b.open} className="rounded-lg border border-gray-200 bg-white shadow-sm">
            <summary className="cursor-pointer px-3 py-2 text-sm font-semibold">
              {b.title} <span className="font-normal text-gray-500">({items.length}) -- {b.hint}</span>
            </summary>
            {items.length === 0 ? (
              <p className="px-3 pb-3 text-sm text-gray-500">None.</p>
            ) : (
              <ul className="divide-y divide-gray-100">
                {items.map((item, i) => (
                  <li key={i} className="px-3 py-2 text-sm">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-mono text-xs text-gray-500">Gate {item.gate}</span>
                      <span className="font-medium">{item.objectName ?? item.componentPath ?? "(unnamed)"}</span>
                      {item.scope && <span className="text-xs text-gray-400">{item.scope}</span>}
                      <VerdictBadge verdict={item.tier1Verdict} />
                      {item.tier2Verdict && <VerdictBadge verdict={item.tier2Verdict} />}
                    </div>
                    {item.reason && <p className="mt-1 text-xs text-gray-600">{item.reason}</p>}
                    {item.tier2Rationale && (
                      <p className="mt-1 text-xs text-teal-800">AI: {item.tier2Rationale}</p>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </details>
        );
      })}
    </div>
  );
}

/** Sort order for the Compare table: problems first, then unverifiable, then
 * verified matches, then anything else. */
function verdictRank(verdict: string | null | undefined): number {
  const v = verdict ?? "";
  if (PROBLEM_VERDICTS.has(v)) return 0;
  if (v === "Unverifiable") return 1;
  if (MATCH_VERDICTS.has(v)) return 2;
  return 3;
}

const GATE_TABS: { key: "1" | "2" | "3"; label: string }[] = [
  { key: "1", label: "Gate 1" },
  { key: "2", label: "Gate 2" },
  { key: "3", label: "Gate 3" },
];

/**
 * A button that opens a checkbox list. Every option starts ticked; unticking
 * one lands in `excluded`. With `searchable`, a search box at the top of the
 * list narrows the options shown (ticks on hidden options are kept).
 */
function CheckboxDropdown({
  label,
  options,
  excluded,
  onToggle,
  searchable = false,
  renderOption,
}: {
  label: string;
  options: string[];
  excluded: Set<string>;
  onToggle: (value: string) => void;
  searchable?: boolean;
  renderOption?: (value: string) => ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const [term, setTerm] = useState("");
  const ref = useRef<HTMLDivElement>(null);

  // Close on Escape or any click outside the dropdown.
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const selected = options.filter((o) => !excluded.has(o)).length;
  const summary =
    selected === options.length ? "All selected" : selected === 0 ? "None selected" : `${selected}/${options.length}`;
  const needle = term.trim().toLowerCase();
  const shown = needle ? options.filter((o) => o.toLowerCase().includes(needle)) : options;

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className="flex min-w-[160px] cursor-pointer select-none items-center gap-1 rounded border border-gray-300 bg-white px-3 py-1 text-sm"
      >
        {label} · {summary}
        <span aria-hidden className="ml-auto pl-2 text-xs text-gray-500">&#9662;</span>
      </button>
      {open && (
        <div className="absolute inset-x-0 z-10 mt-1 rounded border border-gray-200 bg-white p-3 text-xs text-gray-700 shadow-lg">
          {searchable && (
            <input
              autoFocus
              value={term}
              onChange={(e) => setTerm(e.target.value)}
              placeholder="Search..."
              className="mb-2 w-full rounded border border-gray-300 px-2 py-1 text-sm"
            />
          )}
          <div className="max-h-64 space-y-2 overflow-y-auto">
            {shown.length === 0 ? (
              <p className="text-gray-400">No matches</p>
            ) : (
              shown.map((o) => (
                <label key={o} className="flex cursor-pointer items-center gap-2">
                  <input type="checkbox" checked={!excluded.has(o)} onChange={() => onToggle(o)} />
                  {renderOption ? renderOption(o) : <span className="break-all">{o}</span>}
                </label>
              ))
            )}
          </div>
        </div>
      )}
    </div>
  );
}

function CompareTab({ data }: { data: PackageValidation }) {
  const [gate, setGate] = useState<"1" | "2" | "3">("1");
  // Verdicts and objects the user has unticked. Empty by default, so everything starts selected.
  const [excluded, setExcluded] = useState<Set<string>>(() => new Set());
  const [excludedObjects, setExcludedObjects] = useState<Set<string>>(() => new Set());

  const report: GateReport | null = data.gates[gate];

  // Every verdict this gate actually produced, in sort order -- drives the checkbox list.
  const verdictOptions = useMemo(() => {
    const seen = new Set((report?.findings ?? []).map((f) => f.verdict ?? "—"));
    return [...seen].sort((a, b) => verdictRank(a) - verdictRank(b) || a.localeCompare(b));
  }, [report]);

  // Every object name this gate produced, sorted -- drives the object checkbox list.
  const objectOptions = useMemo(() => {
    const seen = new Set((report?.findings ?? []).map((f) => f.objectName ?? "(unnamed)"));
    return [...seen].sort((a, b) => a.localeCompare(b));
  }, [report]);

  const findings = useMemo(() => {
    return (report?.findings ?? [])
      .filter((f: GateFinding) => {
        if (excluded.has(f.verdict ?? "—")) return false;
        if (excludedObjects.has(f.objectName ?? "(unnamed)")) return false;
        return true;
      })
      .sort((a, b) => verdictRank(a.verdict) - verdictRank(b.verdict));
  }, [report, excluded, excludedObjects]);

  function toggle(set: Set<string>, value: string): Set<string> {
    const next = new Set(set);
    if (next.has(value)) next.delete(value);
    else next.add(value);
    return next;
  }

  if (!report) {
    return <EmptyState message={`Gate ${gate} has not been run for this package.`} />;
  }

  return (
    <div>
      <TabNav tabs={GATE_TABS} active={gate} onSelect={setGate} />

      <div className="mb-3 flex flex-wrap items-center gap-2">
        {Object.entries(report.counts ?? {})
          .filter(([, n]) => n > 0)
          .map(([k, n]) => (
            <span
              key={k}
              className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${verdictClass(k)}`}
            >
              {k}: {n}
            </span>
          ))}
        <div className="ml-auto flex flex-wrap items-center gap-2">
          <CheckboxDropdown
            label="Verdict"
            options={verdictOptions}
            excluded={excluded}
            onToggle={(v) => setExcluded((prev) => toggle(prev, v))}
            renderOption={(v) => <VerdictBadge verdict={v} />}
          />
          <CheckboxDropdown
            label="Object"
            options={objectOptions}
            excluded={excludedObjects}
            onToggle={(o) => setExcludedObjects((prev) => toggle(prev, o))}
            searchable
          />
        </div>
      </div>

      {findings.length === 0 ? (
        <EmptyState message="No findings to show with these filters." />
      ) : (
        <div className="overflow-x-auto rounded-lg border border-gray-200 bg-white shadow-sm">
          <table className="min-w-full text-left text-sm">
            <thead className="bg-gray-50 text-xs uppercase tracking-wide text-gray-500">
              <tr>
                <th className="px-3 py-2">Object</th>
                <th className="px-3 py-2">SSIS side</th>
                <th className="px-3 py-2">Generated side</th>
                <th className="px-3 py-2">Verdict</th>
              </tr>
            </thead>
            <tbody>
              {findings.map((f, i) => (
                <tr key={i} className="border-t border-gray-100 align-top">
                  <td className="px-3 py-2">
                    <div className="font-medium">{f.objectName ?? "(unnamed)"}</div>
                    {f.scope && <div className="text-xs text-gray-400">{f.scope}</div>}
                  </td>
                  <td className="px-3 py-2">
                    <pre className="whitespace-pre-wrap break-all font-mono text-[11px] text-gray-700">
                      {show(f.left ?? f.detail)}
                    </pre>
                  </td>
                  <td className="px-3 py-2">
                    <pre className="whitespace-pre-wrap break-all font-mono text-[11px] text-gray-700">
                      {show(f.right ?? f.evidence)}
                    </pre>
                  </td>
                  <td className="px-3 py-2">
                    <VerdictBadge verdict={f.verdict} />
                    {f.detail && f.left !== undefined && (
                      <p className="mt-1 max-w-xs text-xs text-gray-500">{f.detail}</p>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function Tier2Tab({ data }: { data: PackageValidation }) {
  if (!data.packetMarkdown && !data.aiReview) {
    return (
      <EmptyState message="No evidence packet yet. Run Evidence Packets on the Pipeline page, then do the AI review in Claude or Copilot with /ssis-tier2-review." />
    );
  }
  return (
    <div className="space-y-4">
      <div className="rounded border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
        Advisory only. The AI review is a suggestion for a person to check, not a verdict. Gate reports are never changed by it.
      </div>
      {data.aiReview ? (
        <AiReviewView review={data.aiReview} />
      ) : (
        <p className="text-sm text-gray-500">The AI review has not been written yet.</p>
      )}
      {data.packetMarkdown && (
        <div>
          <h4 className="mb-1 text-sm font-semibold text-gray-700">Evidence packet</h4>
          <div className="max-h-[40rem] overflow-auto rounded border border-gray-200 bg-white p-4">
            <MarkdownView markdown={data.packetMarkdown} />
          </div>
        </div>
      )}
    </div>
  );
}

/** One row per reviewed component. Known fields get fixed columns; any other scalar field
 * the review carries gets its own column so nothing is hidden. A Raw JSON toggle shows the
 * file exactly as written. */
function AiReviewView({ review }: { review: unknown }) {
  const [raw, setRaw] = useState(false);
  const rows: Record<string, unknown>[] = Array.isArray(review)
    ? review.filter((r): r is Record<string, unknown> => typeof r === "object" && r !== null)
    : [];
  const fixed = ["component", "scope", "verdict", "rationale"];
  const extra = Array.from(
    new Set(rows.flatMap((r) => Object.keys(r)).filter((k) => !fixed.includes(k))),
  ).filter((k) => rows.some((r) => ["string", "number", "boolean"].includes(typeof r[k])));
  const columns = [...fixed, ...extra];

  return (
    <div>
      <div className="mb-1 flex items-center justify-between">
        <h4 className="text-sm font-semibold text-gray-700">
          AI review {Array.isArray(review) && <span className="font-normal text-gray-500">({rows.length})</span>}
        </h4>
        <label className="flex items-center gap-1 text-xs text-gray-600">
          <input type="checkbox" checked={raw} onChange={(e) => setRaw(e.target.checked)} />
          Raw JSON
        </label>
      </div>

      {raw || rows.length === 0 ? (
        <pre className="max-h-96 overflow-auto rounded bg-gray-50 p-3 text-xs">{show(review)}</pre>
      ) : (
        <div className="overflow-x-auto rounded-lg border border-gray-200 bg-white shadow-sm">
          <table className="min-w-full text-left text-sm">
            <thead className="bg-gray-50 text-xs uppercase tracking-wide text-gray-500">
              <tr>
                {columns.map((c) => (
                  <th key={c} className="px-3 py-2">{c}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={i} className="border-t border-gray-100 align-top">
                  {columns.map((c) => (
                    <td key={c} className="px-3 py-2">
                      {c === "verdict" ? (
                        <VerdictBadge verdict={typeof r[c] === "string" ? (r[c] as string) : null} />
                      ) : c === "rationale" ? (
                        <p className="max-w-md text-xs text-gray-700">{show(r[c])}</p>
                      ) : (
                        <span className="text-xs text-gray-700">{show(r[c])}</span>
                      )}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function FilesTab({ data, workspace }: { data: PackageValidation; workspace: Workspace }) {
  const root = `${workspace.outputPath}\\gate_reports\\${data.package}`;
  const files = [
    `gates\\${data.package}.gate1.json`,
    `gates\\${data.package}.gate2.json`,
    `gates\\${data.package}.gate3.json`,
    `ai_context\\${data.package}.ai-context.md`,
    `ai_context\\${data.package}.ai-review.json`,
    `final_review\\${data.package}.final-summary.json`,
  ];
  return (
    <div>
      <p className="mb-2 text-sm text-gray-500">
        The raw files behind this page. Files that have not been produced yet simply will not exist on disk.
      </p>
      <ul className="space-y-1 font-mono text-xs text-gray-700">
        {files.map((f) => (
          <li key={f}>{root}\{f}</li>
        ))}
      </ul>
    </div>
  );
}
