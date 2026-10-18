import type { Step } from "./commands.js";

export type RunStatus = "success" | "warning" | "failed" | "cancelled";

/**
 * Interprets a step's process exit code into a coarse status. A null code
 * (the process was killed/crashed with no exit code at all) is always
 * 'failed'. See CLAUDE.md / the pipeline runner's own doc comment for how
 * each tool's exit codes were established.
 */
export function interpretExitCode(step: Step, code: number | null): RunStatus {
  if (code === null) {
    return "failed";
  }
  if (code === 0) {
    return "success";
  }

  switch (step) {
    case "extract":
      switch (code) {
        case 2:
          return "failed";
        case 1:
        case 3:
          // These codes are only produced by flags this tool never passes
          // (e.g. --fail-under); map defensively to warning rather than
          // claiming certainty about an unreachable path.
          return "warning";
        default:
          return "failed";
      }
    case "generate":
      switch (code) {
        case 3:
        case 98:
          return "warning";
        case 4:
        case 2:
        case 99:
          return "failed";
        default:
          return "failed";
      }
    case "apply-fills":
      switch (code) {
        case 1:
        case 3:
          return "warning";
        case 2:
          return "failed";
        default:
          return "failed";
      }
    case "apply-tests":
      // 1 = a fills package has no generated folder (a mismatch, not fatal); 3 = some files
      // rejected or unverified. Both leave the package buildable, so both are warnings.
      switch (code) {
        case 1:
        case 3:
          return "warning";
        case 2:
          return "failed";
        default:
          return "failed";
      }
    case "walkthrough":
    case "sampledata":
      switch (code) {
        case 2:
        case 99:
          return "failed";
        default:
          return "failed";
      }
    case "build":
      return "failed";
    case "coverage":
      switch (code) {
        case 1:
        case 2:
          // Exit 2 means "no test projects" -- expected for a package that
          // hasn't been fully generated yet, not an error.
          return "warning";
        default:
          return "failed";
      }
    case "validate":
      // The validator exits 1 when any gate finding is Mismatch/LeftOnly/
      // RightOnly/Unrecognized -- real findings, reported, not a broken run.
      // Anything else (argparse 2, a crash) is a hard failure.
      return code === 1 ? "warning" : "failed";
    case "ai-context":
    case "final-summary":
      return "failed";
    case "validation":
    case "all":
      // Run-Pipeline.ps1 already makes its own step-by-step judgment calls
      // and only hard-aborts on a genuine failure, so a nonzero exit here
      // is informative rather than necessarily catastrophic. The validation
      // run is a chain whose per-command statuses are aggregated in runner.ts.
      return "warning";
  }
}

/**
 * A human-readable reason for a non-success status, shown in the UI next to
 * a "warning"/"failed" badge so a user doesn't have to open the log just to
 * learn what a bare exit code means. Returns null for 'success'/'cancelled'
 * -- nothing to explain there (the DB-persisted RunStatus never actually
 * stores 'running'; that's only an in-flight UI state). Mirrors
 * interpretExitCode's own per-code comments above; keep the two in sync if
 * either changes.
 */
export function describeStatus(
  step: Step,
  status: RunStatus,
  code: number | null,
): string | null {
  if (status === "success" || status === "cancelled") {
    return null;
  }
  if (code === null) {
    return "The process exited with no code at all -- it was likely killed or crashed before it could report a result. Check the log for the last line printed.";
  }

  switch (step) {
    case "extract":
      if (code === 1 || code === 3) {
        return `Exit code ${code} is only produced by a flag this tool never passes (e.g. --fail-under) -- unexpected here. Check the log for what actually happened.`;
      }
      return `Extract reported a hard failure (exit code ${code}) -- check the log for the error.`;
    case "generate":
      switch (code) {
        case 3:
          return "At least one package still has unresolved AI work packets (Tier-1/Tier-2 gaps) blocking it from being fully generatable -- see the Gaps tab.";
        case 98:
          return "Generate finished, but at least one package hit a non-blocking advisory gap -- see the Gaps tab.";
        case 4:
          return "At least one package could not be fully generated (a blocking Tier-3 gap ssisx can't resolve on its own) -- see the Gaps tab.";
        case 2:
          return "Generate was given an invalid argument or input path -- check the log.";
        case 99:
          return "Generate crashed unexpectedly -- check the log for the stack trace.";
        default:
          return `Generate reported a hard failure (exit code ${code}) -- check the log for the error.`;
      }
    case "apply-tests":
      switch (code) {
        case 1:
          return "Some MoreTests files belong to a package that was not generated -- see tests-applied.json.";
        case 3:
          return "Some extra tests were REJECTED (the test project would not compile with them) or could not be verified -- see the Applied Tests page for the errors.";
        case 2:
          return "Apply Tests failed outright -- check the log.";
        default:
          return `Apply Tests reported a hard failure (exit code ${code}) -- check the log.`;
      }
    case "apply-fills":
      switch (code) {
        case 1:
          return "At least one fill could not be applied (a stale evidence hash, or no matching gap) -- see the Fills tab for which one.";
        case 3:
          return "Fills were applied where possible, but at least one seam is still unfilled -- see the Fills/Gaps tabs.";
        case 2:
          return "Apply Fills failed outright -- an unknown package, or the workspace's Fills Path is missing/unreadable. Check the log.";
        default:
          return `Apply Fills reported a hard failure (exit code ${code}) -- check the log for the error.`;
      }
    case "walkthrough":
    case "sampledata":
      return `${step === "walkthrough" ? "svk walkthrough" : "svk sampledata"} failed (exit code ${code}) -- check the log for the error.`;
    case "build":
      return "The generated solution did not build cleanly -- check the log for the compiler errors.";
    case "coverage":
      switch (code) {
        case 1:
          return "At least one generated test failed -- check the log, or coverage-report.md, for which one.";
        case 2:
          return "No test projects exist yet to measure coverage against -- expected until at least one package is fully generated (no blocking gaps) and built.";
        default:
          return `Coverage reported a hard failure (exit code ${code}) -- check the log for the error.`;
      }
    case "validate":
      return code === 1
        ? "The validator reported at least one finding it could not confirm as a match (Mismatch, LeftOnly, RightOnly or Unrecognized) -- open the package's Validation page to see which."
        : `The validator failed (exit code ${code}) -- check the log for the error.`;
    case "ai-context":
      return `Building the Tier-2 evidence packet failed (exit code ${code}) -- check the log.`;
    case "final-summary":
      return `Building the final summary failed (exit code ${code}) -- was Validate run first for this package? Check the log.`;
    case "validation":
      return "At least one validation step reported findings or failed -- open each package's Validation page, or the log, for detail.";
    case "all":
      return "Run All completed, but at least one step along the way reported something other than a clean success -- open that step's own card for its own status and detail.";
    default:
      return `Exit code ${code}.`;
  }
}
