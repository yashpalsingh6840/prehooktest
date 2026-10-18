import { useEffect } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { getExtractedPackages, type Workspace } from "../api/client";
import SampleDataBrowser from "./SampleDataBrowser";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";
import EmptyState from "../components/EmptyState";

/** A standalone "pick a package, see its sample data" page -- the same view
 * as Walkthrough's own Sample Data tab (/walkthrough/:pkg/sampledata), just
 * reachable directly from the sidebar without going through Walkthrough
 * first. Uses the extracted-packages list (not Walkthrough's own package
 * list) since Sample Data only needs Extract to have run, not Walkthrough. */
export default function SampleDataPage({ workspace }: { workspace: Workspace }) {
  const navigate = useNavigate();
  const { pkg: rawPkg } = useParams<{ pkg?: string }>();
  const decodedPkg = rawPkg ? decodeURIComponent(rawPkg) : undefined;

  const packagesQuery = useQuery({
    queryKey: ["artifacts", workspace.id, "extracted-packages"],
    queryFn: () => getExtractedPackages(workspace.id),
  });

  const hasValidPkg = !!packagesQuery.data?.packages.includes(decodedPkg ?? "");

  // Same "always land on a real, bookmarkable selection" pattern as
  // ExtractedData.tsx/Walkthrough.tsx.
  useEffect(() => {
    if (packagesQuery.data?.packages.length && !hasValidPkg) {
      navigate(
        `/workspaces/${workspace.id}/sampledata/${encodeURIComponent(packagesQuery.data.packages[0])}`,
        { replace: true },
      );
    }
  }, [packagesQuery.data, hasValidPkg, navigate, workspace.id]);

  if (packagesQuery.isLoading) return <LoadingState />;
  if (packagesQuery.error) return <ErrorState error={packagesQuery.error} />;
  if (!packagesQuery.data || packagesQuery.data.packages.length === 0) {
    return (
      <div>
        <EmptyState message="Nothing here yet -- run Extract first." />
      </div>
    );
  }

  const active = hasValidPkg ? decodedPkg! : packagesQuery.data.packages[0];

  return (
    <div>
      <h2 className="mb-1 text-base font-semibold">Sample Data</h2>
      <div className="mb-4 flex items-center gap-2">
        <label htmlFor="sampledata-package" className="text-sm font-medium text-gray-700">
          Package
        </label>
        <select
          id="sampledata-package"
          value={active}
          onChange={(e) =>
            navigate(`/workspaces/${workspace.id}/sampledata/${encodeURIComponent(e.target.value)}`)
          }
          className="rounded border border-gray-300 bg-white px-3 py-1.5 text-sm shadow-sm focus:border-blue-500 focus:outline-none"
        >
          {packagesQuery.data.packages.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </select>
      </div>

      <SampleDataBrowser workspace={workspace} packageName={active} />
    </div>
  );
}
