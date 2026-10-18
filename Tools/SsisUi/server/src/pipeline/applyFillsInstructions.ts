import path from "node:path";
import type { Workspace } from "../routes/workspaces.js";
import type { GapSpec } from "../parsers/gaps.js";
import {
  buildAiInstructions,
  type AiInstructions,
  type InstructionFrame,
} from "./aiInstructions.js";

/**
 * Builds the instruction body for the Apply Fills popup. The header, paths, scope,
 * and command line come from the shared aiInstructions.ts; the rules and steps
 * below are this feature's own.
 */

export type ApplyFillsPackageSummary = {
  name: string;
  tier2: number;
  tier1: number;
  testsAndData: number;
};

export type ApplyFillsInstructions = AiInstructions<ApplyFillsPackageSummary>;

// Tier-1 (MissingDatum) and Tier-2 (MissingLogic) gaps are the ones a human or
// assistant can close by writing a fill. Advisory gaps need no fill at all.
function summarize(gaps: GapSpec[]): ApplyFillsPackageSummary[] {
  const byPackage = new Map<string, ApplyFillsPackageSummary>();
  for (const gap of gaps) {
    if (gap.Tier === "Advisory" || gap.Tier === "MissingToolSupport") continue;
    let row = byPackage.get(gap.Package);
    if (!row) {
      row = { name: gap.Package, tier2: 0, tier1: 0, testsAndData: 0 };
      byPackage.set(gap.Package, row);
    }
    if (gap.Kind === "TestOracle" || gap.Kind === "LocalFileSourceData") {
      row.testsAndData++;
    } else if (gap.Tier === "MissingLogic") {
      row.tier2++;
    } else {
      row.tier1++;
    }
  }
  return [...byPackage.values()].sort((a, b) => a.name.localeCompare(b.name));
}

const APPLY_FILLS_FRAME: InstructionFrame<ApplyFillsPackageSummary> = {
  title: "Apply fills",
  noPackagesLine: "- No open fillable gaps. Run Apply Fills to copy any fills already on disk, then stop.",
  packageLine: (p) =>
    `- ${p.name}: ${p.tier2} Tier-2 seam(s), ${p.tier1} Tier-1 decision(s), ${p.testsAndData} test/data gap(s)`,
  rules: ({ ws, genOut }) => [
    `1. Work packets are at ${path.join(genOut, "gaps")}\\<Package>\\<GapId>.md. Read one packet per script component or task, not one per gap. Read only its "What you need to produce" section.`,
    `2. Never overwrite a fill that already exists under ${ws.fillsPath}. Read it first; if it is sound, leave it alone.`,
    `3. Tier-2 (script ports): one file per generated class, ${ws.fillsPath}\\<Package>\\<ClassName>.Fills.cs. It must be a second partial part of the generated class, using the exact namespace and class name from the packet.`,
    "4. Above each seam, write one provenance line:",
    "   // ssisx-fill: GapId=<from packet> Author=<name> Date=<yyyy-mm-dd> EvidenceSha256=<from packet>",
    "   Copy GapId and EvidenceSha256 verbatim from the packet. Never compute or guess them.",
    `5. Tier-1 decisions go in ${ws.fillsPath}\\<Package>.decisions.json, in the shape the packet specifies.`,
    `6. Test oracles go in ${ws.fillsPath}\\<Package>\\Tests\\. Local sample data goes in ${ws.fillsPath}\\<Package>\\TestData\\, named exactly as the packet's ExpectedFileName.`,
    "7. Never invent connection strings, seed data, or input files to make something runnable. If a port cannot be reproduced against the packet's abstractions, say so instead of substituting.",
    "8. Do not commit anything. Leave the files for the user to review.",
  ],
  steps: ({ ws, genOut, applyCommand }) => [
    "1. Write or update the fills following the rules above.",
    `2. Apply them: ${applyCommand}`,
    `3. Optionally build to confirm: dotnet build ${path.join(genOut, "generate", "Generated.slnx")} -c Release`,
    `4. Report: which seams applied; any Stale, Orphaned, or Unattributed entries in ${path.join(genOut, "fills-applied.json")}; every non-mechanical port decision for human review; and any Tier-1 gaps still open.`,
  ],
};

/**
 * `selected` scopes the body to those package names. Omitted (or every package
 * selected) means the whole-workspace body.
 */
export function buildApplyFillsInstructions(
  ws: Workspace,
  genOut: string,
  gaps: GapSpec[],
  selected?: string[],
): ApplyFillsInstructions {
  return buildAiInstructions(ws, genOut, "apply-fills", summarize(gaps), selected, APPLY_FILLS_FRAME);
}
