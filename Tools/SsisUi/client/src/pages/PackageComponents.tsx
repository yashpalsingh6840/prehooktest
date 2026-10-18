import { useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  getPackageSummary,
  type ComponentRole,
  type PackageComponent,
  type Workspace,
} from "../api/client";
import MermaidDiagram from "../components/MermaidDiagram";
import CodeBlock from "../components/CodeBlock";
import Badge, { type BadgeColor } from "../components/Badge";
import CountChip from "../components/CountChip";
import BarChart, { type BarDatum } from "../components/BarChart";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";
import EmptyState from "../components/EmptyState";
import PageInfo from "../components/PageInfo";
import { buildDataFlowChart } from "../utils/mermaidBuilders";
import { CATEGORICAL } from "../utils/chartPalette";

const ROLE_ORDER = ["Source", "Transform", "Destination", "Other"] as const;

const ROLE_COLORS: Record<ComponentRole, BadgeColor> = {
  Source: "blue",
  Transform: "purple",
  Destination: "green",
  Other: "gray",
};

export default function PackageComponents({
  workspace,
  packageName,
  showExtractedLink = true,
}: {
  workspace: Workspace;
  packageName: string;
  /** Hidden where the page itself is already the extracted view (the drawer). */
  showExtractedLink?: boolean;
}) {
  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "package-summary", packageName],
    queryFn: () => getPackageSummary(workspace.id, packageName),
  });

  const [selected, setSelected] = useState<PackageComponent | null>(null);

  if (isLoading) return <LoadingState label="Loading component inventory..." />;
  if (error) return <ErrorState error={error} />;
  if (!data) {
    return <EmptyState message="No extracted spec found for this package -- run Extract first." />;
  }

  const totalComponents = data.dataFlows.reduce((n, f) => n + f.components.length, 0);

  const roleBars: BarDatum[] = ROLE_ORDER.filter((r) => (data.roleCounts[r] ?? 0) > 0).map(
    (r, i) => ({ label: r, value: data.roleCounts[r], color: CATEGORICAL[i] }),
  );

  return (
    <div>
      <PageInfo
        summary="Every pipeline component ssisx extract found in this package's Data Flow Tasks, classified by role and in the same order the generated C# runs them."
        details={
          <p>
            A <strong>role</strong> is inferred from the component type: a connection-reading
            component is Source, a pipeline-only transform is Transform, a connection-writing
            component is Destination, anything else (a Script Task, a container) is Other. The
            flow diagram below each Data Flow Task mirrors the same paths the real package
            declares, not a guess at execution order.
          </p>
        }
      />
      {showExtractedLink && (
        <div className="mb-3 flex justify-end">
          <Link
            to={`/workspaces/${workspace.id}/extracted/${encodeURIComponent(packageName)}`}
            className="rounded border border-gray-300 bg-white px-3 py-1 text-xs font-medium text-gray-700 hover:bg-gray-50"
          >
            View extracted JSON
          </Link>
        </div>
      )}

      <div className="mb-4 flex flex-wrap gap-2">
        <CountChip label="Data flow tasks" value={data.dataFlows.length} color="blue" />
        <CountChip label="Pipeline components" value={totalComponents} color="purple" />
        <CountChip label="Connection managers" value={data.connectionManagers.length} color="gray" />
      </div>

      {roleBars.length > 0 && (
        <div className="mb-4">
          <h4 className="mb-1 text-sm font-semibold text-gray-700">Components by Role</h4>
          <BarChart data={roleBars} />
        </div>
      )}

      {data.dataFlows.length === 0 ? (
        <EmptyState message="This package has no Data Flow Tasks." />
      ) : (
        data.dataFlows.map((flow) => (
          <div key={flow.taskName} className="mb-8">
            <h3 className="mb-2 text-base font-semibold">{flow.taskName}</h3>
            <MermaidDiagram code={buildDataFlowChart(flow.components, flow.edges)} />

            <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
              <table className="w-full border-collapse text-sm">
                <thead>
                  <tr className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                    <th className="px-3 py-2">Component</th>
                    <th className="px-3 py-2">Type</th>
                    <th className="px-3 py-2">Role</th>
                    <th className="px-3 py-2">Connection</th>
                    <th className="px-3 py-2">Detail</th>
                  </tr>
                </thead>
                <tbody>
                  {flow.components.map((c, i) => (
                    <tr
                      key={c.name}
                      className={`cursor-pointer border-t border-gray-100 hover:bg-gray-50 ${
                        i % 2 === 1 ? "bg-gray-50/50" : ""
                      }`}
                      onClick={() => setSelected(c)}
                    >
                      <td className="px-3 py-2 font-medium">{c.name}</td>
                      <td className="px-3 py-2 font-mono text-xs">{c.type}</td>
                      <td className="px-3 py-2">
                        <Badge color={ROLE_COLORS[c.role]}>{c.role}</Badge>
                      </td>
                      <td className="px-3 py-2 font-mono text-xs">{c.connection ?? "—"}</td>
                      <td className="px-3 py-2 text-xs text-gray-600">{c.detail}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        ))
      )}

      <h3 className="mb-2 mt-8 text-base font-semibold">Connection Managers</h3>
      {data.connectionManagers.length === 0 ? (
        <EmptyState message="No connection managers in this package." />
      ) : (
        <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
          <table className="w-full border-collapse text-sm">
            <thead>
              <tr className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                <th className="px-3 py-2">Name</th>
                <th className="px-3 py-2">Type</th>
                <th className="px-3 py-2">Server</th>
                <th className="px-3 py-2">Database</th>
                <th className="px-3 py-2">File Path</th>
              </tr>
            </thead>
            <tbody>
              {data.connectionManagers.map((cm, i) => (
                <tr
                  key={cm.name}
                  className={`border-t border-gray-100 hover:bg-gray-50 ${i % 2 === 1 ? "bg-gray-50/50" : ""}`}
                >
                  <td className="px-3 py-2 font-medium">{cm.name}</td>
                  <td className="px-3 py-2 font-mono text-xs">{cm.type}</td>
                  <td className="px-3 py-2 font-mono text-xs">{cm.server ?? "—"}</td>
                  <td className="px-3 py-2 font-mono text-xs">{cm.database ?? "—"}</td>
                  <td className="px-3 py-2 font-mono text-xs">{cm.filePath ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {selected && (
        <div className="fixed bottom-0 right-0 top-0 w-[min(640px,90vw)] overflow-y-auto border-l border-gray-200 bg-white p-6 shadow-2xl">
          <div className="mb-4 flex justify-between">
            <strong>{selected.name}</strong>
            <button
              className="rounded border border-gray-300 px-2 py-0.5 text-xs hover:bg-gray-50"
              onClick={() => setSelected(null)}
            >
              Close
            </button>
          </div>
          <div className="mb-3 flex items-center gap-2">
            <Badge color={ROLE_COLORS[selected.role]}>{selected.role}</Badge>
            <span className="font-mono text-xs text-gray-500">{selected.type}</span>
          </div>
          <p className="mb-4 text-sm text-gray-700">{selected.detail}</p>

          <div className="mb-3 grid grid-cols-2 gap-3">
            <div>
              <h4 className="mb-1 text-xs font-semibold uppercase tracking-wide text-gray-500">
                Inputs
              </h4>
              {selected.inputs.length === 0 ? (
                <p className="text-xs italic text-gray-400">(none)</p>
              ) : (
                <ul className="list-inside list-disc text-xs text-gray-700">
                  {selected.inputs.map((n) => (
                    <li key={n}>{n}</li>
                  ))}
                </ul>
              )}
            </div>
            <div>
              <h4 className="mb-1 text-xs font-semibold uppercase tracking-wide text-gray-500">
                Outputs
              </h4>
              {selected.outputs.length === 0 ? (
                <p className="text-xs italic text-gray-400">(none)</p>
              ) : (
                <ul className="list-inside list-disc text-xs text-gray-700">
                  {selected.outputs.map((n) => (
                    <li key={n}>{n}</li>
                  ))}
                </ul>
              )}
            </div>
          </div>

          <h4 className="mb-1 mt-4 text-xs font-semibold uppercase tracking-wide text-gray-500">
            Raw component payload
          </h4>
          {selected.payload == null ? (
            <p className="text-xs italic text-gray-400">
              This component type has no dedicated payload captured by the extractor.
            </p>
          ) : (
            <CodeBlock language="json">{JSON.stringify(selected.payload, null, 2)}</CodeBlock>
          )}
        </div>
      )}
    </div>
  );
}
