import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { getCoverageReport, listRuns, type Workspace } from "../api/client";
import { findMarkdownTables } from "../utils/parseMarkdownTable";
import CountChip from "../components/CountChip";
import DataTable from "../components/DataTable";
import MarkdownDrawer from "../components/MarkdownDrawer";
import CoverageInfoContent from "../components/CoverageInfoContent";
import BarChart, { type BarDatum } from "../components/BarChart";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";
import EmptyState from "../components/EmptyState";

/** `Run-Coverage.ps1`'s own table header order -- see
 * Tools/SsisExtractor/scripts/Run-Coverage.ps1. Indices below are derived
 * from this, not hardcoded twice. */
const COL = {
  package: 0,
  testResult: 1,
  linePct: 2,
  branchPct: 3,
  ownPct: 4,
  corePct: 5,
} as const;

/** "67.5%" -> 67.5; "%" (a null PowerShell value renders as a bare "%") or
 * anything unparseable -> null, so a missing value doesn't silently become
 * a false "0%" in an average or a chart. */
function parsePercent(cell: string | undefined): number | null {
  if (!cell) return null;
  const n = Number(cell.replace("%", "").trim());
  return Number.isFinite(n) ? n : null;
}

const COVERAGE_INTRO =
  "Test coverage of ssisx-generated C# code, measured by actually running each package's " +
  "generated .Tests project (produced by the Coverage step on the Pipeline page, after " +
  "Generate and a successful Build). \"Line %\" / \"Branch %\" are blended with the shared " +
  "Etl.Core library every package depends on; \"Own Code %\" counts only that package's own " +
  "generated code and is the more honest figure for judging a starter-test baseline.";

function average(values: (number | null)[]): number | null {
  const valid = values.filter((v): v is number => v !== null);
  if (valid.length === 0) return null;
  return Math.round((valid.reduce((sum, v) => sum + v, 0) / valid.length) * 10) / 10;
}

/** The "Coverage" heading with the page's info icon at its top right. The icon opens
 * the explanation in a modal dialog. */
function CoverageHeading() {
  const [open, setOpen] = useState(false);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [open]);

  return (
    <>
      <div className="mb-1 flex items-start justify-between gap-2">
        <h2 className="text-base font-semibold">Coverage</h2>
        <button
          type="button"
          onClick={() => setOpen(true)}
          aria-label="About coverage"
          className="flex h-5 w-5 shrink-0 cursor-pointer items-center justify-center rounded-full border border-gray-300 text-xs font-semibold leading-none text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-600"
        >
          i
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
            aria-label="About coverage"
            className="flex max-h-[90vh] w-full max-w-2xl flex-col rounded-lg border border-gray-200 bg-white shadow-xl"
          >
            <div className="flex shrink-0 items-center justify-between border-b border-gray-200 px-5 py-3">
              <h2 className="text-base font-semibold text-gray-800">About coverage</h2>
              <button
                type="button"
                aria-label="Close"
                onClick={() => setOpen(false)}
                className="cursor-pointer rounded px-2 py-1 text-sm text-gray-500 hover:bg-gray-100"
              >
                ✕
              </button>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto px-5 py-4">
              <CoverageInfoContent />
            </div>
          </div>
        </div>
      )}
    </>
  );
}

export default function CoveragePage({ workspace }: { workspace: Workspace }) {
  // Watch the run list so the report refreshes itself while a coverage run is in
  // progress (e.g. started by Apply Tests' Done button), instead of showing stale numbers.
  const runsQuery = useQuery({
    queryKey: ["runs", workspace.id],
    queryFn: () => listRuns(workspace.id),
    refetchInterval: 3000,
  });
  const coverageRunning = (runsQuery.data ?? []).some((r) => r.step === "coverage" && r.status === "running");

  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "coverage"],
    queryFn: () => getCoverageReport(workspace.id),
    refetchInterval: coverageRunning ? 3000 : false,
  });

  const [selectedPackage, setSelectedPackage] = useState<string>("__all__");
  const [showReport, setShowReport] = useState(false);

  const table = data?.available ? findMarkdownTables(data.markdown)[0] : undefined;

  const packageNames = useMemo(
    () => (table ? [...new Set(table.rows.map((r) => r[COL.package]))].sort() : []),
    [table],
  );

  const filteredRows = useMemo(() => {
    if (!table) return [];
    if (selectedPackage === "__all__") return table.rows;
    return table.rows.filter((r) => r[COL.package] === selectedPackage);
  }, [table, selectedPackage]);

  if (isLoading) {
    return (
      <div>
        <LoadingState label="Loading coverage report..." />
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
  if (!table || table.rows.length === 0) {
    return (
      <div>
        <CoverageHeading />
        <p className="mb-3 text-sm text-gray-500">{COVERAGE_INTRO}</p>
        <EmptyState message="Run the Coverage step on the Pipeline page first -- it needs Generate (and ideally a successful Build) to have run already." />
      </div>
    );
  }

  const passing = filteredRows.filter((r) => r[COL.testResult].startsWith("PASS")).length;
  const failing = filteredRows.length - passing;

  const linePctBars: BarDatum[] = [];
  const branchPctBars: BarDatum[] = [];
  const ownPctBars: BarDatum[] = [];
  const corePctBars: BarDatum[] = [];
  const linePcts: (number | null)[] = [];
  const branchPcts: (number | null)[] = [];
  const ownPcts: (number | null)[] = [];
  const corePcts: (number | null)[] = [];

  for (const row of [...filteredRows].sort((a, b) => a[COL.package].localeCompare(b[COL.package]))) {
    const pkg = row[COL.package];
    const line = parsePercent(row[COL.linePct]);
    const branch = parsePercent(row[COL.branchPct]);
    const own = parsePercent(row[COL.ownPct]);
    const core = parsePercent(row[COL.corePct]);
    linePcts.push(line);
    branchPcts.push(branch);
    ownPcts.push(own);
    corePcts.push(core);
    // A missing value (a crashed test run never reached coverage collection)
    // renders as 0 in the chart rather than being omitted -- 0 reads correctly
    // here ("no coverage measured"), unlike the averages above which must
    // exclude it instead.
    linePctBars.push({ label: pkg, value: line ?? 0 });
    branchPctBars.push({ label: pkg, value: branch ?? 0 });
    ownPctBars.push({ label: pkg, value: own ?? 0 });
    corePctBars.push({ label: pkg, value: core ?? 0 });
  }

  return (
    <div>
      <CoverageHeading />
      <p className="mb-3 text-sm text-gray-500">{COVERAGE_INTRO}</p>

      <div className="mb-4 flex items-center gap-2">
        <label htmlFor="coverage-package" className="text-sm font-medium text-gray-700">
          Package
        </label>
        <select
          id="coverage-package"
          value={selectedPackage}
          onChange={(e) => setSelectedPackage(e.target.value)}
          className="rounded border border-gray-300 bg-white px-3 py-1.5 text-sm shadow-sm focus:border-blue-500 focus:outline-none"
        >
          <option value="__all__">All packages ({packageNames.length})</option>
          {packageNames.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </select>
        {data?.available && (
          <button
            type="button"
            onClick={() => setShowReport(true)}
            className="cursor-pointer rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50"
          >
            View full report
          </button>
        )}
      </div>
      {showReport && data?.available && (
        <MarkdownDrawer
          label="Coverage"
          title="coverage-report.md"
          markdown={data.markdown}
          onClose={() => setShowReport(false)}
        />
      )}

      <div className="mb-4 flex flex-wrap items-center gap-2">
        <CountChip
          label="Packages"
          value={filteredRows.length}
          color="gray"
          hint="Packages with a generated .Tests project -- a package fully blocked on gaps has none."
        />
        <CountChip
          label="Tests passing"
          value={passing}
          color="green"
          hint="Packages whose generated non-Integration test suite passed."
        />
        <CountChip
          label="Tests failing"
          value={failing}
          color={failing > 0 ? "red" : "green"}
          hint="Packages whose generated test suite failed, or crashed before coverage was even collected."
        />
        <CountChip
          label="Avg own code %"
          value={average(ownPcts) ?? 0}
          color="blue"
          hint="Headline figure: average coverage of each package's own generated code only, excluding the shared Etl.Core library."
        />
        <CountChip
          label="Avg line % (incl. Etl.Core)"
          value={average(linePcts) ?? 0}
          color="gray"
          hint="Blended figure: line coverage including the shared Etl.Core library, which is mostly untested here and pulls this number down."
        />
      </div>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <div>
          <h4 className="mb-1 text-sm font-semibold text-gray-700">Line % by package</h4>
          <BarChart data={linePctBars} unit="%" />
        </div>
        <div>
          <h4 className="mb-1 text-sm font-semibold text-gray-700">Branch % by package</h4>
          <BarChart data={branchPctBars} unit="%" />
        </div>
        <div>
          <h4 className="mb-1 text-sm font-semibold text-gray-700">
            Own generated code % by package
          </h4>
          <BarChart data={ownPctBars} unit="%" />
        </div>
        <div>
          <h4 className="mb-1 text-sm font-semibold text-gray-700">Shared Etl.Core % by package</h4>
          <BarChart data={corePctBars} unit="%" />
        </div>
      </div>

      <h4 className="mb-1 mt-4 text-sm font-semibold text-gray-700">All details</h4>
      <DataTable
        headers={table.headers}
        rows={filteredRows}
        filterPlaceholder="Filter by package..."
      />
    </div>
  );
}
