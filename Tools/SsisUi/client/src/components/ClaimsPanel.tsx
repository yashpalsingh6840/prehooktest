import { useState, useEffect } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  getWalkthroughClaims,
  updateWalkthroughClaim,
  type Workspace,
  type WalkthroughClaim,
  type WalkthroughStatus,
} from "../api/client";
import { type BadgeColor } from "./Badge";
import LoadingState from "./LoadingState";
import ErrorState from "./ErrorState";
import EmptyState from "./EmptyState";

const STATUSES: WalkthroughStatus[] = ["NotValidated", "Validated", "Blocked"];

const STATUS_COLORS: Record<WalkthroughStatus, BadgeColor> = {
  Validated: "green",
  NotValidated: "amber",
  Blocked: "red",
};

const STATUS_SELECT_CLASSES: Record<BadgeColor, string> = {
  green: "border-green-300 bg-green-50 text-green-800",
  amber: "border-amber-300 bg-amber-50 text-amber-800",
  red: "border-red-300 bg-red-50 text-red-800",
  gray: "border-gray-300 bg-gray-50 text-gray-700",
  blue: "border-blue-300 bg-blue-50 text-blue-800",
  purple: "border-purple-300 bg-purple-50 text-purple-800",
};

const inputCls = "w-full rounded border border-gray-300 px-2 py-1 text-sm";
const btnPrimary =
  "rounded bg-blue-600 px-3 py-1 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50";

function ClaimRow({
  workspaceId,
  packageName,
  claim,
}: {
  workspaceId: string;
  packageName: string;
  claim: WalkthroughClaim;
}) {
  const queryClient = useQueryClient();
  const [status, setStatus] = useState<WalkthroughStatus>(claim.Status);
  const [reviewer, setReviewer] = useState(claim.Reviewer ?? "");
  const [note, setNote] = useState(claim.Note ?? "");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  // Re-sync local edit state if the underlying claim changes out from under
  // us (e.g. another save, or a query refetch after this row's own save).
  useEffect(() => {
    setStatus(claim.Status);
    setReviewer(claim.Reviewer ?? "");
    setNote(claim.Note ?? "");
  }, [claim.Status, claim.Reviewer, claim.Note]);

  async function handleSave() {
    setSaving(true);
    setError(null);
    setSaved(false);
    try {
      await updateWalkthroughClaim(workspaceId, packageName, {
        ruleId: claim.RuleId,
        status,
        reviewer,
        note,
      });
      setSaved(true);
      queryClient.invalidateQueries({
        queryKey: ["artifacts", workspaceId, "walkthrough-claims", packageName],
      });
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  return (
    <tr className="border-t border-gray-100 hover:bg-gray-50">
      <td className="break-all px-3 py-2 font-mono text-xs">{claim.RuleId}</td>
      <td className="px-3 py-2">
        <select
          value={status}
          onChange={(e) => setStatus(e.target.value as WalkthroughStatus)}
          className={`rounded border px-2 py-1 text-xs font-semibold ${STATUS_SELECT_CLASSES[STATUS_COLORS[status]]}`}
        >
          {STATUSES.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
      </td>
      <td className="px-3 py-2">
        <input
          value={reviewer}
          onChange={(e) => setReviewer(e.target.value)}
          className={`${inputCls} w-[120px]`}
        />
      </td>
      <td className="px-3 py-2">
        <textarea
          value={note}
          onChange={(e) => setNote(e.target.value)}
          rows={2}
          className={`${inputCls} min-w-[180px]`}
        />
      </td>
      <td className="px-3 py-2 align-top">
        <button className={btnPrimary} onClick={handleSave} disabled={saving}>
          {saving ? "Saving..." : "Save"}
        </button>
        {saved && <div className="mt-1 text-xs text-green-700">Saved</div>}
        {error && <div className="mt-1 text-xs text-red-700">{error}</div>}
      </td>
    </tr>
  );
}

export function ClaimsPanel({ workspace, packageName }: { workspace: Workspace; packageName: string }) {
  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "walkthrough-claims", packageName],
    queryFn: () => getWalkthroughClaims(workspace.id, packageName),
  });

  if (isLoading) return <LoadingState label="Loading rule reviews..." />;
  if (error) return <ErrorState error={error} />;
  if (!data || !data.available || data.claims.length === 0) {
    return (
      <EmptyState message="No rule-review claims for this package yet -- run Walkthrough first." />
    );
  }

  return (
    <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
      <table className="w-full border-collapse text-sm">
        <thead>
          <tr className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
            <th className="px-3 py-2">Rule ID</th>
            <th className="px-3 py-2">Status</th>
            <th className="px-3 py-2">Reviewer</th>
            <th className="px-3 py-2">Note</th>
            <th className="px-3 py-2"></th>
          </tr>
        </thead>
        <tbody>
          {data.claims.map((claim) => (
            <ClaimRow
              key={claim.RuleId}
              workspaceId={workspace.id}
              packageName={packageName}
              claim={claim}
            />
          ))}
        </tbody>
      </table>
    </div>
  );
}
