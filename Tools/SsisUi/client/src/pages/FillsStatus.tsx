import { useCallback, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { getFillsStatus, type FillStatus, type Workspace } from "../api/client";
import Badge, { type BadgeColor } from "../components/Badge";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";
import EmptyState from "../components/EmptyState";
import PageInfo from "../components/PageInfo";
import ApplyFillsModal from "../components/ApplyFillsModal";
import OutputFilesPanel from "../components/OutputFilesPanel";

const STATUS_COLORS: Record<FillStatus, BadgeColor> = {
  Applied: "green",
  Stale: "amber",
  Orphaned: "red",
  Unattributed: "gray",
};

function StatusBadge({ status }: { status: FillStatus }) {
  return <Badge color={STATUS_COLORS[status]}>{status}</Badge>;
}

function FillsPageInfo() {
  return (
    <PageInfo
      summary="What the last Apply Fills run found under the workspace's Fills Path, and whether each fill was actually usable."
      details={
        <>
          <p>
            Apply Fills copies a reviewed seam fill (ported Script Task/Component logic, a
            TEST-ORACLE test, a LOCAL-DATA sample file) into the generated project, but only when
            it's trustworthy: <strong>Applied</strong> means its recorded evidence hash still
            matches the gap it answers. <strong>Stale</strong> means that gap's underlying
            evidence changed since the fill was written (the package was re-extracted) -- it's
            refused, not silently applied anyway. <strong>Orphaned</strong> means it answers a
            gap that no longer exists. <strong>Unattributed</strong> means it has no{" "}
            <code className="rounded bg-gray-200 px-1 py-0.5">// ssisx-fill:</code> provenance
            comment at all -- still applied, just with no author/date/hash on record.
          </p>
          <p>
            A Tier-1 decision (<code className="rounded bg-gray-200 px-1 py-0.5">decisions.json</code>)
            doesn't appear here at all -- that's consumed by Generate itself, not Apply Fills.
          </p>
        </>
      }
    />
  );
}

export default function FillsStatus({ workspace }: { workspace: Workspace }) {
  const [showApplyFills, setShowApplyFills] = useState(false);
  const [pkg, setPkg] = useState("all");
  const closeApplyFills = useCallback(() => setShowApplyFills(false), []);

  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "fills-status"],
    queryFn: () => getFillsStatus(workspace.id),
  });

  const packages = [...new Set((data?.fills ?? []).map((f) => f.Package))].sort((a, b) =>
    a.localeCompare(b),
  );
  const visibleFills = (data?.fills ?? []).filter((f) => pkg === "all" || f.Package === pkg);

  const toolbar = (
    <div className="mb-4 flex items-center gap-3">
      {packages.length > 0 && (
        <label className="flex items-center gap-1.5 text-sm text-gray-600">
          Package
          <select
            value={pkg}
            onChange={(e) => setPkg(e.target.value)}
            className="rounded border border-gray-300 bg-white px-2 py-1 text-sm shadow-sm focus:border-blue-500 focus:outline-none"
          >
            <option value="all">All packages ({packages.length})</option>
            {packages.map((p) => (
              <option key={p} value={p}>
                {p}
              </option>
            ))}
          </select>
        </label>
      )}
      <button
        className="ml-auto cursor-pointer rounded bg-emerald-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-emerald-700"
        onClick={() => setShowApplyFills(true)}
      >
        Re-run apply-fills
      </button>
      {showApplyFills && (
        <ApplyFillsModal workspace={workspace} onClose={closeApplyFills} />
      )}
    </div>
  );

  if (isLoading) return <LoadingState />;
  if (error) return <ErrorState error={error} />;
  if (!data || !data.available) {
    return (
      <div>
        <FillsPageInfo />
        {toolbar}
        <EmptyState message="Nothing here yet -- run Apply Fills first." />
        <OutputFilesPanel workspace={workspace} />
      </div>
    );
  }

  return (
    <div>
      <h2 className="mb-1 text-base font-semibold">Applied Fills</h2>
      <FillsPageInfo />
      {toolbar}
      {visibleFills.length === 0 ? (
        <EmptyState message={data.fills.length === 0 ? "No fills recorded." : "No fills for this package."} />
      ) : (
        <div className="overflow-x-auto rounded-lg border border-gray-200 bg-white shadow-sm">
          <table className="w-full border-collapse text-sm">
            <thead>
              <tr className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                <th className="px-3 py-2">Package</th>
                <th className="px-3 py-2">File</th>
                <th className="px-3 py-2">Seam</th>
                <th className="px-3 py-2">Gap</th>
                <th className="px-3 py-2">Status</th>
                <th className="px-3 py-2">Author</th>
                <th className="px-3 py-2">Date</th>
              </tr>
            </thead>
            <tbody>
              {visibleFills.map((f, i) => (
                <tr
                  key={`${f.Package}-${f.FileName}-${f.Seam}-${i}`}
                  className={`border-t border-gray-100 hover:bg-gray-50 ${i % 2 === 1 ? "bg-gray-50/50" : ""}`}
                >
                  <td className="px-3 py-2">{f.Package}</td>
                  <td className="px-3 py-2 font-mono text-xs">{f.FileName}</td>
                  <td className="px-3 py-2 font-mono text-xs">{f.Seam}</td>
                  <td className="px-3 py-2 font-mono text-xs">{f.GapId ?? "—"}</td>
                  <td className="px-3 py-2">
                    <StatusBadge status={f.Status} />
                  </td>
                  <td className="px-3 py-2">{f.Author ?? "—"}</td>
                  <td className="px-3 py-2">{f.Date ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <OutputFilesPanel workspace={workspace} />
    </div>
  );
}
