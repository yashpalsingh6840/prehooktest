import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { getPortfolioStats, type Workspace } from "../api/client";
import BarChart, { type BarDatum } from "./BarChart";
import { CATEGORICAL, STATUS } from "../utils/chartPalette";
import CountChip from "./CountChip";
import LoadingState from "./LoadingState";
import ErrorState from "./ErrorState";
import EmptyState from "./EmptyState";

const TIER_ORDER = ["Advisory", "MissingDatum", "MissingLogic", "MissingToolSupport"] as const;
const TIER_COLOR: Record<(typeof TIER_ORDER)[number], string> = {
  Advisory: STATUS.good,
  MissingDatum: STATUS.warning,
  MissingLogic: STATUS.serious,
  MissingToolSupport: STATUS.critical,
};

// Fixed GapKind declaration order (see api/client.ts) -- stable across
// renders and 1:1 with the 8-slot categorical palette, never cycled.
const KIND_ORDER = [
  "Unclassified",
  "ScriptTask",
  "ScriptComponentColumn",
  "LookupJoinKey",
  "ConditionalConstraint",
  "EncryptedConnectionManagerSecret",
  "TestOracle",
  "LocalFileSourceData",
] as const;

const ROLE_ORDER = ["Source", "Transform", "Destination", "Other"] as const;

const TOP_PACKAGES_LIMIT = 10;

/** Only the categories actually present get a bar -- an all-zero category
 * from a fixed order list would otherwise draw an empty, mislabeled bar. */
function toOrderedBars(
  counts: Record<string, number>,
  order: readonly string[],
  colorFor: (key: string, index: number) => string | undefined,
): BarDatum[] {
  return order
    .filter((key) => (counts[key] ?? 0) > 0)
    .map((key, i) => ({ label: key, value: counts[key], color: colorFor(key, i) }));
}

/** Portfolio-wide stat cards + charts, computed live from gaps.json and
 * every extracted package spec -- shown on the Dashboard above the pipeline
 * run cards. Deliberately separate from PackageComponents.tsx's own
 * per-package chart, which this doesn't replace. */
export default function PortfolioCharts({ workspace }: { workspace: Workspace }) {
  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "portfolio-stats"],
    queryFn: () => getPortfolioStats(workspace.id),
  });

  const topPackages = useMemo<BarDatum[]>(() => {
    if (!data) return [];
    return Object.entries(data.gapsByPackage)
      .sort((a, b) => b[1] - a[1])
      .slice(0, TOP_PACKAGES_LIMIT)
      .map(([label, value]) => ({ label, value }));
  }, [data]);

  if (isLoading) return <LoadingState label="Loading portfolio stats..." />;
  if (error) return <ErrorState error={error} />;
  if (!data || (data.packageCount === 0 && data.totalGaps === 0)) {
    return (
      <EmptyState message="Run Extract (and Generate) to see portfolio charts here." />
    );
  }

  const hasRoleCounts = Object.keys(data.roleCounts).length > 0;
  const packagesWithGaps = Object.keys(data.gapsByPackage).length;
  const packagesGapFree = Math.max(0, data.packageCount - packagesWithGaps);
  const avgGapsPerPackage =
    data.packageCount > 0 ? Math.round((data.totalGaps / data.packageCount) * 10) / 10 : 0;

  const gapsByTierBars = toOrderedBars(data.gapsByTier, TIER_ORDER, (key) => TIER_COLOR[key as (typeof TIER_ORDER)[number]]);
  const gapsByKindBars = toOrderedBars(data.gapsByKind, KIND_ORDER, (_key, i) => CATEGORICAL[i]);
  const roleBars = toOrderedBars(data.roleCounts, ROLE_ORDER, (_key, i) => CATEGORICAL[i]);

  return (
    <div>
      <div className="mb-4 flex flex-wrap gap-2">
        <CountChip
          label="Packages"
          value={data.packageCount}
          color="gray"
          hint="Number of packages extracted into this workspace."
        />
        <CountChip
          label="Gap-free"
          value={packagesGapFree}
          color="green"
          hint="Packages with no reported gaps at all, blocking or advisory."
        />
        <CountChip
          label="Total gaps"
          value={data.totalGaps}
          color="amber"
          hint="Every gap reported across the whole portfolio, blocking and advisory combined."
        />
        <CountChip
          label="Blocking"
          value={data.blockingGaps}
          color="red"
          hint="Gaps that prevent a package from being fully generatable."
        />
        <CountChip
          label="Advisory"
          value={data.advisoryGaps}
          color="blue"
          hint="Non-blocking gaps -- informational, don't stop generation."
        />
        <CountChip
          label="Avg gaps / pkg"
          value={avgGapsPerPackage}
          color="gray"
          hint="Total gaps divided by package count -- a rough portfolio-wide density figure."
        />
      </div>

      {(data.totalGaps > 0 || hasRoleCounts) && (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          {gapsByTierBars.length > 0 && (
            <div>
              <h4 className="mb-1 text-sm font-semibold text-gray-700">Gaps by Tier</h4>
              <BarChart data={gapsByTierBars} />
            </div>
          )}
          {gapsByKindBars.length > 0 && (
            <div>
              <h4 className="mb-1 text-sm font-semibold text-gray-700">Gaps by Kind</h4>
              <BarChart data={gapsByKindBars} />
            </div>
          )}
          {hasRoleCounts && (
            <div>
              <h4 className="mb-1 text-sm font-semibold text-gray-700">
                Components by Role (all packages)
              </h4>
              <BarChart data={roleBars} />
            </div>
          )}
          {topPackages.length > 0 && (
            <div>
              <h4 className="mb-1 text-sm font-semibold text-gray-700">
                Top Packages by Gap Count
                {packagesWithGaps > TOP_PACKAGES_LIMIT
                  ? ` (top ${TOP_PACKAGES_LIMIT} of ${packagesWithGaps})`
                  : ""}
              </h4>
              <BarChart data={topPackages} />
            </div>
          )}
        </div>
      )}
    </div>
  );
}
