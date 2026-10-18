export type Framework = "net8.0" | "net10.0";

export type Workspace = {
  id: string;
  name: string;
  inputPath: string;
  outputPath: string;
  fillsPath: string;
  framework: Framework;
  createdAt: string;
  updatedAt: string;
  /** When any step last started in this workspace; null if it never ran. */
  lastRunAt?: string | null;
};

export type WorkspaceInput = {
  name: string;
  inputPath: string;
  outputPath: string;
  fillsPath: string;
  framework: Framework;
};

async function handle<T>(res: Response): Promise<T> {
  if (!res.ok) {
    let message = `HTTP ${res.status}`;
    try {
      const body = await res.json();
      if (body?.error) {
        message =
          typeof body.error === "string"
            ? body.error
            : JSON.stringify(body.error);
      }
    } catch {
      // ignore, fall back to the plain status message
    }
    throw new Error(message);
  }
  if (res.status === 204) {
    return undefined as T;
  }
  return res.json() as Promise<T>;
}

export function listWorkspaces(): Promise<Workspace[]> {
  return fetch("/api/workspaces").then((r) => handle<Workspace[]>(r));
}

export function getWorkspace(id: string): Promise<Workspace> {
  return fetch(`/api/workspaces/${id}`).then((r) => handle<Workspace>(r));
}

export function createWorkspace(data: WorkspaceInput): Promise<Workspace> {
  return fetch("/api/workspaces", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(data),
  }).then((r) => handle<Workspace>(r));
}

export function updateWorkspace(
  id: string,
  data: WorkspaceInput,
): Promise<Workspace> {
  return fetch(`/api/workspaces/${id}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(data),
  }).then((r) => handle<Workspace>(r));
}

/** Removes the workspace entry. With `deleteOutput`, also deletes its output
 * folder from disk -- the server never deletes the input or fills folders. */
export function deleteWorkspace(id: string, deleteOutput = false): Promise<void> {
  const qs = deleteOutput ? "?deleteOutput=true" : "";
  return fetch(`/api/workspaces/${id}${qs}`, { method: "DELETE" }).then((r) =>
    handle<void>(r),
  );
}

/** Opens the native folder dialog on the server's desktop. `path` is null when
 * the user cancelled. `initialPath` is where the dialog starts, if given. */
export function pickFolder(initialPath?: string): Promise<{ path: string | null }> {
  return fetch("/api/fs/pick-folder", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ initialPath: initialPath ?? "" }),
  }).then((r) => handle<{ path: string | null }>(r));
}

export const STEPS = [
  "extract",
  "walkthrough",
  "sampledata",
  "generate",
  "apply-fills",
  "apply-tests",
  "build",
  "coverage",
  "validate",
  "ai-context",
  "final-summary",
  "validation",
  "all",
] as const;

export type Step = (typeof STEPS)[number];
export type RunStatus = "running" | "success" | "warning" | "failed" | "cancelled";

export type Run = {
  id: string;
  workspaceId: string;
  step: Step;
  package: string | null;
  status: RunStatus;
  exitCode: number | null;
  startedAt: string;
  finishedAt: string | null;
  logPath: string;
  /** Human-readable reason for a non-success status, computed server-side
   * from (step, exitCode) -- null for success/running/cancelled. */
  statusDetail: string | null;
};

export function startRun(
  workspaceId: string,
  step: Step,
  pkg?: string,
): Promise<Run> {
  return fetch("/api/runs", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ workspaceId, step, package: pkg }),
  }).then((r) => handle<Run>(r));
}

export function listRuns(workspaceId: string): Promise<Run[]> {
  return fetch(`/api/runs?workspaceId=${encodeURIComponent(workspaceId)}`).then(
    (r) => handle<Run[]>(r),
  );
}

export function getRun(id: string): Promise<Run> {
  return fetch(`/api/runs/${id}`).then((r) => handle<Run>(r));
}

export function cancelRun(id: string): Promise<Run> {
  return fetch(`/api/runs/${id}/cancel`, { method: "POST" }).then((r) => handle<Run>(r));
}

/** Opens a step's output folder in Windows Explorer, on the machine running
 * this server (a local, single-developer tool -- see Tools/SsisUi/README.md). */
export function openStepFolder(workspaceId: string, step: Step): Promise<{ ok: boolean; path: string }> {
  return fetch(`/api/workspaces/${encodeURIComponent(workspaceId)}/system/open-folder`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ step }),
  }).then((r) => handle<{ ok: boolean; path: string }>(r));
}

/** Opens an arbitrary folder (restricted server-side to one of this
 * workspace's own configured roots) in Windows Explorer. */
export function openPath(workspaceId: string, path: string): Promise<{ ok: boolean; path: string }> {
  return fetch(`/api/workspaces/${encodeURIComponent(workspaceId)}/system/open-path`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ path }),
  }).then((r) => handle<{ ok: boolean; path: string }>(r));
}

/** Opens one generated file (relative to `<out>/gen/generate/`, same shape
 * getGeneratedFileContent takes) in VS Code via its `code` CLI. */
export function openFileInVsCode(
  workspaceId: string,
  relPath: string,
): Promise<{ ok: boolean; path: string }> {
  return fetch(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/system/open-file/${relPath
      .split("/")
      .map(encodeURIComponent)
      .join("/")}`,
    { method: "POST" },
  ).then((r) => handle<{ ok: boolean; path: string }>(r));
}

// --- Phase 3: read-only artifact viewers ----------------------------------

export type GapKind =
  | "Unclassified"
  | "ScriptTask"
  | "ScriptComponentColumn"
  | "LookupJoinKey"
  | "ConditionalConstraint"
  | "EncryptedConnectionManagerSecret"
  | "TestOracle"
  | "LocalFileSourceData";

export type GapTier =
  | "Advisory"
  | "MissingDatum"
  | "MissingLogic"
  | "MissingToolSupport";

export type GapSpec = {
  GapId: string;
  Package: string;
  Kind: GapKind;
  Tier: GapTier;
  Location: string;
  Reason: string;
  IsBlocking: boolean;
  PacketPath: string | null;
  EvidenceSha256: string | null;
  EvidenceRefId: string | null;
  ExpectedFileName: string | null;
  TransformClassName: string | null;
};

export type GapsResult = { available: boolean; gaps: GapSpec[] };

export type FillStatus = "Applied" | "Unattributed" | "Stale" | "Orphaned";

export type FillRecordSpec = {
  Package: string;
  FileName: string;
  Seam: string;
  GapId: string | null;
  Status: FillStatus;
  Author: string | null;
  Date: string | null;
  RecordedEvidenceSha256: string | null;
  CurrentEvidenceSha256: string | null;
  ConformanceRuleId: string | null;
  Synthetic: boolean;
};

export type FillsAppliedResult = { available: boolean; fills: FillRecordSpec[] };

export type MarkdownResult = { available: boolean; markdown: string };

export type WalkthroughResult = {
  available: boolean;
  packages: { package: string; markdown: string }[];
};

function artifactUrl(workspaceId: string, suffix: string) {
  return `/api/workspaces/${encodeURIComponent(workspaceId)}/artifacts${suffix}`;
}

export function getGaps(workspaceId: string): Promise<GapsResult> {
  return fetch(artifactUrl(workspaceId, "/gaps")).then((r) => handle<GapsResult>(r));
}

export function getGapPacket(
  workspaceId: string,
  gapId: string,
): Promise<{ markdown: string }> {
  return fetch(artifactUrl(workspaceId, `/gaps/${encodeURIComponent(gapId)}/packet`)).then(
    (r) => handle<{ markdown: string }>(r),
  );
}

export function getFillsStatus(workspaceId: string): Promise<FillsAppliedResult> {
  return fetch(artifactUrl(workspaceId, "/fills-status")).then((r) =>
    handle<FillsAppliedResult>(r),
  );
}

/** One row of tests-applied.json: what apply-tests did with one MoreTests file. */
export type MoreTestStatus = "Applied" | "Rejected" | "Unverified";

export type MoreTestRecordSpec = {
  Package: string;
  FileName: string;
  Status: MoreTestStatus;
  Attributed: boolean;
  Author: string | null;
  Date: string | null;
  Targets: string | null;
  CompilerErrors: string[] | null;
};

export type TestsAppliedResult = { available: boolean; tests: MoreTestRecordSpec[] };

export function getTestsApplied(workspaceId: string): Promise<TestsAppliedResult> {
  return fetch(artifactUrl(workspaceId, "/tests-applied")).then((r) =>
    handle<TestsAppliedResult>(r),
  );
}

/** Which pipeline steps have produced their output, for hiding menus until
 * their step has run. */
export type StepKey =
  | "extract"
  | "walkthrough"
  | "sampledata"
  | "generate"
  | "applyFills"
  | "applyTests"
  | "coverage"
  | "validate"
  | "aiContext"
  | "aiReview"
  | "finalSummary";

export type StepStatus = {
  /** Whether each step has produced its output. */
  done: Record<StepKey, boolean>;
  /** When each step's output was last written (ISO), or null if it hasn't run. */
  completedAt: Record<StepKey, string | null>;
};

export function getStepStatus(workspaceId: string): Promise<StepStatus> {
  return fetch(artifactUrl(workspaceId, "/step-status")).then((r) => handle<StepStatus>(r));
}

// ---- Validation (SSIS_RewriteValidationTool output, read-only) ----

export type VerdictCounts = Record<string, number>;

export type ValidationStages = {
  validated: boolean;
  packet: boolean;
  review: boolean;
  finalSummary: boolean;
};

export type ValidationOverview = {
  package: string;
  bucketCounts: {
    toolVerified: number;
    exception: number;
    aiVerified: number;
    humanNeedsReview: number;
  };
  gateCounts: Record<"1" | "2" | "3", VerdictCounts | null>;
  stages: ValidationStages;
};

export type GateFinding = {
  objectName?: string;
  scope?: string;
  verdict?: string;
  detail?: string;
  [key: string]: unknown;
};

export type GateReport = {
  package: string;
  gate: 1 | 2 | 3;
  counts: VerdictCounts;
  findings: GateFinding[];
};

export type SummaryItem = {
  gate: number | string;
  scope?: string;
  objectName?: string;
  componentPath?: string | null;
  tier1Verdict?: string;
  tier2Verdict?: string | null;
  tier2Rationale?: string | null;
  reason?: string;
};

export type FinalSummary = {
  package: string;
  toolVerified: SummaryItem[];
  exception: SummaryItem[];
  aiVerified: SummaryItem[];
  humanNeedsReview: SummaryItem[];
};

export type PackageValidation = {
  package: string;
  gates: Record<"1" | "2" | "3", GateReport | null>;
  finalSummary: FinalSummary | null;
  aiReview: unknown | null;
  packetMarkdown: string | null;
  stages: ValidationStages;
};

export function getValidation(workspaceId: string): Promise<ValidationOverview[]> {
  return fetch(artifactUrl(workspaceId, "/validation")).then((r) =>
    handle<ValidationOverview[]>(r),
  );
}

export function getPackageValidation(
  workspaceId: string,
  pkg: string,
): Promise<PackageValidation> {
  return fetch(artifactUrl(workspaceId, `/validation/${encodeURIComponent(pkg)}`)).then((r) =>
    handle<PackageValidation>(r),
  );
}

export type OutputFileName ="gaps" | "fills-applied" | "apply-fills-log";

export type OutputFileResult = {
  available: boolean;
  markdown: string;
  path: string;
  kind: "markdown" | "json" | "text";
};

/** One of the generate/apply-fills output files, read raw from the gen folder. */
export function revealOutputFile(workspaceId: string, name: OutputFileName): Promise<{ ok: true; path: string }> {
  return fetch(artifactUrl(workspaceId, `/output-file/${name}/reveal`), { method: "POST" }).then((r) =>
    handle<{ ok: true; path: string }>(r),
  );
}

export function getOutputFile(
  workspaceId: string,
  name: OutputFileName,
): Promise<OutputFileResult> {
  return fetch(artifactUrl(workspaceId, `/output-file/${name}`)).then((r) =>
    handle<OutputFileResult>(r),
  );
}

export type ApplyFillsPackage ={ name: string; tier2: number; tier1: number; testsAndData: number };

export type ApplyFillsInstructions = {
  command: string;
  allPackages: ApplyFillsPackage[];
  packages: ApplyFillsPackage[];
  body: string;
};

/** `packages` scopes the instruction body to those names; omitted = all. */
export function getApplyFillsInstructions(
  workspaceId: string,
  packages?: string[],
): Promise<ApplyFillsInstructions> {
  const qs = packages ? `?packages=${encodeURIComponent(packages.join(","))}` : "";
  return fetch(artifactUrl(workspaceId, `/apply-fills-instructions${qs}`)).then((r) =>
    handle<ApplyFillsInstructions>(r),
  );
}

export type ApplyTestsPackageSummary = { name: string; existing: number };

export type ApplyTestsInstructions = {
  command: string;
  allPackages: ApplyTestsPackageSummary[];
  packages: ApplyTestsPackageSummary[];
  body: string;
};

export function getApplyTestsInstructions(
  workspaceId: string,
  packages?: string[],
): Promise<ApplyTestsInstructions> {
  const qs = packages ? `?packages=${encodeURIComponent(packages.join(","))}` : "";
  return fetch(artifactUrl(workspaceId, `/apply-tests-instructions${qs}`)).then((r) =>
    handle<ApplyTestsInstructions>(r),
  );
}

export function getGenerateReport(workspaceId: string): Promise<MarkdownResult> {
  return fetch(artifactUrl(workspaceId, "/report")).then((r) => handle<MarkdownResult>(r));
}

export type GeneratePackageRow = {
  package: string;
  filesWritten: number;
  starterTests: number;
  gaps: number;
};
export type CrashedPackageRow = { package: string; diagnosticPath: string };
export type Tier1DecisionRow = { package: string; status: string; gapId: string; detail: string };
export type FailedToLoadRow = { path: string; kind: string; reason: string };

export type GenerateSummary = {
  available: boolean;
  totalFiles: number;
  totalPackages: number;
  totalGaps: number;
  crashedCount: number;
  packages: GeneratePackageRow[];
  crashedPackages: CrashedPackageRow[];
  tier1Decisions: Tier1DecisionRow[];
  failedToLoad: FailedToLoadRow[];
};

export function getGenerateSummary(workspaceId: string): Promise<GenerateSummary> {
  return fetch(artifactUrl(workspaceId, "/generate-summary")).then((r) =>
    handle<GenerateSummary>(r),
  );
}

export function getGeneratedFiles(
  workspaceId: string,
  pkg: string,
): Promise<{ available: boolean; productionFiles: string[]; testFiles: string[] }> {
  return fetch(artifactUrl(workspaceId, `/generate/${encodeURIComponent(pkg)}/files`)).then((r) =>
    handle<{ available: boolean; productionFiles: string[]; testFiles: string[] }>(r),
  );
}

/** `relPath` is a path relative to `<out>/gen/generate/` as returned by
 * getGeneratedFiles, e.g. "SyntheticAggregate/Mapping/Foo.cs". */
export function getGeneratedFileContent(
  workspaceId: string,
  relPath: string,
): Promise<{ content: string }> {
  return fetch(
    artifactUrl(
      workspaceId,
      `/generate-file/${relPath.split("/").map(encodeURIComponent).join("/")}`,
    ),
  ).then((r) => handle<{ content: string }>(r));
}

export function getHowToFillGaps(workspaceId: string): Promise<MarkdownResult> {
  return fetch(artifactUrl(workspaceId, "/how-to-fill-gaps")).then((r) =>
    handle<MarkdownResult>(r),
  );
}

export function getCoverageReport(workspaceId: string): Promise<MarkdownResult> {
  return fetch(artifactUrl(workspaceId, "/coverage")).then((r) => handle<MarkdownResult>(r));
}

export function getLoadFailures(workspaceId: string): Promise<MarkdownResult> {
  return fetch(artifactUrl(workspaceId, "/load-failures")).then((r) =>
    handle<MarkdownResult>(r),
  );
}

export function getPortfolioDigest(workspaceId: string): Promise<MarkdownResult> {
  return fetch(artifactUrl(workspaceId, "/portfolio-digest")).then((r) =>
    handle<MarkdownResult>(r),
  );
}

export function getWalkthrough(workspaceId: string): Promise<WalkthroughResult> {
  return fetch(artifactUrl(workspaceId, "/walkthrough")).then((r) =>
    handle<WalkthroughResult>(r),
  );
}

export type WalkthroughStatus = "NotValidated" | "Validated" | "Blocked";

export type WalkthroughClaim = {
  RuleId: string;
  Status: WalkthroughStatus;
  Reviewer: string | null;
  ReviewedDate: string | null;
  Note: string | null;
};

export type ClaimsResult = { available: boolean; claims: WalkthroughClaim[] };

export function getWalkthroughClaims(
  workspaceId: string,
  packageName: string,
): Promise<ClaimsResult> {
  return fetch(
    artifactUrl(workspaceId, `/walkthrough/${encodeURIComponent(packageName)}/claims`),
  ).then((r) => handle<ClaimsResult>(r));
}

export function updateWalkthroughClaim(
  workspaceId: string,
  packageName: string,
  update: { ruleId: string; status: WalkthroughStatus; reviewer: string; note: string },
): Promise<ClaimsResult> {
  return fetch(
    artifactUrl(workspaceId, `/walkthrough/${encodeURIComponent(packageName)}/claims`),
    {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(update),
    },
  ).then((r) => handle<ClaimsResult>(r));
}

// --- Phase 4: authoring a fill -------------------------------------------

function fillsUrl(workspaceId: string, relPath: string) {
  return `/api/workspaces/${encodeURIComponent(workspaceId)}/fills/${relPath
    .split("/")
    .map(encodeURIComponent)
    .join("/")}`;
}

export function suggestFillPath(
  workspaceId: string,
  gapId: string,
): Promise<{ path: string }> {
  return fetch(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/fills/suggest?gapId=${encodeURIComponent(gapId)}`,
  ).then((r) => handle<{ path: string }>(r));
}

/** Returns null (not an error) when the file doesn't exist yet -- a brand-new, unsaved fill. */
export async function getFillContent(
  workspaceId: string,
  relPath: string,
): Promise<string | null> {
  const res = await fetch(fillsUrl(workspaceId, relPath));
  if (res.status === 404) return null;
  const body = await handle<{ content: string }>(res);
  return body.content;
}

export function putFillContent(
  workspaceId: string,
  relPath: string,
  content: string,
): Promise<{ ok: true }> {
  return fetch(fillsUrl(workspaceId, relPath), {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ content }),
  }).then((r) => handle<{ ok: true }>(r));
}

// --- Settings --------------------------------------------------------------

export type ResolutionMode = "override" | "published" | "dotnet-run" | "venv";
export type ResolutionInfo = { mode: ResolutionMode; path: string };

export type SettingsResult = {
  ssisxExePath: string | null;
  svkExePath: string | null;
  validatorExePath: string | null;
  resolution: { ssisx: ResolutionInfo; svk: ResolutionInfo; validator: ResolutionInfo };
};

export function getSettings(): Promise<SettingsResult> {
  return fetch("/api/settings").then((r) => handle<SettingsResult>(r));
}

export function updateSettings(data: {
  ssisxExePath?: string | null;
  svkExePath?: string | null;
  validatorExePath?: string | null;
}): Promise<SettingsResult> {
  return fetch("/api/settings", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(data),
  }).then((r) => handle<SettingsResult>(r));
}

// --- Phase 5: package component inventory + sample data browser ----------

export type ComponentRole = "Source" | "Destination" | "Transform" | "Other";

export type PackageComponent = {
  name: string;
  type: string;
  role: ComponentRole;
  detail: string;
  connection: string | null;
  inputs: string[];
  outputs: string[];
  payload: unknown;
};

export type PackageDataFlow = {
  taskName: string;
  components: PackageComponent[];
  edges: { from: string; to: string }[];
};

export type PackageConnectionManager = {
  name: string;
  type: string;
  server: string | null;
  database: string | null;
  provider: string | null;
  filePath: string | null;
};

export type PackageSummary = {
  package: string;
  connectionManagers: PackageConnectionManager[];
  dataFlows: PackageDataFlow[];
  roleCounts: Record<string, number>;
};

export function getExtractedPackages(workspaceId: string): Promise<{ packages: string[] }> {
  return fetch(artifactUrl(workspaceId, "/packages")).then((r) =>
    handle<{ packages: string[] }>(r),
  );
}

export function getPackageSummary(workspaceId: string, pkg: string): Promise<PackageSummary> {
  return fetch(artifactUrl(workspaceId, `/packages/${encodeURIComponent(pkg)}`)).then((r) =>
    handle<PackageSummary>(r),
  );
}

export type SampleDataFile = { name: string; kind: string; sizeBytes: number };

export function getSampleDataFiles(
  workspaceId: string,
  pkg: string,
): Promise<{ available: boolean; files: SampleDataFile[]; path: string }> {
  return fetch(artifactUrl(workspaceId, `/sampledata/${encodeURIComponent(pkg)}`)).then((r) =>
    handle<{ available: boolean; files: SampleDataFile[]; path: string }>(r),
  );
}

export function getRawPackageSpec(
  workspaceId: string,
  pkg: string,
): Promise<{ json: unknown; path: string }> {
  return fetch(artifactUrl(workspaceId, `/packages/${encodeURIComponent(pkg)}/raw`)).then((r) =>
    handle<{ json: unknown; path: string }>(r),
  );
}

export type PortfolioStats = {
  gapsAvailable: boolean;
  totalGaps: number;
  blockingGaps: number;
  advisoryGaps: number;
  gapsByTier: Record<string, number>;
  gapsByKind: Record<string, number>;
  gapsByPackage: Record<string, number>;
  packageCount: number;
  roleCounts: Record<string, number>;
};

export function getPortfolioStats(workspaceId: string): Promise<PortfolioStats> {
  return fetch(artifactUrl(workspaceId, "/portfolio-stats")).then((r) =>
    handle<PortfolioStats>(r),
  );
}

export function getSampleDataFileContent(
  workspaceId: string,
  pkg: string,
  fileName: string,
): Promise<{ binary: boolean; content?: string; sizeBytes?: number }> {
  return fetch(
    artifactUrl(
      workspaceId,
      `/sampledata/${encodeURIComponent(pkg)}/file/${encodeURIComponent(fileName)}`,
    ),
  ).then((r) => handle<{ binary: boolean; content?: string; sizeBytes?: number }>(r));
}

export function sampleDataDownloadUrl(workspaceId: string, pkg: string, fileName: string): string {
  return artifactUrl(
    workspaceId,
    `/sampledata/${encodeURIComponent(pkg)}/download/${encodeURIComponent(fileName)}`,
  );
}

export type DecisionFile = Record<string, Record<string, unknown>>;

/** Returns {} when <Package>.decisions.json doesn't exist yet. */
export async function getDecisionsFile(
  workspaceId: string,
  packageName: string,
): Promise<DecisionFile> {
  const text = await getFillContent(workspaceId, `${packageName}.decisions.json`);
  if (text === null) return {};
  try {
    return JSON.parse(text) as DecisionFile;
  } catch {
    return {};
  }
}

export function putDecisionsFile(
  workspaceId: string,
  packageName: string,
  decisions: DecisionFile,
): Promise<{ ok: true }> {
  return putFillContent(
    workspaceId,
    `${packageName}.decisions.json`,
    JSON.stringify(decisions, null, 2),
  );
}
