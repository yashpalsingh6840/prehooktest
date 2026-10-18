import { Fragment, useState } from "react";
import { Link, useLocation, useNavigate, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { getGaps, getGapPacket, type GapSpec, type Workspace } from "../api/client";
import GapBadge from "../components/GapBadge";
import TabNav from "../components/TabNav";
import MarkdownView from "../components/MarkdownView";
import FillEditor, { gapHasFillMechanism } from "../components/FillEditor";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";
import EmptyState from "../components/EmptyState";
import PageInfo from "../components/PageInfo";

type Filter = "all" | "blocking" | "advisory";

const FILTER_KEYS = new Set<string>(["all", "blocking", "advisory"]);

function GapsPageInfo() {
  return (
    <PageInfo
      summary="Every gap the last Generate run couldn't resolve on its own -- each one is reported rather than guessed at, so nothing in the generated code is silently wrong."
      details={
        <>
          <p>
            Gaps are grouped by package, then by <strong>tier</strong> -- a taxonomy of who can
            close it. <strong>MissingDatum</strong> (Tier 1) is a single fact an AI can propose
            and a human confirms (a Lookup's join key, an encrypted secret acknowledgment).{" "}
            <strong>MissingLogic</strong> (Tier 2) means the real logic IS in the package (a
            Script Task/Component), just not mechanically translatable -- it becomes a
            compile-time seam an AI ports and a human reviews. <strong>MissingToolSupport</strong>{" "}
            (Tier 3) means ssisx can't model the shape at all; it's a feature request for the
            tool itself, never something to paper over per-package.
          </p>
          <p>
            A gap with a fill mechanism shows an "edit" affordance opening a form (Tier 1) or
            a code editor (Tier 2) -- saving writes into the workspace's Fills Path, and the
            matching Pipeline step (Generate for a decision, Apply Fills for a code seam) has to
            be re-run to actually pick it up.
          </p>
        </>
      }
    />
  );
}

export default function GapsExplorer({ workspace }: { workspace: Workspace }) {
  const navigate = useNavigate();
  const location = useLocation();
  const { filter: rawFilter } = useParams<{ filter?: string }>();
  const filter: Filter = FILTER_KEYS.has(rawFilter ?? "") ? (rawFilter as Filter) : "all";

  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "gaps"],
    queryFn: () => getGaps(workspace.id),
  });

  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [packetFor, setPacketFor] = useState<GapSpec | null>(null);
  const [fillFor, setFillFor] = useState<GapSpec | null>(null);
  // Local UI state, not URL-driven like `filter` -- the package list is only
  // known once `data` loads, and there's no need to make this bookmarkable.
  // Pre-seeded from navigation state when a caller (e.g. the Generate
  // Report's "Open in Gaps Explorer" button) links here for one package.
  const [packageFilter, setPackageFilter] = useState<string>(
    () => (location.state as { package?: string } | null)?.package ?? "all",
  );

  const packetQuery = useQuery({
    queryKey: ["artifacts", workspace.id, "packet", packetFor?.GapId],
    queryFn: () => getGapPacket(workspace.id, packetFor!.GapId),
    enabled: !!packetFor,
  });

  if (isLoading) return <LoadingState />;
  if (error) return <ErrorState error={error} />;
  if (!data || !data.available) {
    return (
      <div>
        <EmptyState message="Nothing here yet -- run Generate first." />
      </div>
    );
  }

  const allPackages = [...new Set(data.gaps.map((g) => g.Package))].sort((a, b) =>
    a.localeCompare(b),
  );

  const filtered = data.gaps.filter((g) => {
    if (packageFilter !== "all" && g.Package !== packageFilter) return false;
    if (filter === "blocking") return g.IsBlocking;
    if (filter === "advisory") return !g.IsBlocking;
    return true;
  });

  // Package -> Tier -> Kind -> gaps
  const grouped = new Map<string, Map<string, Map<string, GapSpec[]>>>();
  for (const gap of filtered) {
    if (!grouped.has(gap.Package)) grouped.set(gap.Package, new Map());
    const byTier = grouped.get(gap.Package)!;
    if (!byTier.has(gap.Tier)) byTier.set(gap.Tier, new Map());
    const byKind = byTier.get(gap.Tier)!;
    if (!byKind.has(gap.Kind)) byKind.set(gap.Kind, []);
    byKind.get(gap.Kind)!.push(gap);
  }

  function toggle(key: string) {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  function selectFilter(f: Filter) {
    navigate(`/workspaces/${workspace.id}/gaps${f === "all" ? "" : "/" + f}`);
  }

  const toolbar = (
    <Toolbar
      filtered={filtered.length}
      total={data.gaps.length}
      filter={filter}
      onSelect={selectFilter}
      packages={allPackages}
      packageFilter={packageFilter}
      onPackageChange={setPackageFilter}
    />
  );

  if (filtered.length === 0) {
    return (
      <div>
        <GapsPageInfo />
        {toolbar}
        <EmptyState message="No gaps match this filter." />
      </div>
    );
  }

  return (
    <div>
      <h2 className="mb-1 text-base font-semibold">Gaps</h2>
      <GapsPageInfo />
      {toolbar}

      {[...grouped.entries()].map(([pkg, byTier]) => (
        <div key={pkg} className="mb-6">
          <h3 className="mb-2 text-base font-semibold">{pkg}</h3>
          {[...byTier.entries()].map(([tier, byKind]) => (
            <div key={tier} className="mb-3 pl-2">
              {[...byKind.entries()].map(([kind, gaps]) => (
                <div key={kind} className="mb-2 overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
                  <div className="flex items-center gap-2 border-b border-gray-200 bg-gray-50 px-3 py-1.5 text-sm text-gray-600">
                    <GapBadge tier={gaps[0].Tier as GapSpec["Tier"]} />
                    <strong className="text-gray-800">{kind}</strong>
                    <span>({gaps.length})</span>
                  </div>
                  <table className="w-full border-collapse text-sm">
                    <tbody>
                      {gaps.map((gap, i) => {
                        const isOpen = expanded.has(gap.GapId);
                        return (
                          <Fragment key={gap.GapId}>
                            <tr
                              className={`cursor-pointer border-t border-gray-100 hover:bg-gray-50 ${
                                i % 2 === 1 ? "bg-gray-50/50" : ""
                              }`}
                              onClick={() => toggle(gap.GapId)}
                            >
                              <td className="w-6 px-2 py-1.5">
                                <button
                                  className="border-none bg-transparent text-gray-500"
                                  aria-label={isOpen ? "Collapse" : "Expand"}
                                >
                                  {isOpen ? "▾" : "▸"}
                                </button>
                              </td>
                              <td className="px-2 py-1.5 font-mono text-xs">{gap.Location}</td>
                              <td
                                className={`px-2 py-1.5 text-xs ${
                                  gap.IsBlocking ? "font-semibold text-red-700" : "text-gray-500"
                                }`}
                              >
                                {gap.IsBlocking ? "blocking" : "advisory"}
                              </td>
                            </tr>
                            {isOpen && (
                              <tr>
                                <td colSpan={3} className="bg-gray-50 px-4 py-3 pl-8">
                                  <p className="m-0 mb-2 whitespace-pre-wrap text-sm">{gap.Reason}</p>
                                  <div className="flex gap-2">
                                    <Link
                                      to={`/workspaces/${workspace.id}/extracted/${encodeURIComponent(gap.Package)}`}
                                      className="rounded border border-gray-300 bg-white px-3 py-1 text-xs hover:bg-gray-50"
                                      onClick={(e) => e.stopPropagation()}
                                    >
                                      View package JSON
                                    </Link>
                                    {gap.PacketPath && (
                                      <button
                                        className="rounded border border-gray-300 bg-white px-3 py-1 text-xs hover:bg-gray-50"
                                        onClick={(e) => {
                                          e.stopPropagation();
                                          setFillFor(null);
                                          setPacketFor(gap);
                                        }}
                                      >
                                        View work packet
                                      </button>
                                    )}
                                    {gapHasFillMechanism(gap.Kind) ? (
                                      <button
                                        className="rounded bg-blue-600 px-3 py-1 text-xs font-medium text-white hover:bg-blue-700"
                                        onClick={(e) => {
                                          e.stopPropagation();
                                          setPacketFor(null);
                                          setFillFor(gap);
                                        }}
                                      >
                                        Fill this gap
                                      </button>
                                    ) : (
                                      <span className="text-xs italic text-gray-400">
                                        Tool limitation -- no fill mechanism
                                      </span>
                                    )}
                                  </div>
                                </td>
                              </tr>
                            )}
                          </Fragment>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              ))}
            </div>
          ))}
        </div>
      ))}

      {packetFor && (
        <div className="fixed bottom-0 right-0 top-0 w-[min(640px,90vw)] overflow-y-auto border-l border-gray-200 bg-white p-6 shadow-2xl">
          <div className="mb-4 flex justify-between">
            <strong>{packetFor.GapId}</strong>
            <button
              className="rounded border border-gray-300 px-2 py-0.5 text-xs hover:bg-gray-50"
              onClick={() => setPacketFor(null)}
            >
              Close
            </button>
          </div>
          {packetQuery.isLoading && <LoadingState />}
          {packetQuery.error && <ErrorState error={packetQuery.error} />}
          {packetQuery.data && <MarkdownView markdown={packetQuery.data.markdown} />}
        </div>
      )}

      {fillFor && (
        <div className="fixed bottom-0 right-0 top-0 w-[min(760px,95vw)] overflow-y-auto border-l border-gray-200 bg-white shadow-2xl">
          <FillEditor workspace={workspace} gap={fillFor} onClose={() => setFillFor(null)} />
        </div>
      )}
    </div>
  );
}

function Toolbar({
  filtered,
  total,
  filter,
  onSelect,
  packages,
  packageFilter,
  onPackageChange,
}: {
  filtered: number;
  total: number;
  filter: Filter;
  onSelect: (f: Filter) => void;
  packages: string[];
  packageFilter: string;
  onPackageChange: (pkg: string) => void;
}) {
  return (
    <>
      <TabNav tabs={FILTER_TABS} active={filter} onSelect={onSelect} />
      <div className="mb-4 flex flex-wrap items-center gap-2">
        <span className="text-sm text-gray-500">
          {filtered} of {total} gap(s)
        </span>
        <label className="ml-2 flex items-center gap-1.5 text-sm text-gray-600">
          Package
          <select
            value={packageFilter}
            onChange={(e) => onPackageChange(e.target.value)}
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
      </div>
    </>
  );
}

const FILTER_TABS: { key: Filter; label: string }[] = [
  { key: "all", label: "All" },
  { key: "blocking", label: "Blocking" },
  { key: "advisory", label: "Advisory" },
];
