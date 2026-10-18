import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import Editor from "@monaco-editor/react";
import {
  getDecisionsFile,
  getFillContent,
  putDecisionsFile,
  putFillContent,
  startRun,
  suggestFillPath,
  type GapSpec,
  type Workspace,
} from "../api/client";
import LoadingState from "./LoadingState";
import ErrorState from "./ErrorState";

const TIER1_KINDS: ReadonlySet<GapSpec["Kind"]> = new Set([
  "EncryptedConnectionManagerSecret",
  "LookupJoinKey",
]);

const FILE_BASED_KINDS: ReadonlySet<GapSpec["Kind"]> = new Set([
  "ScriptTask",
  "ScriptComponentColumn",
  "TestOracle",
  "LocalFileSourceData",
]);

export function gapHasFillMechanism(kind: GapSpec["Kind"]): boolean {
  return TIER1_KINDS.has(kind) || FILE_BASED_KINDS.has(kind);
}

function languageFor(kind: GapSpec["Kind"]): string {
  return kind === "LocalFileSourceData" ? "plaintext" : "csharp";
}

function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}

function fillHeader(gap: GapSpec, author: string): string {
  const parts = [`GapId=${gap.GapId}`, `Author=${author || "reviewer"}`, `Date=${todayIso()}`];
  if (gap.EvidenceSha256) parts.push(`EvidenceSha256=${gap.EvidenceSha256}`);
  return `// ssisx-fill: ${parts.join(" ")}\n`;
}

/** Which pipeline step actually consumes this gap's fill, per the real
 * ssisx mechanism: a Tier-1 decisions.json is read by `generate` itself
 * (GenerateCommand.LoadDecisions, via --fills); every file-based fill
 * (ScriptTask/ScriptComponentColumn/TestOracle/LocalFileSourceData) is
 * consumed by `apply-fills`. */
function followUpStep(kind: GapSpec["Kind"]): { step: "generate" | "apply-fills"; label: string } {
  if (TIER1_KINDS.has(kind)) {
    return { step: "generate", label: "Re-run Generate to apply this decision" };
  }
  return { step: "apply-fills", label: "Re-run Apply Fills to pick this up" };
}

export default function FillEditor({
  workspace,
  gap,
  onClose,
}: {
  workspace: Workspace;
  gap: GapSpec;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const isTier1 = TIER1_KINDS.has(gap.Kind);

  const [relPath, setRelPath] = useState<string>("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [triggering, setTriggering] = useState(false);
  const [triggered, setTriggered] = useState(false);

  // Tier-1 form state
  const [confirmedBy, setConfirmedBy] = useState("");
  const [confirmedOn, setConfirmedOn] = useState(todayIso());
  const [confidence, setConfidence] = useState("");
  const [rationale, setRationale] = useState("");
  const [inputColumn, setInputColumn] = useState("");
  const [referenceColumn, setReferenceColumn] = useState("");

  // File-based editor state
  const [content, setContent] = useState("");
  const [author, setAuthor] = useState("");

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setSaved(false);
    setTriggered(false);

    (async () => {
      try {
        if (isTier1) {
          const decisions = await getDecisionsFile(workspace.id, gap.Package);
          if (cancelled) return;
          const existing = decisions[gap.GapId] as Record<string, unknown> | undefined;
          if (existing) {
            setConfirmedBy(typeof existing.confirmedBy === "string" ? existing.confirmedBy : "");
            setConfirmedOn(
              typeof existing.confirmedOn === "string" ? existing.confirmedOn : todayIso(),
            );
            setConfidence(typeof existing.confidence === "string" ? existing.confidence : "");
            setRationale(typeof existing.rationale === "string" ? existing.rationale : "");
            setInputColumn(typeof existing.inputColumn === "string" ? existing.inputColumn : "");
            setReferenceColumn(
              typeof existing.referenceColumn === "string" ? existing.referenceColumn : "",
            );
          }
          setRelPath(`${gap.Package}.decisions.json`);
        } else {
          const { path: suggested } = await suggestFillPath(workspace.id, gap.GapId);
          if (cancelled) return;
          setRelPath(suggested);
          const existing = await getFillContent(workspace.id, suggested);
          if (cancelled) return;
          setContent(existing ?? (gap.Kind === "LocalFileSourceData" ? "" : fillHeader(gap, "")));
        }
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workspace.id, gap.GapId]);

  async function handleSave() {
    setSaving(true);
    setError(null);
    try {
      if (isTier1) {
        const decisions = await getDecisionsFile(workspace.id, gap.Package);
        const entry: Record<string, unknown> = {
          confirmedBy,
          confirmedOn,
        };
        if (confidence) entry.confidence = confidence;
        if (rationale) entry.rationale = rationale;
        if (gap.EvidenceSha256) entry.evidenceSha256 = gap.EvidenceSha256;
        if (gap.Kind === "LookupJoinKey") {
          entry.inputColumn = inputColumn;
          entry.referenceColumn = referenceColumn;
        }
        decisions[gap.GapId] = entry;
        await putDecisionsFile(workspace.id, gap.Package, decisions);
      } else {
        await putFillContent(workspace.id, relPath, content);
      }
      setSaved(true);
      queryClient.invalidateQueries({ queryKey: ["artifacts", workspace.id] });
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  async function handleTriggerFollowUp() {
    setTriggering(true);
    try {
      await startRun(workspace.id, followUpStep(gap.Kind).step);
      setTriggered(true);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setTriggering(false);
    }
  }

  function insertHeader() {
    setContent((prev) => fillHeader(gap, author) + prev.replace(/^\/\/ ssisx-fill:.*\n/, ""));
  }

  if (!gapHasFillMechanism(gap.Kind)) {
    return (
      <div className="p-4">
        <p className="text-sm text-gray-500">
          This is a tool limitation ({gap.Kind}), not something a fill can close -- {gap.Reason}
        </p>
        <button className={btnSecondary} onClick={onClose}>
          Close
        </button>
      </div>
    );
  }

  return (
    <div className="p-4 pb-6">
      <div className="mb-3 flex items-baseline justify-between">
        <h4 className="m-0 text-base font-semibold">Fill: {gap.GapId}</h4>
        <button className={btnSecondary} onClick={onClose}>
          Close
        </button>
      </div>

      {loading && <LoadingState />}
      {error && <ErrorState error={error} />}

      {!loading && isTier1 && (
        <div className="flex max-w-[480px] flex-col gap-3">
          <label className={labelCls}>
            Target file
            <div className="mt-0.5 font-mono text-xs text-gray-500">{relPath}</div>
          </label>
          {gap.Kind === "LookupJoinKey" && (
            <>
              <label className={labelCls}>
                Input column
                <input
                  className={inputCls}
                  value={inputColumn}
                  onChange={(e) => setInputColumn(e.target.value)}
                />
              </label>
              <label className={labelCls}>
                Reference column
                <input
                  className={inputCls}
                  value={referenceColumn}
                  onChange={(e) => setReferenceColumn(e.target.value)}
                />
              </label>
              <label className={labelCls}>
                Confidence
                <select
                  className={inputCls}
                  value={confidence}
                  onChange={(e) => setConfidence(e.target.value)}
                >
                  <option value="">(none)</option>
                  <option value="High">High</option>
                  <option value="Medium">Medium</option>
                  <option value="Low">Low</option>
                </select>
              </label>
            </>
          )}
          <label className={labelCls}>
            Rationale
            <textarea
              className={inputCls}
              value={rationale}
              onChange={(e) => setRationale(e.target.value)}
              rows={3}
            />
          </label>
          <label className={labelCls}>
            Confirmed by <span className="text-red-700">*</span>
            <input
              className={inputCls}
              value={confirmedBy}
              onChange={(e) => setConfirmedBy(e.target.value)}
            />
          </label>
          <label className={labelCls}>
            Confirmed on
            <input
              className={inputCls}
              type="date"
              value={confirmedOn}
              onChange={(e) => setConfirmedOn(e.target.value)}
            />
          </label>
          <label className={labelCls}>
            Evidence SHA-256 (from the work packet, read-only)
            <input
              className={`${inputCls} text-gray-500`}
              value={gap.EvidenceSha256 ?? "(none recorded)"}
              readOnly
            />
          </label>
          <p className="text-xs text-gray-500">
            An entry with no "Confirmed by" is a proposal, not a decision -- <code>ssisx generate</code> reports it as
            pending and does not use it.
          </p>
          <button className={btnPrimary} onClick={handleSave} disabled={saving || !confirmedBy}>
            {saving ? "Saving..." : "Save decision"}
          </button>
        </div>
      )}

      {!loading && !isTier1 && (
        <div>
          <div className="mb-2 flex items-center gap-2">
            <label className="text-xs text-gray-600">
              Path:{" "}
              <input
                className="w-[360px] rounded border border-gray-300 px-2 py-1 font-mono text-xs"
                value={relPath}
                onChange={(e) => setRelPath(e.target.value)}
                readOnly={gap.Kind === "LocalFileSourceData"}
              />
            </label>
            {gap.Kind !== "LocalFileSourceData" && (
              <>
                <input
                  className="w-[140px] rounded border border-gray-300 px-2 py-1 text-sm"
                  placeholder="Author"
                  value={author}
                  onChange={(e) => setAuthor(e.target.value)}
                />
                <button className={btnSecondary} onClick={insertHeader}>
                  Insert fill header
                </button>
              </>
            )}
          </div>
          <div className="overflow-hidden rounded border border-gray-300">
            <Editor
              height="420px"
              language={languageFor(gap.Kind)}
              value={content}
              onChange={(v) => setContent(v ?? "")}
              options={{ minimap: { enabled: false }, fontSize: 13 }}
            />
          </div>
          <div className="mt-2">
            <button className={btnPrimary} onClick={handleSave} disabled={saving || !relPath}>
              {saving ? "Saving..." : "Save fill"}
            </button>
          </div>
        </div>
      )}

      {saved && (
        <div className="mt-4 rounded border border-green-200 bg-green-50 p-3">
          <p className="m-0 mb-2 text-sm text-green-800">
            Saved. {followUpStep(gap.Kind).label}.
          </p>
          <button className={btnPrimary} onClick={handleTriggerFollowUp} disabled={triggering || triggered}>
            {triggered ? "Started" : triggering ? "Starting..." : `Run ${followUpStep(gap.Kind).step}`}
          </button>
        </div>
      )}
    </div>
  );
}

const labelCls = "flex flex-col gap-1 text-sm text-gray-700";
const inputCls = "w-full rounded border border-gray-300 px-2 py-1 text-sm";
const btnPrimary =
  "rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50";
const btnSecondary =
  "rounded border border-gray-300 px-3 py-1.5 text-sm hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50";
