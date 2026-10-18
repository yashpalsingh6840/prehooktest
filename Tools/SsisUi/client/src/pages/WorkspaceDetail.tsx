import { createPortal } from "react-dom";
import { Route, Routes, useLocation, useParams } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { getWorkspace, openPath, type Workspace } from "../api/client";
import PipelineDashboard from "./PipelineDashboard";
import Reports from "./Reports";
import GapsExplorer from "./GapsExplorer";
import FillsStatus from "./FillsStatus";
import TestsApplied from "./TestsApplied";
import Walkthrough from "./Walkthrough";
import ExtractedData from "./ExtractedData";
import SampleDataPage from "./SampleDataPage";
import DashboardSummary from "./DashboardSummary";
import CoveragePage from "./CoveragePage";
import ValidationPage from "./ValidationPage";
import Sidebar from "../components/Sidebar";
import NavIcon from "../components/NavIcon";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";

function join(root: string, ...parts: string[]): string {
  const trimmedRoot = root.replace(/[\\/]+$/, "");
  return [trimmedRoot, ...parts].join("\\");
}

/** Which on-disk folder is actually relevant to whatever the user is
 * currently looking at -- recomputed on every navigation, unlike a fixed
 * "Input Path" label, so it genuinely changes as you move around the app. */
function contextualPath(ws: Workspace, pathname: string, base: string): { label: string; path: string } {
  const rest = pathname.startsWith(base) ? pathname.slice(base.length) : pathname;
  const segments = rest.split("/").filter(Boolean);
  const [first, ...rem] = segments;

  switch (first) {
    case "extracted":
      return { label: "Extract output", path: join(ws.outputPath, "extract") };
    case "reports": {
      if ((rem[0] ?? "generate") === "generate") {
        return { label: "Generate output", path: join(ws.outputPath, "gen", "generate") };
      }
      return { label: "Extract metadata", path: join(ws.outputPath, "extract") };
    }
    case "gaps":
      return { label: "Gaps", path: join(ws.outputPath, "gen") };
    case "fills":
      return { label: "Fills (authored)", path: ws.fillsPath };
    case "coverage":
      return { label: "Coverage output", path: join(ws.outputPath, "gen", "generate") };
    case "validation":
      return { label: "Validation output", path: join(ws.outputPath, "gate_reports") };
    case "walkthrough":
      return rem[1] === "sampledata"
        ? { label: "Sample data", path: join(ws.outputPath, "svk", "sampledata") }
        : { label: "Walkthrough", path: join(ws.outputPath, "svk", "walkthrough") };
    case "sampledata":
      return { label: "Sample data", path: join(ws.outputPath, "svk", "sampledata") };
    default:
      return { label: "Output", path: ws.outputPath };
  }
}

function ContextualPath({ workspace }: { workspace: Workspace }) {
  const location = useLocation();
  const base = `/workspaces/${workspace.id}`;
  const { label, path } = contextualPath(workspace, location.pathname, base);

  const mutation = useMutation({ mutationFn: () => openPath(workspace.id, path) });

  return (
    <div className="flex min-w-0 items-center gap-1.5">
      <span className="shrink-0 text-[11px] font-medium uppercase tracking-wide text-gray-400">
        {label}:
      </span>
      <span className="truncate font-mono text-xs text-gray-500" title={path}>
        {path}
      </span>
      <button
        type="button"
        onClick={() => mutation.mutate()}
        disabled={mutation.isPending}
        title="Open this folder in File Explorer"
        aria-label={`Open ${label} folder`}
        className="flex h-4 w-4 shrink-0 items-center justify-center rounded-full border border-gray-300 bg-white text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-600 disabled:cursor-not-allowed disabled:opacity-50"
      >
        <NavIcon name="folder" className="h-2.5 w-2.5" />
      </button>
      {mutation.isError && (
        <span className="shrink-0 text-xs text-red-700">
          {mutation.error instanceof Error ? mutation.error.message : String(mutation.error)}
        </span>
      )}
    </div>
  );
}

/** The workspace name and output path, shown on the right of the top bar. */
function WorkspaceHeaderInfo({ workspace }: { workspace: Workspace }) {
  const slot = document.getElementById("app-header-slot");
  if (!slot) return null;
  return createPortal(
    <div className="flex min-w-0 items-center gap-3">
      <h1 className="shrink-0 text-base font-semibold">{workspace.name}</h1>
      <ContextualPath workspace={workspace} />
    </div>,
    slot,
  );
}

export default function WorkspaceDetail() {
  const { id } = useParams<{ id: string }>();

  const { data: workspace, isLoading, error } = useQuery({
    queryKey: ["workspace", id],
    queryFn: () => getWorkspace(id!),
    enabled: !!id,
  });

  return (
    <main className="mx-auto max-w-7xl p-4">
      {isLoading && <LoadingState />}
      {error && <ErrorState error={error} />}

      {workspace && (
        <>
          <WorkspaceHeaderInfo workspace={workspace} />

          <div className="flex gap-6">
            <Sidebar workspace={workspace} />
            <div className="min-w-0 flex-1">
              <Routes>
                <Route index element={<PipelineDashboard workspace={workspace} />} />
                <Route path="dashboard" element={<DashboardSummary workspace={workspace} />} />
                <Route path="reports/:section?" element={<Reports workspace={workspace} />} />
                <Route path="gaps/:filter?" element={<GapsExplorer workspace={workspace} />} />
                <Route path="fills" element={<FillsStatus workspace={workspace} />} />
                <Route path="tests" element={<TestsApplied workspace={workspace} />} />
                <Route path="coverage" element={<CoveragePage workspace={workspace} />} />
                <Route
                  path="validation/:pkg?/:tab?"
                  element={<ValidationPage workspace={workspace} />}
                />
                <Route
                  path="extracted/:pkg?"
                  element={<ExtractedData workspace={workspace} />}
                />
                <Route
                  path="walkthrough/:pkg?/:tab?"
                  element={<Walkthrough workspace={workspace} />}
                />
                <Route
                  path="sampledata/:pkg?"
                  element={<SampleDataPage workspace={workspace} />}
                />
              </Routes>
            </div>
          </div>
        </>
      )}
    </main>
  );
}
