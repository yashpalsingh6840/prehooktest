import { useEffect, useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { getValidation, type Workspace } from "../api/client";
import CodeBlock from "./CodeBlock";
import LoadingState from "./LoadingState";
import ErrorState from "./ErrorState";
import { CopyButton, PackageMultiSelect } from "./AiPromptModal";

type Assistant = "copilot" | "claude";

/** Only the invocation line differs between the two tabs -- both paste the same
 * instruction block, and both run the validator's own /ssis-tier2-review command. */
const INVOCATION: Record<Assistant, { heading: string; line: string }> = {
  copilot: {
    heading: "GitHub Copilot",
    line: "In Copilot Chat, open the validator folder (Tools\\SSIS_RewriteValidationTool) so its prompt files are available, then paste the block below as your message.",
  },
  claude: {
    heading: "Claude",
    line: "In Claude Code, open the validator folder (Tools\\SSIS_RewriteValidationTool) so its slash commands are available, then paste the block below as your message.",
  },
};

/** The instruction block a human pastes into the assistant. Built here from the
 * packages picked, so it always names the exact packet files to review. */
function buildBody(outputPath: string, packages: string[]): string {
  const root = `${outputPath}\\gate_reports`;
  return [
    "# Tier-2 AI review (SSIS rewrite validation)",
    "",
    "Review each package below by running its own command. Judge only the evidence packet",
    "that command is given. Do not open the .dtsx, other generated files, or any gaps\\ folder.",
    "",
    "## Packages",
    ...packages.map(
      (p) => `- /ssis-tier2-review ${p}  ->  packet: ${root}\\${p}\\ai_context\\${p}.ai-context.md`,
    ),
    "",
    "## Output",
    `Save each review next to its packet: ${root}\\<Package>\\ai_context\\<Package>.ai-review.md`,
    `and .ai-review.json. Never edit ${root}\\<Package>\\gates\\ (Tier-1 reports are never changed here).`,
    "",
    "## Rules",
    "- Advisory only. A person confirms each verdict before it means anything.",
    "- Use only the packet. Evidence the packet lacks is AiInconclusive, never a guessed match.",
    "- Verdicts: AiConfirmedMatch, AiSuspectedDiscrepancy, AiInconclusive, AiNotApplicable.",
    "  For left-only gaps: AiFoundUnmappedImplementation or AiConfirmedGap.",
    "- Every verdict cites specific evidence from the packet.",
  ].join("\n");
}

export default function AiReviewModal({
  workspace,
  onClose,
}: {
  workspace: Workspace;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [assistant, setAssistant] = useState<Assistant>("copilot");

  const overviewQuery = useQuery({
    queryKey: ["artifacts", workspace.id, "validation"],
    queryFn: () => getValidation(workspace.id),
  });
  const overview = overviewQuery.data ?? [];
  const allNames = overview.map((o) => o.package);
  // Only packages with an evidence packet can be reviewed.
  const ready = overview.filter((o) => o.stages.packet).map((o) => o.package);

  // null = the default (every package that has a packet). Once the user picks,
  // it becomes an explicit list.
  const [selected, setSelected] = useState<string[] | null>(null);
  const effective = selected ?? ready;
  const skipped = effective.filter((p) => !ready.includes(p));
  const reviewable = effective.filter((p) => ready.includes(p));

  const body = useMemo(
    () => buildBody(workspace.outputPath, reviewable),
    [workspace.outputPath, reviewable],
  );

  function togglePackage(name: string) {
    const next = effective.includes(name)
      ? effective.filter((n) => n !== name)
      : [...effective, name];
    // Keep the picker's own order so the selection stays stable.
    setSelected(allNames.filter((n) => next.includes(n)));
  }

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") onClose();
    }
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  function done() {
    // The review is written by the assistant, not by this app, so refresh every
    // view that reads validation output or step status.
    queryClient.invalidateQueries({ queryKey: ["artifacts", workspace.id] });
    queryClient.invalidateQueries({ queryKey: ["step-status", workspace.id] });
    onClose();
  }

  const invocation = INVOCATION[assistant];

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-gray-900/40 p-4"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-label="AI review"
        className="flex max-h-[90vh] w-full min-w-0 max-w-3xl flex-col rounded-lg border border-gray-200 bg-white shadow-xl"
      >
        <div className="flex shrink-0 items-center justify-between border-b border-gray-200 px-4 py-3">
          <h2 className="text-base font-semibold text-gray-800">AI review</h2>
          <button
            type="button"
            aria-label="Close"
            className="cursor-pointer rounded px-2 py-1 text-sm text-gray-500 hover:bg-gray-100"
            onClick={onClose}
          >
            ✕
          </button>
        </div>

        <div className="min-h-[14rem] min-w-0 flex-1 overflow-y-auto px-4 py-3 text-sm text-gray-700">
          <p className="mb-3 text-gray-500">
            The AI review is done by an AI assistant, not by this app. It judges each package's
            evidence packet and writes an advisory review next to it. Paste the instructions into
            your assistant, then come back and press Done to refresh.
          </p>

          {overviewQuery.isLoading && <LoadingState />}
          {overviewQuery.error && <ErrorState error={overviewQuery.error} />}

          {!overviewQuery.isLoading && ready.length === 0 && (
            <p className="mb-3 rounded border border-amber-200 bg-amber-50 p-2 text-xs text-amber-800">
              No evidence packets yet. Run Evidence Packets on the Pipeline page first.
            </p>
          )}

          {allNames.length > 0 && (
            <div className="mb-3 flex flex-wrap items-center gap-3">
              <span className="text-xs font-semibold uppercase tracking-wide text-gray-500">Packages</span>
              <PackageMultiSelect
                names={allNames}
                selected={effective}
                onToggle={togglePackage}
                onSelectAll={() => setSelected(ready)}
                onClear={() => setSelected([])}
              />
              {reviewable.length === 0 && (
                <span className="text-xs text-amber-700">Select at least one package that has a packet.</span>
              )}
              {skipped.length > 0 && (
                <span className="text-xs text-amber-700">
                  Skipped (no packet yet): {skipped.join(", ")}
                </span>
              )}
            </div>
          )}

          <div role="tablist" className="mb-3 flex gap-1 border-b border-gray-200">
            {(["copilot", "claude"] as Assistant[]).map((a) => (
              <button
                key={a}
                type="button"
                role="tab"
                aria-selected={assistant === a}
                className={`cursor-pointer -mb-px border-b-2 px-3 py-1.5 text-sm ${
                  assistant === a
                    ? "border-blue-600 font-semibold text-blue-700"
                    : "border-transparent text-gray-500 hover:text-gray-700"
                }`}
                onClick={() => setAssistant(a)}
              >
                {INVOCATION[a].heading}
              </button>
            ))}
          </div>

          {reviewable.length > 0 && (
            <>
              <p className="mb-2">{invocation.line}</p>
              <div className="relative">
                <div className="absolute right-2 top-4 z-10">
                  <CopyButton text={body} label="Copy instructions" />
                </div>
                <CodeBlock language="markdown">{body}</CodeBlock>
              </div>
            </>
          )}
        </div>

        <div className="flex shrink-0 items-center justify-end gap-2 border-t border-gray-200 px-4 py-3">
          <button
            type="button"
            className="cursor-pointer rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
            onClick={done}
          >
            Done, refresh
          </button>
        </div>
      </div>
    </div>
  );
}
