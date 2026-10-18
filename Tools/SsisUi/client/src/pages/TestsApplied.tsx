import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  getTestsApplied,
  type MoreTestRecordSpec,
  type MoreTestStatus,
  type Workspace,
} from "../api/client";
import Badge, { type BadgeColor } from "../components/Badge";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";
import EmptyState from "../components/EmptyState";
import PageInfo from "../components/PageInfo";
import ApplyTestsModal from "../components/ApplyTestsModal";

const STATUS_COLORS: Record<MoreTestStatus, BadgeColor> = {
  Applied: "green",
  Rejected: "red",
  Unverified: "amber",
};

const STATUS_FILTERS: { key: "all" | MoreTestStatus; label: string }[] = [
  { key: "all", label: "All" },
  { key: "Applied", label: "Applied" },
  { key: "Rejected", label: "Rejected" },
  { key: "Unverified", label: "Unverified" },
];

function TestsPageInfo() {
  return (
    <PageInfo
      summary="The extra hand-written tests Apply Tests copied into each generated test project, and whether the project still compiles with them."
      details={
        <>
          <p>
            Apply Tests reads the MoreTests folder under the workspace's Fills Path, copies each
            file into the package's test project, and builds that project to check it. A file the
            compiler rejects is removed from the generated output again. Its original stays in the
            Fills Path, untouched.
          </p>
          <p>
            <strong>Applied</strong> means the file compiled with the project and is in place.{" "}
            <strong>Rejected</strong> means the compiler blamed the file. Open the row to see the
            errors, fix the file, and re-run Apply Tests. <strong>Unverified</strong> means the
            build failed for a reason no MoreTests file caused, so the copy was left in place
            without being checked.
          </p>
          <p>
            <strong>Undocumented</strong> means the file has no{" "}
            <code className="rounded bg-gray-200 px-1 py-0.5">// ssisx-more-test:</code> provenance
            comment. It is still applied, but nothing records who wrote it or why.
          </p>
        </>
      }
    />
  );
}

export default function TestsApplied({ workspace }: { workspace: Workspace }) {
  const [pkg, setPkg] = useState("all");
  const [status, setStatus] = useState<"all" | MoreTestStatus>("all");
  const [openKey, setOpenKey] = useState<string | null>(null);
  const queryClient = useQueryClient();

  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "tests-applied"],
    queryFn: () => getTestsApplied(workspace.id),
  });

  const [showWrite, setShowWrite] = useState(false);

  if (isLoading) return <LoadingState />;
  if (error) return <ErrorState error={error} />;

  if (!data || !data.available) {
    return (
      <div>
        <TestsPageInfo />
        <EmptyState message="Nothing here yet -- run Apply Tests (pipeline step 4b) after Generate has produced the package's test project." />
      </div>
    );
  }

  const tests = data.tests;
  const packages = [...new Set(tests.map((t) => t.Package))].sort((a, b) => a.localeCompare(b));
  const count = (s: MoreTestStatus) => tests.filter((t) => t.Status === s).length;
  const undocumented = tests.filter((t) => !t.Attributed).length;
  const visible = tests.filter(
    (t) => (pkg === "all" || t.Package === pkg) && (status === "all" || t.Status === status),
  );

  return (
    <div>
      <h2 className="mb-1 text-base font-semibold">Applied Tests</h2>
      <TestsPageInfo />

      <div className="mb-4 flex flex-wrap items-center gap-2">
        <Badge color="green">Applied {count("Applied")}</Badge>
        <Badge color="red">Rejected {count("Rejected")}</Badge>
        <Badge color="amber">Unverified {count("Unverified")}</Badge>
        <Badge color="gray">Undocumented {undocumented}</Badge>
        <div className="ml-auto flex items-center gap-2">
          <button
            type="button"
            onClick={() => queryClient.invalidateQueries({ queryKey: ["artifacts", workspace.id, "tests-applied"] })}
            className="cursor-pointer rounded border border-gray-300 bg-white px-2.5 py-1 text-xs font-medium text-gray-700 hover:bg-gray-50"
          >
            Refresh
          </button>
          <button
            type="button"
            onClick={() => setShowWrite(true)}
            className="cursor-pointer rounded bg-violet-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-violet-700"
          >
            Write more tests
          </button>
          {showWrite && <ApplyTestsModal workspace={workspace} onClose={() => setShowWrite(false)} />}
        </div>
      </div>

      <div className="mb-4 flex flex-wrap items-center gap-3">
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
        <div className="flex gap-1">
          {STATUS_FILTERS.map((f) => (
            <button
              key={f.key}
              type="button"
              onClick={() => setStatus(f.key)}
              className={`cursor-pointer rounded px-2.5 py-1 text-xs font-medium ${
                status === f.key
                  ? "bg-gray-800 text-white"
                  : "border border-gray-300 bg-white text-gray-700 hover:bg-gray-50"
              }`}
            >
              {f.label}
            </button>
          ))}
        </div>
      </div>

      {visible.length === 0 ? (
        <EmptyState message={tests.length === 0 ? "No extra tests were recorded." : "No tests match these filters."} />
      ) : (
        <div className="overflow-x-auto rounded-lg border border-gray-200 bg-white shadow-sm">
          <table className="w-full border-collapse text-sm">
            <thead>
              <tr className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                <th className="px-3 py-2">Package</th>
                <th className="px-3 py-2">File</th>
                <th className="px-3 py-2">Status</th>
                <th className="px-3 py-2">Author</th>
                <th className="px-3 py-2">Date</th>
                <th className="px-3 py-2">Targets</th>
              </tr>
            </thead>
            <tbody>
              {visible.map((t, i) => {
                const key = `${t.Package}/${t.FileName}`;
                const expandable = t.Status === "Rejected" && (t.CompilerErrors?.length ?? 0) > 0;
                return (
                  <TestRow
                    key={`${key}-${i}`}
                    record={t}
                    striped={i % 2 === 1}
                    expandable={expandable}
                    open={expandable && openKey === key}
                    onToggle={() => setOpenKey(openKey === key ? null : key)}
                  />
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function TestRow({
  record,
  striped,
  expandable,
  open,
  onToggle,
}: {
  record: MoreTestRecordSpec;
  striped: boolean;
  expandable: boolean;
  open: boolean;
  onToggle: () => void;
}) {
  return (
    <>
      <tr
        className={`border-t border-gray-100 ${expandable ? "cursor-pointer hover:bg-gray-50" : ""} ${
          striped ? "bg-gray-50/50" : ""
        }`}
        onClick={expandable ? onToggle : undefined}
      >
        <td className="px-3 py-2">{record.Package}</td>
        <td className="px-3 py-2 font-mono text-xs">{record.FileName}</td>
        <td className="px-3 py-2">
          <Badge color={STATUS_COLORS[record.Status]}>{record.Status}</Badge>
          {!record.Attributed && <span className="ml-2 text-xs text-gray-500">undocumented</span>}
        </td>
        <td className="px-3 py-2">{record.Author ?? "—"}</td>
        <td className="px-3 py-2">{record.Date ?? "—"}</td>
        <td className="px-3 py-2 font-mono text-xs">{record.Targets ?? "—"}</td>
      </tr>
      {open && (
        <tr className="border-t border-gray-100 bg-red-50/40">
          <td colSpan={6} className="px-3 py-2">
            <p className="mb-1 text-xs font-semibold text-red-700">
              Compiler errors -- fix the file under the Fills Path, then re-run Apply Tests:
            </p>
            <ul className="space-y-0.5 font-mono text-xs text-gray-800">
              {record.CompilerErrors?.map((e, i) => (
                <li key={i}>{e}</li>
              ))}
            </ul>
          </td>
        </tr>
      )}
    </>
  );
}
