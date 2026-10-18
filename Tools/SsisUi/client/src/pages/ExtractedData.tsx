import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  getExtractedPackages,
  getRawPackageSpec,
  getWalkthrough,
  type Workspace,
} from "../api/client";
import WalkthroughDrawer from "../components/WalkthroughDrawer";
import CodeBlock from "../components/CodeBlock";
import JsonTree from "../components/JsonTree";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";
import EmptyState from "../components/EmptyState";
import PageInfo from "../components/PageInfo";
import PackageSummaryCard from "../components/PackageSummaryCard";

type View = "pretty" | "raw";

export default function ExtractedData({ workspace }: { workspace: Workspace }) {
  const navigate = useNavigate();
  const { pkg: rawPkg } = useParams<{ pkg?: string }>();
  const decodedPkg = rawPkg ? decodeURIComponent(rawPkg) : undefined;
  const [view, setView] = useState<View>("pretty");
  const [drawerOpen, setDrawerOpen] = useState(false);

  const packagesQuery = useQuery({
    queryKey: ["artifacts", workspace.id, "extracted-packages"],
    queryFn: () => getExtractedPackages(workspace.id),
  });

  const hasValidPkg = !!packagesQuery.data?.packages.includes(decodedPkg ?? "");

  // Same "always land on a real, bookmarkable selection" pattern as
  // Walkthrough.tsx -- redirect to the first package when the URL names
  // none, or names one that no longer has an extracted spec.
  useEffect(() => {
    if (packagesQuery.data?.packages.length && !hasValidPkg) {
      navigate(
        `/workspaces/${workspace.id}/extracted/${encodeURIComponent(packagesQuery.data.packages[0])}`,
        { replace: true },
      );
    }
  }, [packagesQuery.data, hasValidPkg, navigate, workspace.id]);

  const active = hasValidPkg ? decodedPkg! : packagesQuery.data?.packages[0];

  const specQuery = useQuery({
    queryKey: ["artifacts", workspace.id, "raw-spec", active],
    queryFn: () => getRawPackageSpec(workspace.id, active!),
    enabled: !!active,
  });

  // Same query key the Package Explorer page uses, so this is usually a cache hit.
  const walkthroughQuery = useQuery({
    queryKey: ["artifacts", workspace.id, "walkthrough"],
    queryFn: () => getWalkthrough(workspace.id),
  });
  const hasWalkthrough =
    !!walkthroughQuery.data?.available &&
    !!walkthroughQuery.data.packages.some((p) => p.package === active);

  if (packagesQuery.isLoading) return <LoadingState />;
  if (packagesQuery.error) return <ErrorState error={packagesQuery.error} />;
  if (!packagesQuery.data || packagesQuery.data.packages.length === 0) {
    return (
      <div>
        <EmptyState message="Nothing here yet -- run Extract first." />
      </div>
    );
  }

  return (
    <div>
      <h2 className="mb-1 text-base font-semibold">Extracted Data</h2>
      <PageInfo
        summary="The raw spec.json ssisx extract wrote for this package -- a deterministic, redacted, lossless description of its control flow and every pipeline component."
        details={
          <>
            <p>
              Written directly from the package's own XML (never the SSIS object model, so it
              needs no Windows/GAC/SSISDB access) -- extraction is deterministic: running it
              again against an unchanged package produces byte-identical output. Any connection
              string or credential is redacted; this file is safe to commit or share.
            </p>
            <p>
              <strong>Pretty</strong> is a collapsible tree view of the same JSON; <strong>Raw JSON</strong>{" "}
              is the literal file content, useful for copy-pasting a path to grep for or diffing
              between two runs.
            </p>
          </>
        }
      />
      <div className="mb-4 flex flex-wrap items-center gap-2">
        <label htmlFor="extracted-package" className="text-sm font-medium text-gray-700">
          Package
        </label>
        <select
          id="extracted-package"
          value={active}
          onChange={(e) =>
            navigate(`/workspaces/${workspace.id}/extracted/${encodeURIComponent(e.target.value)}`)
          }
          className="rounded border border-gray-300 bg-white px-3 py-1.5 text-sm shadow-sm focus:border-blue-500 focus:outline-none"
        >
          {packagesQuery.data.packages.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </select>

        {hasWalkthrough && (
          <button
            type="button"
            onClick={() => setDrawerOpen(true)}
            className="cursor-pointer rounded border border-blue-300 bg-blue-50 px-3 py-1.5 text-sm font-medium text-blue-700 shadow-sm hover:bg-blue-100"
          >
            Package Explorer
          </button>
        )}

      </div>

      {drawerOpen && active && (
        <WalkthroughDrawer workspace={workspace} packageName={active} onClose={() => setDrawerOpen(false)} />
      )}

      {specQuery.isLoading && <LoadingState label="Loading extracted spec..." />}
      {specQuery.error && <ErrorState error={specQuery.error} />}
      {specQuery.data && (
        <>
          <PackageSummaryCard json={specQuery.data.json} />
          <div className="mb-3 flex items-start justify-between gap-3">
            <p className="min-w-0 break-all font-mono text-xs text-gray-400">{specQuery.data.path}</p>
            <div className="flex shrink-0 gap-1 rounded border border-gray-300 bg-white p-0.5">
              {(["pretty", "raw"] as View[]).map((v) => (
                <button
                  key={v}
                  onClick={() => setView(v)}
                  className={`rounded px-2.5 py-1 text-xs font-medium capitalize ${
                    view === v ? "bg-blue-600 text-white" : "text-gray-600 hover:bg-gray-50"
                  }`}
                >
                  {v === "pretty" ? "Pretty" : "Raw JSON"}
                </button>
              ))}
            </div>
          </div>
          {view === "pretty" ? (
            <JsonTree data={specQuery.data.json} />
          ) : (
            <CodeBlock language="json">{JSON.stringify(specQuery.data.json, null, 2)}</CodeBlock>
          )}
        </>
      )}
    </div>
  );
}
