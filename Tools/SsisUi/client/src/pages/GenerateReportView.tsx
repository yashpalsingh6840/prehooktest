import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { getGaps, getGenerateSummary, getGeneratedFiles, type Workspace } from "../api/client";
import GapBadge from "../components/GapBadge";
import FileBrowserPane from "../components/FileBrowserPane";
import Badge from "../components/Badge";
import PageInfo from "../components/PageInfo";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";
import EmptyState from "../components/EmptyState";

/** Per-package detail view for the Generate Report -- replaces the old flat
 * markdown table (one row per package, no way to open anything it
 * described). Files Written / Starter Tests are now real file browsers, and
 * Gaps is a compact read-only list with a link into the full Gaps Explorer
 * (which already has its own fill-authoring UI, so this doesn't duplicate
 * it). "Packages that crashed/failed to load" live on the Load Failures tab
 * instead -- that's where every other "didn't get processed" list already
 * lives. */
export default function GenerateReportView({ workspace }: { workspace: Workspace }) {
  const navigate = useNavigate();

  const summaryQuery = useQuery({
    queryKey: ["artifacts", workspace.id, "generate-summary"],
    queryFn: () => getGenerateSummary(workspace.id),
  });
  const gapsQuery = useQuery({
    queryKey: ["artifacts", workspace.id, "gaps"],
    queryFn: () => getGaps(workspace.id),
  });

  const [selected, setSelected] = useState<string | null>(null);

  const packages = summaryQuery.data?.packages ?? [];
  const sortedPackages = [...packages].sort((a, b) => a.package.localeCompare(b.package));
  const hasValidSelection = !!selected && packages.some((p) => p.package === selected);

  useEffect(() => {
    if (sortedPackages.length > 0 && !hasValidSelection) {
      setSelected(sortedPackages[0].package);
    }
  }, [sortedPackages, hasValidSelection]);

  const active = hasValidSelection
    ? packages.find((p) => p.package === selected)
    : sortedPackages[0];

  // Every hook must run unconditionally on every render (even while
  // summaryQuery is still loading/errored, handled further below) -- `active`
  // is undefined in that case, so this just stays disabled.
  const filesQuery = useQuery({
    queryKey: ["artifacts", workspace.id, "generate-files", active?.package],
    queryFn: () => getGeneratedFiles(workspace.id, active!.package),
    enabled: !!active,
  });

  const packageGaps = (gapsQuery.data?.gaps ?? []).filter((g) => g.Package === active?.package);

  if (summaryQuery.isLoading) return <LoadingState />;
  if (summaryQuery.error) return <ErrorState error={summaryQuery.error} />;
  if (!summaryQuery.data || !summaryQuery.data.available) {
    return <EmptyState message="Nothing here yet -- run Generate first." />;
  }

  const data = summaryQuery.data;

  const runSummary = (
    <div>
      <h4 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-gray-400">
        Run Summary
      </h4>
      <div className="flex flex-wrap gap-2">
        <span title="Number of packages this generate run processed.">
          <Badge color="gray">Packages: {data.totalPackages}</Badge>
        </span>
        <span title="Total production + test files written across every processed package.">
          <Badge color="blue">Files written: {data.totalFiles}</Badge>
        </span>
        <span title="Total gaps reported across every processed package, blocking and advisory combined.">
          <Badge color={data.totalGaps > 0 ? "amber" : "green"}>Gaps: {data.totalGaps}</Badge>
        </span>
        {data.crashedCount > 0 && (
          <span title="Packages that crashed during generation -- see the Load Failures tab.">
            <Badge color="red">Crashed: {data.crashedCount}</Badge>
          </span>
        )}
      </div>
    </div>
  );

  return (
    <div>
      <PageInfo
        summary="Per-package detail from the last ssisx generate run -- what got written, and every gap that stopped a package from being fully generatable."
        details={
          <p>
            "Files Written" and "Starter Tests" below are real file browsers over the generated
            project, not just a count. A gap here links into the full Gaps Explorer, which has
            its own AI work-packet viewer and fill-authoring UI, so this view stays a summary --
            it doesn't duplicate that. Packages that crashed or failed to load during generation
            (as opposed to one that loaded fine but hit a reported gap) show up on the Load
            Failures tab instead.
          </p>
        }
      />
      {!active ? (
        <>
          <div className="mb-4">{runSummary}</div>
          <EmptyState message="No packages were processed in this generate run." />
        </>
      ) : (
        <>
          {/* One row: the two summaries on the left, the package picker on the right. */}
          <div className="mb-5 flex flex-wrap items-start justify-between gap-4">
            <div className="flex flex-wrap items-start gap-4">
              {runSummary}

              <span className="hidden h-10 w-px shrink-0 self-center bg-gray-200 sm:block" aria-hidden />

              <div>
                <h4 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-gray-400">
                  Package Summary &mdash; <span className="normal-case text-gray-500">{active.package}</span>
                </h4>
                <div className="flex flex-wrap gap-2">
                  <span title={`Production + test files written specifically for ${active.package} -- see the Files Written / Starter Tests lists below.`}>
                    <Badge color="blue">Files written: {active.filesWritten}</Badge>
                  </span>
                  <span title={`Of the files above, how many are generated starter test files for ${active.package} (the Starter Tests list below).`}>
                    <Badge color="purple">Starter tests: {active.starterTests}</Badge>
                  </span>
                  <span title={`Gaps reported specifically for ${active.package} -- see the Gaps table below, or open it in the full Gaps Explorer.`}>
                    <Badge color={active.gaps > 0 ? "amber" : "green"}>Gaps: {active.gaps}</Badge>
                  </span>
                </div>
              </div>
            </div>

            <div>
              <h4 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-gray-400">
                <label htmlFor="generate-package">Package</label>
              </h4>
              <div className="flex items-center gap-2">
              <select
                id="generate-package"
                value={active.package}
                onChange={(e) => setSelected(e.target.value)}
                className="rounded border border-gray-300 bg-white px-3 py-1.5 text-sm shadow-sm focus:border-blue-500 focus:outline-none"
              >
                {sortedPackages.map((p) => (
                  <option key={p.package} value={p.package}>
                    {p.package}
                  </option>
                ))}
              </select>
              <Link
                to={`/workspaces/${workspace.id}/extracted/${encodeURIComponent(active.package)}`}
                title="View extracted JSON"
                aria-label="View extracted JSON"
                className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full border border-sky-300 bg-sky-50 text-sky-700 hover:bg-sky-100"
              >
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className="h-3.5 w-3.5" aria-hidden="true">
                  <path d="M8 4c-2 0-2.5 1-2.5 3v2.5c0 1-.8 2-2 2.5 1.2.5 2 1.5 2 2.5V17c0 2 .5 3 2.5 3M16 4c2 0 2.5 1 2.5 3v2.5c0 1 .8 2 2 2.5-1.2.5-2 1.5-2 2.5V17c0 2-.5 3-2.5 3" />
                </svg>
              </Link>
              </div>
            </div>
          </div>

          <h3 className="mb-2 text-base font-semibold">Files Written</h3>
          {filesQuery.isLoading && <LoadingState label="Loading files..." />}
          {filesQuery.error && <ErrorState error={filesQuery.error} />}
          {filesQuery.data && (
            <FileBrowserPane
              workspaceId={workspace.id}
              files={filesQuery.data.productionFiles}
              emptyMessage="No files were written for this package."
            />
          )}

          <h3 className="mb-2 mt-6 text-base font-semibold">Starter Tests</h3>
          {filesQuery.data && (
            <FileBrowserPane
              workspaceId={workspace.id}
              files={filesQuery.data.testFiles}
              emptyMessage="No starter tests were generated for this package (e.g. --skip-tests, or nothing wired)."
            />
          )}

          <div className="mb-2 mt-6 flex items-center justify-between">
            <h3 className="text-base font-semibold">Gaps ({packageGaps.length})</h3>
            <button
              onClick={() =>
                navigate(`/workspaces/${workspace.id}/gaps`, { state: { package: active.package } })
              }
              className="rounded border border-gray-300 bg-white px-3 py-1 text-xs font-medium text-gray-700 hover:bg-gray-50"
            >
              Open in Gaps Explorer
            </button>
          </div>
          {packageGaps.length === 0 ? (
            <EmptyState message="No gaps for this package." />
          ) : (
            <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
              <table className="w-full border-collapse text-sm">
                <thead>
                  <tr className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    <th className="px-3 py-2">Tier</th>
                    <th className="px-3 py-2">Kind</th>
                    <th className="px-3 py-2">Location</th>
                    <th className="px-3 py-2">Reason</th>
                  </tr>
                </thead>
                <tbody>
                  {packageGaps.map((g, i) => (
                    <tr
                      key={g.GapId}
                      className={`border-t border-gray-100 ${i % 2 === 1 ? "bg-gray-50/50" : ""}`}
                    >
                      <td className="px-3 py-2 align-top">
                        <GapBadge tier={g.Tier} />
                      </td>
                      <td className="px-3 py-2 align-top text-xs">{g.Kind}</td>
                      <td className="px-3 py-2 align-top font-mono text-xs">{g.Location}</td>
                      <td className="px-3 py-2 align-top text-xs text-gray-600">{g.Reason}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}

      {data.tier1Decisions.length > 0 && (
        <>
          <h3 className="mb-2 mt-8 text-base font-semibold">Tier-1 Decisions</h3>
          <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
            <table className="w-full border-collapse text-sm">
              <thead>
                <tr className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                  <th className="px-3 py-2">Package</th>
                  <th className="px-3 py-2">Status</th>
                  <th className="px-3 py-2">Gap ID</th>
                  <th className="px-3 py-2">Detail</th>
                </tr>
              </thead>
              <tbody>
                {data.tier1Decisions.map((d, i) => (
                  <tr
                    key={`${d.package}-${d.gapId}`}
                    className={`border-t border-gray-100 ${i % 2 === 1 ? "bg-gray-50/50" : ""}`}
                  >
                    <td className="px-3 py-2">{d.package}</td>
                    <td className="px-3 py-2 text-xs">{d.status}</td>
                    <td className="px-3 py-2 font-mono text-xs">{d.gapId}</td>
                    <td className="px-3 py-2 text-xs text-gray-600">{d.detail}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
