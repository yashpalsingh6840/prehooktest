import fs from "node:fs";
import path from "node:path";

// Read-only views over what SSIS_RewriteValidationTool writes into
// <workspace outputPath>/gate_reports/<Package>/{gates,ai_context,final_review}.
// Every reader returns null/empty for a missing file -- a package that has not
// been validated yet is a normal state, never an error.

export function gateReportsRoot(outputPath: string): string {
  return path.join(outputPath, "gate_reports");
}

export type GateNumber = 1 | 2 | 3;

export type GateFinding = {
  objectName?: string;
  scope?: string;
  verdict?: string;
  detail?: string;
  [key: string]: unknown;
};

export type GateReport = {
  package: string;
  gate: GateNumber;
  counts: Record<string, number>;
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
  stages: { validated: boolean; packet: boolean; review: boolean; finalSummary: boolean };
};

export type ValidationOverview = {
  package: string;
  bucketCounts: {
    toolVerified: number;
    exception: number;
    aiVerified: number;
    humanNeedsReview: number;
  };
  gateCounts: Record<"1" | "2" | "3", Record<string, number> | null>;
  stages: PackageValidation["stages"];
};

// Package names become path segments, so only accept a conservative shape.
export function isValidPackageName(name: string): boolean {
  return /^[A-Za-z0-9_.-]+$/.test(name) && !name.includes("..");
}

function readJson<T>(file: string): T | null {
  try {
    return JSON.parse(fs.readFileSync(file, "utf8")) as T;
  } catch {
    return null;
  }
}

function readText(file: string): string | null {
  try {
    return fs.readFileSync(file, "utf8");
  } catch {
    return null;
  }
}

/** Packages that have at least one gate report on disk, sorted by name. */
export function listValidatedPackages(outputPath: string): string[] {
  const root = gateReportsRoot(outputPath);
  try {
    return fs
      .readdirSync(root, { withFileTypes: true })
      .filter((e) => e.isDirectory() && fs.existsSync(path.join(root, e.name, "gates")))
      .map((e) => e.name)
      .sort();
  } catch {
    return [];
  }
}

function packageFiles(outputPath: string, pkg: string) {
  const dir = path.join(gateReportsRoot(outputPath), pkg);
  return {
    gate: (g: GateNumber) => path.join(dir, "gates", `${pkg}.gate${g}.json`),
    finalSummary: path.join(dir, "final_review", `${pkg}.final-summary.json`),
    aiReview: path.join(dir, "ai_context", `${pkg}.ai-review.json`),
    packet: path.join(dir, "ai_context", `${pkg}.ai-context.md`),
  };
}

export function readPackageValidation(outputPath: string, pkg: string): PackageValidation {
  const files = packageFiles(outputPath, pkg);
  const gates = {
    "1": readJson<GateReport>(files.gate(1)),
    "2": readJson<GateReport>(files.gate(2)),
    "3": readJson<GateReport>(files.gate(3)),
  };
  const finalSummary = readJson<FinalSummary>(files.finalSummary);
  const aiReview = readJson<unknown>(files.aiReview);
  const packetMarkdown = readText(files.packet);
  return {
    package: pkg,
    gates,
    finalSummary,
    aiReview,
    packetMarkdown,
    stages: {
      validated: Object.values(gates).some((g) => g !== null),
      packet: packetMarkdown !== null,
      review: aiReview !== null,
      finalSummary: finalSummary !== null,
    },
  };
}

export function summarizeValidation(outputPath: string): ValidationOverview[] {
  return listValidatedPackages(outputPath).map((pkg) => {
    const v = readPackageValidation(outputPath, pkg);
    const fs_ = v.finalSummary;
    const gateCounts = {
      "1": v.gates["1"]?.counts ?? null,
      "2": v.gates["2"]?.counts ?? null,
      "3": v.gates["3"]?.counts ?? null,
    };
    return {
      package: pkg,
      bucketCounts: {
        toolVerified: fs_?.toolVerified.length ?? 0,
        exception: fs_?.exception.length ?? 0,
        aiVerified: fs_?.aiVerified.length ?? 0,
        humanNeedsReview: fs_?.humanNeedsReview.length ?? 0,
      },
      gateCounts,
      stages: v.stages,
    };
  });
}

/** Per-stage "has any output" and "last written" across every package, for step-status. */
export function validationStatus(outputPath: string): {
  validate: { done: boolean; completedAt: string | null };
  aiContext: { done: boolean; completedAt: string | null };
  aiReview: { done: boolean; completedAt: string | null };
  finalSummary: { done: boolean; completedAt: string | null };
} {
  const pkgs = listValidatedPackages(outputPath);
  const gateFiles = pkgs.flatMap((p) => [1, 2, 3].map((g) => packageFiles(outputPath, p).gate(g as GateNumber)));
  const packetFiles = pkgs.map((p) => packageFiles(outputPath, p).packet);
  const reviewFiles = pkgs.map((p) => packageFiles(outputPath, p).aiReview);
  const summaryFiles = pkgs.map((p) => packageFiles(outputPath, p).finalSummary);

  const at = (files: string[]) => {
    const existing = files.filter((f) => fs.existsSync(f));
    let latest: number | null = null;
    for (const f of existing) {
      const t = fs.statSync(f).mtimeMs;
      if (latest === null || t > latest) latest = t;
    }
    return {
      done: existing.length > 0,
      completedAt: latest === null ? null : new Date(latest).toISOString(),
    };
  };
  return {
    validate: at(gateFiles),
    aiContext: at(packetFiles),
    aiReview: at(reviewFiles),
    finalSummary: at(summaryFiles),
  };
}
