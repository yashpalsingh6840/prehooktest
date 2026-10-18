import { useState, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  cancelRun,
  listRuns,
  getStepStatus,
  getValidation,
  openStepFolder,
  startRun,
  type Run,
  type RunStatus,
  type Step,
  type StepKey,
  type Workspace,
} from "../api/client";
import LogPane from "../components/LogPane";
import InfoPopover from "../components/InfoPopover";
import PageInfo from "../components/PageInfo";
import NavIcon from "../components/NavIcon";
import ApplyFillsModal from "../components/ApplyFillsModal";
import ApplyTestsModal from "../components/ApplyTestsModal";
import AiReviewModal from "../components/AiReviewModal";
import { formatDateTime } from "../utils/formatDateTime";

const STEP_LABELS: Record<Step, string> = {
  extract: "Extract",
  walkthrough: "Walkthrough",
  sampledata: "Sample Data",
  generate: "Generate",
  "apply-fills": "Apply Fills",
  "apply-tests": "Apply Tests",
  build: "Build",
  coverage: "Coverage",
  validate: "Validate",
  "ai-context": "Evidence Packets",
  "final-summary": "Final Summary",
  validation: "Run Validate + Packets",
  all: "Run Phase 1",
};

/** The AI review is not a CLI step -- it runs in Claude or Copilot -- so its
 * card is a separate, run-less card rather than a Step. */
type CardId = Step | "ai-review";

/** Phase 2 steps. Their run buttons are teal so they read apart from Phase 1's blue. */
const PHASE2_STEPS = new Set<Step>(["validate", "ai-context", "final-summary"]);

/** Maps a card's step to the key used by the step-status endpoint. Build and
 * Run All have no output of their own to check, so they map to nothing. */
const STEP_STATUS_KEY: Partial<Record<Step, StepKey>> = {
  extract: "extract",
  walkthrough: "walkthrough",
  sampledata: "sampledata",
  generate: "generate",
  "apply-fills": "applyFills",
  "apply-tests": "applyTests",
  coverage: "coverage",
  validate: "validate",
  "ai-context": "aiContext",
  "final-summary": "finalSummary",
};

/** Pipeline rows, top to bottom. Phase 2 needs Phase 1's Generate output, so
 * its cards stay disabled until Generate has run. */
const PHASES: { id: "phase1" | "phase2"; title: string; blurb: string; cards: CardId[] }[] = [
  {
    id: "phase1",
    title: "Phase 1 - Extract/Generate",
    blurb: "Turns the SSIS packages into a generated C# solution, then builds and tests it.",
    // "all" (Run Phase 1) is hidden while a new pipeline step is being added; its definition
    // above is kept so it can be listed here again.
    cards: ["extract", "walkthrough", "sampledata", "generate", "apply-fills", "apply-tests", "build", "coverage"],
  },
  {
    id: "phase2",
    title: "Phase 2 - Validation",
    blurb: "Checks each generated package against its .dtsx and summarises what still needs a human.",
    cards: ["validate", "ai-context", "ai-review", "final-summary"],
  },
];

/** What each step does, where it writes, and what needs to happen before/
 * after it -- shown via the card's own info icon. `output` is relative to
 * the workspace's own Output Path, resolved at render time in StepCard. */
const STEP_INFO: Record<
  Step,
  { what: string; output: string; prereq: string | null; next: string }
> = {
  extract: {
    what:
      "Reads every .dtsx/.dtproj under the workspace's Input Path and writes a deterministic, redacted spec per package, plus analysis metadata (findings, portfolio digest, load failures).",
    output: "extract\\",
    prereq: null,
    next: "Run this first -- everything else reads from its output. Then try Walkthrough / Sample Data, or go straight to Generate.",
  },
  walkthrough: {
    what:
      "Runs `svk walkthrough` against the Extract output, producing a human-readable, component-by-component markdown narrative per package, plus a claims file for reviewing it.",
    output: "svk\\walkthrough\\",
    prereq: "Extract must have run first.",
    next: "Optional, read-only review step -- doesn't block Generate.",
  },
  sampledata: {
    what:
      "Runs `svk sampledata` against the Extract output, producing representative sample rows per package so you can sanity-check column shapes before generating code.",
    output: "svk\\sampledata\\",
    prereq: "Extract must have run first.",
    next: "Optional -- doesn't block Generate.",
  },
  generate: {
    what:
      "Turns each package's extracted spec into a runnable C# project: production code, starter tests, and a gap report (gaps.json) with AI work packets for anything it couldn't derive on its own.",
    output: "gen\\generate\\ (+ gen\\generate-report.md, gen\\gaps.json)",
    prereq: "Extract must have run first.",
    next: "Review the Gaps tab, author fills under the workspace's Fills Path, then run Apply Fills.",
  },
  "apply-fills": {
    what:
      "Copies hand-authored fills (Tier-1 decisions, Tier-2 script ports, test oracles, local sample data) from the workspace's Fills Path into the generated solution -- matched by each fill's own provenance header, never by file path.",
    output: "gen\\generate\\<Package>\\Fills\\ (+ gen\\fills-applied.json)",
    prereq: "Generate must have run first, and at least one fill file already written.",
    next: "Check the Fills tab for Applied/Stale/Orphaned status, then Build.",
  },
  "apply-tests": {
    what:
      "Copies hand-written extra tests (MoreTests) from the workspace's Fills Path into each generated test project, then builds that project to check each one. A file that does not compile is removed again; its original stays in the Fills Path.",
    output: "gen\generate\<Package>.Tests\MoreTests\ (+ gen\tests-applied.json)",
    prereq: "Generate must have run first, and at least one MoreTests file already written under the Fills Path.",
    next: "Check the Applied Tests tab for Rejected/Unverified files, then Build.",
  },
  build: {
    what:
      "Runs `dotnet build` against the generated Generated.slnx (Release) to confirm the generated code -- with any applied fills -- actually compiles.",
    output: "(console output only -- no files written)",
    prereq: "Generate must have run first.",
    next: "A clean build means the code is ready for Coverage, or to hand off as-is.",
  },
  coverage: {
    what:
      "Runs each package's unit tests (every test except the Integration-tagged ones, which need a real database or real files) and reports line/branch coverage for the generated code. Integration tests are NOT included in these numbers.",
    output: "gen\\generate\\coverage-report.md",
    prereq: "Generate must have run first (and ideally a successful Build).",
    next: "Review coverage-report.md to see how much of the generated code the starter tests actually exercise.",
  },
  validate: {
    what:
      "Runs the SSIS_RewriteValidationTool gates for every package (or the one named): Gate 1 compares SQL, Gate 2 the control and data-flow graph, Gate 3 each component's column schema. Each gate writes a report of what matches and what does not.",
    output: "gate_reports\\<Package>\\gates\\",
    prereq: "Generate must have run first.",
    next: "Open the Validation page for anything that does not match. Then run Evidence Packets.",
  },
  "ai-context": {
    what:
      "Packages the findings the gates could not settle on their own into a Tier-2 evidence packet (<Package>.ai-context.md) for AI review. Pure extraction -- no AI runs here.",
    output: "gate_reports\\<Package>\\ai_context\\",
    prereq: "Validate must have run first.",
    next: "Hand each packet to an AI review (see the AI Review card), then run Final Summary.",
  },
  "final-summary": {
    what:
      "Merges the gate reports and any AI review into four buckets: Tool-Verified, Exception, AI-Verified and Human-Needs-Review.",
    output: "gate_reports\\<Package>\\final_review\\",
    prereq: "Validate must have run first. Run it after the AI review, so the summary includes the review.",
    next: "Human-Needs-Review is the work list -- open the Validation page to work through it.",
  },
  validation: {
    what:
      "Runs Validate and Evidence Packets for every package in order. Final Summary is left out on purpose: it should run after the AI review. The AI review itself needs an AI session.",
    output: "gate_reports\\ (gates and packets, per package)",
    prereq: "Generate must have run first.",
    next: "Then do the AI review for each package that has a packet, and run Final Summary again.",
  },
  all: {
    what:
      "Runs every Phase 1 step in order -- Extract, Walkthrough, Sample Data, Generate, Apply Fills, Build, Coverage -- stopping early only on a genuine failure. Phase 2 (Validation) is not included: run its cards yourself, after the AI review.",
    output: "Same locations as running each step individually.",
    prereq: null,
    next: "The easiest way to refresh everything after changing a package or a fill.",
  },
};

/** Steps that correspond to an in-app page worth jumping straight to -- Build
 * and Coverage have no dedicated viewer (console output / a bare .md file
 * nothing renders yet), and "Run All" doesn't correspond to any one page. */
const STEP_ROUTE: Partial<Record<Step, (workspaceId: string) => string>> = {
  extract: (id) => `/workspaces/${id}/extracted`,
  walkthrough: (id) => `/workspaces/${id}/walkthrough`,
  sampledata: (id) => `/workspaces/${id}/walkthrough`,
  generate: (id) => `/workspaces/${id}/reports/generate`,
  "apply-fills": (id) => `/workspaces/${id}/fills`,
  validate: (id) => `/workspaces/${id}/validation`,
  "ai-context": (id) => `/workspaces/${id}/validation`,
  "final-summary": (id) => `/workspaces/${id}/validation`,
};

export default function PipelineDashboard({
  workspace,
}: {
  workspace: Workspace;
}) {
  const queryClient = useQueryClient();
  const [activeRunId, setActiveRunId] = useState<string | null>(null);
  const [showApplyFills, setShowApplyFills] = useState(false);
  const [showApplyTests, setShowApplyTests] = useState(false);
  const [showAiReview, setShowAiReview] = useState(false);

  const { data: runs } = useQuery({
    queryKey: ["runs", workspace.id],
    queryFn: () => listRuns(workspace.id),
    // Cheap poll so each card's "last run" badge updates even without a
    // live stream open -- the stream itself is the real-time source while
    // a LogPane is actually mounted.
    refetchInterval: 3000,
  });

  const runMutation = useMutation({
    mutationFn: ({ step }: { step: Step }) =>
      startRun(workspace.id, step, undefined),
    onSuccess: (run) => {
      setActiveRunId(run.id);
      queryClient.invalidateQueries({ queryKey: ["runs", workspace.id] });
    },
  });

  const cancelMutation = useMutation({
    mutationFn: (runId: string) => cancelRun(runId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["runs", workspace.id] });
    },
  });

  const runningRun = (runs ?? []).find((r) => r.status === "running");
  const anyRunning = !!runningRun;

  // When each step's output was last written on disk. Covers steps run outside
  // this UI (e.g. Copilot running apply-fills), which leave no run row behind.
  const { data: stepStatus } = useQuery({
    queryKey: ["step-status", workspace.id],
    queryFn: () => getStepStatus(workspace.id),
    refetchInterval: 5000,
  });

  // Per-package AI review state, read from the same validation data the Validation
  // pages use. A review is "done" when its ai-review.json exists on disk.
  const { data: validationRows } = useQuery({
    queryKey: ["artifacts", workspace.id, "validation"],
    queryFn: () => getValidation(workspace.id),
    refetchInterval: 5000,
  });
  const withPacket = (validationRows ?? []).filter((o) => o.stages.packet);
  const reviewedCount = withPacket.filter((o) => o.stages.review).length;
  const allReviewed = withPacket.length > 0 && reviewedCount === withPacket.length;
  const aiReviewStatus =
    withPacket.length === 0
      ? "No packets yet"
      : `${reviewedCount} of ${withPacket.length} packages reviewed`;

  function completedAtFor(step: Step): string | null {
    const key = STEP_STATUS_KEY[step];
    return key && stepStatus ? stepStatus.completedAt[key] : null;
  }

  function lastRunFor(step: Step): Run | undefined {
    return (runs ?? []).find((r) => r.step === step);
  }

  return (
    <div>
      {showApplyTests && (
        <ApplyTestsModal workspace={workspace} onClose={() => setShowApplyTests(false)} />
      )}
      {showApplyFills && (
        <ApplyFillsModal workspace={workspace} onClose={() => setShowApplyFills(false)} />
      )}
      {showAiReview && (
        <AiReviewModal workspace={workspace} onClose={() => setShowAiReview(false)} />
      )}
      <h2 className="mb-1 text-base font-semibold">Pipeline</h2>
      <PageInfo
        summary="Two phases, each card below runs one real ssisx/svk step against this workspace's Input Path, streaming its output live -- click a card's own info icon for what that specific step does."
        details={
          <>
            <p>
              <strong>Phase 1 - Extract/Generate</strong>: <strong>Extract</strong> is always
              first (every other step reads its output), then optionally{" "}
              <strong>Walkthrough</strong>/<strong>Sample Data</strong> for a read-only review,
              then <strong>Generate</strong> (a runnable C# project per package, plus a gap
              report for anything it couldn't translate), <strong>Apply Fills</strong> (if a
              human/AI has written a decision or code fill into the workspace's Fills Path),{" "}
              <strong>Build</strong>, and <strong>Coverage</strong> (runs each generated
              package's unit tests -- Integration tests are excluded).
            </p>
            <p>
              Nothing here mutates the original SSIS packages -- every step only reads the Input
              Path and writes under the workspace's own Output Path.
            </p>
            <p>
              <strong>Phase 2 - Validation</strong> unlocks once Phase 1 has generated output.{" "}
              <strong>Validate</strong> compares the generated output against the validation
              reference, <strong>Evidence Packets</strong> builds the per-package packets an AI
              reviewer reads, <strong>AI Review</strong> records that review, and{" "}
              <strong>Final Summary</strong> runs last, after the AI review.
            </p>
          </>
        }
      />
      {PHASES.map((phase) => {
        // Phase 2 reads Phase 1's generated output, so it is blocked until
        // Generate has run (checked against disk, so a CLI run counts too).
        const blockedReason =
          phase.id === "phase2" && stepStatus && !stepStatus.done.generate
            ? "Run Generate first"
            : null;
        return (
          <section key={phase.id} className="mb-6">
            {/* Title and its group Run button sit together; the blurb goes on its own line. */}
            <div className="mb-1 flex items-center gap-3">
              <h3 className="text-sm font-semibold">{phase.title}</h3>
              {phase.id === "phase2" && (
                <button
                  type="button"
                  onClick={() => runMutation.mutate({ step: "validation" })}
                  disabled={anyRunning || !!blockedReason}
                  aria-label={STEP_LABELS.validation}
                  title={`${STEP_LABELS.validation}: runs Validate and Evidence Packets for every package. Run Final Summary after the AI review.`}
                  className="flex h-6 w-6 shrink-0 cursor-pointer items-center justify-center rounded-full bg-teal-600 text-white shadow-sm hover:bg-teal-700 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <svg viewBox="0 0 20 20" fill="currentColor" className="h-2.5 w-2.5" aria-hidden="true">
                    <path d="M6 4.5v11l9-5.5z" />
                  </svg>
                </button>
              )}
            </div>
            <p className="mb-2 text-xs text-gray-500">{phase.blurb}</p>
            <div className="grid grid-cols-[repeat(auto-fill,minmax(220px,1fr))] gap-3">
              {phase.cards.map((card) => {
                if (card === "ai-review") {
                  return (
                    <ManualCard
                      key={card}
                      title="AI Review"
                      statusText={aiReviewStatus}
                      completedAt={stepStatus?.completedAt.aiReview ?? null}
                      complete={allReviewed}
                      info={{
                        what: "Runs in Claude or Copilot, not in this app. Open the instructions, paste them into your assistant, then press Done here. The assistant judges each package's evidence packet and writes an advisory review beside it.",
                        output: `${workspace.outputPath}\\gate_reports\\<Package>\\ai_context\\<Package>.ai-review.json`,
                        prereq: "Evidence Packets must have run first.",
                        next: "Run Final Summary afterwards, so the review is folded into the rollup.",
                      }}
                      blockedReason={blockedReason}
                      actionLabel={allReviewed ? "Review again" : "Open AI review"}
                      onOpen={() => setShowAiReview(true)}
                    />
                  );
                }
                const step = card;
                const lastRun = lastRunFor(step);
                const isRunningThis = lastRun?.status === "running";
                return (
                  <StepCard
                    key={step}
                    step={step}
                    lastRun={lastRun}
                    completedAt={completedAtFor(step)}
                    disabled={anyRunning}
                    blockedReason={blockedReason}
                    workspaceId={workspace.id}
                    outputPath={workspace.outputPath}
                    onRun={() =>
                      // Apply Fills never runs straight away: the assistant has to
                      // write the fills first, so the popup carries the instructions.
                      step === "apply-fills"
                        ? setShowApplyFills(true)
                        : step === "apply-tests"
                        ? setShowApplyTests(true)
                        : runMutation.mutate({ step })
                    }
                    onCancel={
                      isRunningThis ? () => cancelMutation.mutate(lastRun!.id) : undefined
                    }
                    cancelling={isRunningThis && cancelMutation.isPending}
                    onViewLog={setActiveRunId}
                  />
                );
              })}
            </div>
          </section>
        );
      })}

      {activeRunId && (
        <div className="mt-6">
          <h3 className="mb-2 flex items-center gap-3 text-sm font-semibold">
            Live output
            <button
              className="rounded border border-gray-300 px-2 py-0.5 text-xs hover:bg-gray-50"
              onClick={() => setActiveRunId(null)}
            >
              Close
            </button>
          </h3>
          <LogPane runId={activeRunId} />
        </div>
      )}
    </div>
  );
}

function OpenFolderButton({ workspaceId, step }: { workspaceId: string; step: Step }) {
  const mutation = useMutation({
    mutationFn: () => openStepFolder(workspaceId, step),
  });

  return (
    <div className="relative">
      <button
        type="button"
        onClick={() => mutation.mutate()}
        disabled={mutation.isPending}
        title="Open this step's output folder in File Explorer"
        aria-label={`Open ${STEP_LABELS[step]} output folder`}
        className="flex h-4 w-4 cursor-pointer items-center justify-center rounded-full border border-gray-300 bg-white text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-600 disabled:cursor-not-allowed disabled:opacity-50"
      >
        <NavIcon name="folder" className="h-2.5 w-2.5" />
      </button>
      {mutation.isError && (
        <div className="absolute right-0 top-full z-30 mt-1 w-56 rounded border border-red-200 bg-white p-2 text-xs text-red-700 shadow-lg">
          {mutation.error instanceof Error ? mutation.error.message : String(mutation.error)}
        </div>
      )}
    </div>
  );
}

function StepCard({
  step,
  lastRun,
  completedAt,
  disabled,
  blockedReason,
  workspaceId,
  outputPath,
  onRun,
  onCancel,
  cancelling,
  onViewLog,
}: {
  step: Step;
  lastRun: Run | undefined;
  /** When this step's output was last written on disk, if it has run at all. */
  completedAt: string | null;
  disabled: boolean;
  /** Set when an earlier phase has not run yet -- the card shows it and cannot run. */
  blockedReason: string | null;
  workspaceId: string;
  outputPath: string;
  onRun: () => void;
  onCancel: (() => void) | undefined;
  cancelling: boolean;
  onViewLog: (runId: string) => void;
}) {
  const info = STEP_INFO[step];
  const route = STEP_ROUTE[step]?.(workspaceId);
  // Rerun once the step has run at all -- either via this UI (a run row) or
  // outside it (output already on disk).
  const hasRun = !!lastRun || !!completedAt;
  return (
    <div
      className={`relative flex flex-col gap-2 rounded-lg border border-gray-200 p-3 shadow-sm ${
        step === "apply-fills" || step === "apply-tests" ? "bg-violet-50" : "bg-white"
      }`}
    >
      {route && (
        <Link
          to={route}
          title={`View ${STEP_LABELS[step]} in the app`}
          aria-label={`View ${STEP_LABELS[step]}`}
          className="absolute left-2 top-2 flex h-4 w-4 items-center justify-center rounded-full border border-gray-300 bg-white text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-600"
        >
          <NavIcon name="external" className="h-2.5 w-2.5" />
        </Link>
      )}

      {(step === "apply-fills" || step === "apply-tests") && (
        <AiStepBadge
          offsetClass={route ? "left-8" : "left-2"}
          text={step === "apply-tests"
            ? "AI step: the extra tests are written by an AI assistant (Claude or Copilot), not by this app."
            : "AI step: the fills are written by an AI assistant (Claude or Copilot), not by this app."}
        />
      )}
      <div className="absolute right-2 top-2 flex items-center gap-1">
        <OpenFolderButton workspaceId={workspaceId} step={step} />
        <InfoPopover label={`About ${STEP_LABELS[step]}`}>
          <p className="mb-2">{info.what}</p>
          <p className="mb-1">
            <span className="font-semibold text-gray-900">Output: </span>
            <span className="break-all font-mono text-[11px]">
              {info.output.startsWith("(") ? info.output : `${outputPath}\\${info.output}`}
            </span>
          </p>
          {info.prereq && (
            <p className="mb-1">
              <span className="font-semibold text-gray-900">Needs: </span>
              {info.prereq}
            </p>
          )}
          <p>
            <span className="font-semibold text-gray-900">Next: </span>
            {info.next}
          </p>
        </InfoPopover>
      </div>

      <strong className="px-5 text-center text-sm">{STEP_LABELS[step]}</strong>
      {!lastRun && completedAt ? (
        <div className="text-xs text-gray-500">
          Ran <span className="text-gray-700">{formatDateTime(completedAt)}</span>
        </div>
      ) : lastRun ? (
        <div className="flex items-center gap-2 text-xs">
          <RunBadge status={lastRun.status} />
          <span className="text-gray-500">
            {formatDateTime(lastRun.startedAt)}
          </span>
          {lastRun.statusDetail && (
            <InfoPopover
              label={`Why ${lastRun.status}`}
              className="relative inline-block"
              panelClassName="w-64"
            >
              <p className="mb-2">{lastRun.statusDetail}</p>
              <p className="mb-0 font-mono text-[11px] text-gray-400">
                exit code {lastRun.exitCode ?? "n/a"}
              </p>
              <button
                type="button"
                onClick={() => onViewLog(lastRun.id)}
                className="mt-2 rounded border border-gray-300 bg-white px-2 py-1 text-[11px] font-medium text-gray-700 hover:bg-gray-50"
              >
                View full log
              </button>
            </InfoPopover>
          )}
        </div>
      ) : (
        <div className="text-xs text-gray-400">never run</div>
      )}
      {onCancel ? (
        <button
          className="rounded bg-red-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-red-700 disabled:cursor-not-allowed disabled:opacity-50"
          onClick={onCancel}
          disabled={cancelling}
          title="Stop this run"
        >
          {cancelling ? "Cancelling…" : "✕ Cancel"}
        </button>
      ) : (
        <button
          className={`rounded px-3 py-1.5 text-sm font-medium text-white disabled:cursor-not-allowed disabled:opacity-50 ${
            PHASE2_STEPS.has(step) ? "bg-teal-600 hover:bg-teal-700" : "bg-blue-600 hover:bg-blue-700"
          }`}
          onClick={onRun}
          disabled={disabled || !!blockedReason}
          title={
            blockedReason ??
            (hasRun ? `Run ${STEP_LABELS[step]} again` : `Run ${STEP_LABELS[step]}`)
          }
        >
          {blockedReason ? blockedReason : hasRun ? "Rerun" : "Run"}
        </button>
      )}
    </div>
  );
}

/** A card for a step that happens outside this app. Its button opens the
 * instructions modal instead of starting a run. */
function ManualCard({
  title,
  statusText,
  completedAt,
  complete,
  info,
  blockedReason,
  onOpen,
  actionLabel,
}: {
  title: string;
  statusText: string;
  /** When the last AI review was written on disk, if any. */
  completedAt: string | null;
  /** True once every package with a packet has a review -- drives the green/orange tone. */
  complete: boolean;
  info: { what: string; output: string; prereq: string | null; next: string };
  blockedReason: string | null;
  onOpen: () => void;
  actionLabel: string;
}) {
  return (
    <div className="relative flex flex-col gap-2 rounded-lg border border-dashed border-gray-300 bg-violet-50 p-3 shadow-sm">
      <AiStepBadge text="AI step: the review is done by an AI assistant (Claude or Copilot), not by this app." />
      <div className="absolute right-2 top-2 flex items-center gap-1">
        <InfoPopover label={`About ${title}`}>
          <p className="mb-2">{info.what}</p>
          <p className="mb-1">
            <span className="font-semibold text-gray-900">Output: </span>
            <span className="break-all font-mono text-[11px]">{info.output}</span>
          </p>
          {info.prereq && (
            <p className="mb-1">
              <span className="font-semibold text-gray-900">Needs: </span>
              {info.prereq}
            </p>
          )}
          <p>
            <span className="font-semibold text-gray-900">Next: </span>
            {info.next}
          </p>
        </InfoPopover>
      </div>

      <strong className="px-5 text-center text-sm">{title}</strong>
      {/* Once a review exists, the count moves into the timestamp's tooltip. */}
      {!blockedReason && completedAt ? (
        <div
          title={statusText}
          className={`text-xs ${complete ? "text-green-700" : "text-amber-700"}`}
        >
          Reviewed {formatDateTime(completedAt)}
        </div>
      ) : (
        <div className="text-xs text-gray-500">{blockedReason ?? statusText}</div>
      )}
      <button
        className="mt-auto rounded bg-teal-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-teal-700 disabled:cursor-not-allowed disabled:opacity-50"
        onClick={onOpen}
        disabled={!!blockedReason}
      >
        {actionLabel}
      </button>
    </div>
  );
}

/** Marks a step that is done by an AI assistant (Claude or Copilot), not by this app.
 * Shown as a sparkle in the card's top-left; the explanation is in the tooltip. */
function AiStepBadge({ text, offsetClass = "left-2" }: { text: string; offsetClass?: string }) {
  return (
    <span
      role="img"
      aria-label={text}
      title={text}
      className={`absolute ${offsetClass} top-2 inline-flex h-4 w-4 items-center justify-center rounded-full bg-violet-100 text-violet-700`}
    >
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinejoin="round" className="h-3 w-3" aria-hidden="true">
        <path d="M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z" />
      </svg>
    </span>
  );
}

/** A status icon instead of a word, matching the validation menu's check/dot badges.
 * The word stays in the tooltip and for screen readers. */
function RunBadge({ status }: { status: RunStatus }) {
  const looks: Record<RunStatus, { label: string; bg: string; fg: string; glyph: ReactNode }> = {
    running: {
      label: "Running",
      bg: "bg-blue-100",
      fg: "text-blue-700",
      glyph: <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-current" />,
    },
    success: {
      label: "Succeeded",
      bg: "bg-green-100",
      fg: "text-green-700",
      glyph: (
        <path fillRule="evenodd" d="M16.7 5.3a1 1 0 0 1 0 1.4l-7.5 7.5a1 1 0 0 1-1.4 0L3.3 9.7a1 1 0 1 1 1.4-1.4l4 4 6.8-6.8a1 1 0 0 1 1.4 0z" clipRule="evenodd" />
      ),
    },
    warning: {
      label: "Finished with warnings",
      bg: "bg-amber-100",
      fg: "text-amber-700",
      glyph: <path d="M10 5v6M10 14.2v.3" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" fill="none" />,
    },
    failed: {
      label: "Failed",
      bg: "bg-red-100",
      fg: "text-red-700",
      glyph: <path d="M6.5 6.5l7 7M13.5 6.5l-7 7" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" fill="none" />,
    },
    cancelled: {
      label: "Cancelled",
      bg: "bg-gray-200",
      fg: "text-gray-600",
      glyph: <path d="M6 10h8" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" fill="none" />,
    },
  };
  const look = looks[status];
  return (
    <span
      role="img"
      aria-label={look.label}
      title={look.label}
      className={`inline-flex h-4 w-4 shrink-0 items-center justify-center rounded-full ${look.bg} ${look.fg}`}
    >
      {status === "running" ? (
        look.glyph
      ) : (
        <svg viewBox="0 0 20 20" fill={status === "success" ? "currentColor" : "none"} className="h-3 w-3" aria-hidden="true">
          {look.glyph}
        </svg>
      )}
    </span>
  );
}
