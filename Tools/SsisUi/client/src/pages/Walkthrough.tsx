import { useEffect } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  getWalkthrough,
  type Workspace,
} from "../api/client";
import MarkdownView from "../components/MarkdownView";
import { ClaimsPanel } from "../components/ClaimsPanel";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";
import EmptyState from "../components/EmptyState";
import PageInfo from "../components/PageInfo";
import TabNav from "../components/TabNav";
import PackageComponents from "./PackageComponents";
import SampleDataBrowser from "./SampleDataBrowser";

type Tab = "report" | "components" | "sampledata";
const TAB_KEYS = new Set<string>(["report", "components", "sampledata"]);
const TABS: { key: Tab; label: string }[] = [
  { key: "report", label: "Report" },
  { key: "components", label: "Components" },
  { key: "sampledata", label: "Sample Data" },
];


export default function Walkthrough({ workspace }: { workspace: Workspace }) {
  const navigate = useNavigate();
  const { pkg, tab: rawTab } = useParams<{ pkg?: string; tab?: string }>();
  const tab: Tab = TAB_KEYS.has(rawTab ?? "") ? (rawTab as Tab) : "report";

  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "walkthrough"],
    queryFn: () => getWalkthrough(workspace.id),
  });

  const decodedPkg = pkg ? decodeURIComponent(pkg) : undefined;
  const hasValidPkg = !!data?.packages.some((p) => p.package === decodedPkg);

  // Always land on a real, bookmarkable selection -- redirect to the first
  // package whenever the URL names none, or names one that doesn't exist.
  useEffect(() => {
    if (data?.packages.length && !hasValidPkg) {
      navigate(
        `/workspaces/${workspace.id}/walkthrough/${encodeURIComponent(data.packages[0].package)}`,
        { replace: true },
      );
    }
  }, [data, hasValidPkg, navigate, workspace.id]);

  if (isLoading) return <LoadingState />;
  if (error) return <ErrorState error={error} />;
  if (!data || !data.available) {
    return (
      <div>
        <EmptyState message="Nothing here yet -- run the Walkthrough step first." />
      </div>
    );
  }

  const active = data.packages.find((p) => p.package === decodedPkg) ?? data.packages[0];

  return (
    <div>
      <h2 className="mb-1 text-base font-semibold">Package Explorer</h2>
      {data.packages.length > 1 && (
        <div className="mb-4 flex items-center gap-2">
          <label htmlFor="walkthrough-package" className="text-sm font-medium text-gray-700">
            Package
          </label>
          <select
            id="walkthrough-package"
            value={active.package}
            onChange={(e) =>
              navigate(
                `/workspaces/${workspace.id}/walkthrough/${encodeURIComponent(e.target.value)}/${tab}`,
              )
            }
            className="rounded border border-gray-300 bg-white px-3 py-1.5 text-sm shadow-sm focus:border-blue-500 focus:outline-none"
          >
            {data.packages.map((p) => (
              <option key={p.package} value={p.package}>
                {p.package}
              </option>
            ))}
          </select>
          <span className="text-xs text-gray-400">
            ({data.packages.length} package{data.packages.length === 1 ? "" : "s"})
          </span>
        </div>
      )}

      <TabNav
        tabs={TABS}
        active={tab}
        hrefFor={(key) =>
          `/workspaces/${workspace.id}/walkthrough/${encodeURIComponent(active.package)}/${key}`
        }
      />

      {tab === "report" && (
        <>
          <PageInfo
            summary="A human-readable, component-by-component narrative of this package's data flow, generated deterministically by svk walkthrough -- not AI-written."
            details={
              <p>
                Built from the same extracted spec as every other page here; each component gets
                the same sequence number it gets in the generated C# (see the Components tab),
                so this doc and the generated code can be read side by side. <strong>Rule Reviews</strong>{" "}
                below is a separate, human-owned file (<code className="rounded bg-gray-200 px-1 py-0.5">&lt;Package&gt;.claims.json</code>) --
                svk writes a stub once and never touches it again, so marking a rule Validated/
                Blocked here is a durable decision, not something a re-run will overwrite.
              </p>
            }
          />
          <div className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
            <MarkdownView markdown={active.markdown} />
          </div>

          <h3 className="mb-2 mt-8 text-base font-semibold">Rule Reviews</h3>
          <ClaimsPanel workspace={workspace} packageName={active.package} />
        </>
      )}

      {tab === "components" && (
        <PackageComponents workspace={workspace} packageName={active.package} />
      )}

      {tab === "sampledata" && (
        <SampleDataBrowser workspace={workspace} packageName={active.package} />
      )}
    </div>
  );
}
