import type { Workspace } from "../api/client";
import PortfolioCharts from "../components/PortfolioCharts";
import PageInfo from "../components/PageInfo";

/** The portfolio-wide summary/graphs view, split out of what used to be one
 * combined "Dashboard" page -- the pipeline run cards + live log now live on
 * their own "Pipeline" page (the workspace's index route) instead. */
export default function DashboardSummary({ workspace }: { workspace: Workspace }) {
  return (
    <div>
      <h2 className="mb-1 text-base font-semibold">Dashboard</h2>
      <PageInfo
        summary="A portfolio-wide rollup computed live from Extract's package specs and Generate's gaps.json -- re-run either step and these numbers update."
        details={
          <>
            <p>
              <strong>Packages</strong> counts every package Extract found. <strong>Gap-free</strong>,{" "}
              <strong>Total Gaps</strong>, <strong>Blocking</strong>, and <strong>Advisory</strong>{" "}
              summarize Generate's own per-package gap list -- a blocking gap stops that package
              from being fully generatable; an advisory one is informational and doesn't stop it.
            </p>
            <p>
              "Gaps by Kind" breaks the same list down by the gap's taxonomy (e.g. a Script
              Component column someone needs to port vs. an unsupported component type this tool
              doesn't model yet) -- see a gap's own entry on the Gaps page for the full
              Tier-1/2/3 explanation. "Components by Role" and "Top Packages by Gap Count" are
              both computed client-side from the same underlying data, not separate CLI output.
            </p>
          </>
        }
      />
      <PortfolioCharts workspace={workspace} />
    </div>
  );
}
