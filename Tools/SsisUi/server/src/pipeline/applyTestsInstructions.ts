import fs from "node:fs";
import path from "node:path";
import type { Workspace } from "../routes/workspaces.js";
import {
  buildAiInstructions,
  type AiInstructions,
  type InstructionFrame,
} from "./aiInstructions.js";

/**
 * Builds the instruction body for the Apply Tests popup. The header, paths, scope,
 * and command line come from the shared aiInstructions.ts; the rules and steps
 * below are this feature's own.
 */

export type ApplyTestsPackageSummary = {
  name: string;
  /** How many MoreTests files already sit under this package's fills folder. */
  existing: number;
};

export type ApplyTestsInstructions = AiInstructions<ApplyTestsPackageSummary>;

/** Packages that have a generated `<Package>.Tests` project, i.e. the only ones a
 * "more test" can join. A package generated with --skip-tests has none. */
function listTestablePackages(ws: Workspace, genOut: string): ApplyTestsPackageSummary[] {
  const generateDir = path.join(genOut, "generate");
  let entries: string[];
  try {
    entries = fs.readdirSync(generateDir);
  } catch {
    return [];
  }
  return entries
    .filter((n) => n.endsWith(".Tests") && fs.statSync(path.join(generateDir, n)).isDirectory())
    .map((n) => n.slice(0, -".Tests".length))
    .sort((a, b) => a.localeCompare(b))
    .map((name) => ({ name, existing: countMoreTests(ws, name) }));
}

function countMoreTests(ws: Workspace, packageName: string): number {
  try {
    return fs
      .readdirSync(path.join(ws.fillsPath, packageName, "MoreTests"))
      .filter((n) => n.endsWith(".cs")).length;
  } catch {
    return 0;
  }
}

const APPLY_TESTS_FRAME: InstructionFrame<ApplyTestsPackageSummary> = {
  title: "Write extra tests",
  noPackagesLine: "- No generated package has a test project yet. Run Generate first, then stop.",
  packageLine: (p) => `- ${p.name}: ${p.existing} MoreTests file(s) already in the fills folder`,
  rules: ({ ws, genOut }) => [
    `1. For each package, read only the "Test coverage notes" section of ${path.join(genOut, "generate", "<Package>", "README.md")}. It says what the starter tests already assert and what they do not. Do not open the generated test files or the production code unless a note is unclear.`,
    `2. Each new test is one file: ${ws.fillsPath}\\<Package>\\MoreTests\\<Name>MoreTests.cs. Never write into ${ws.fillsPath}\\<Package>\\Tests\\ -- that folder belongs to the TEST-ORACLE mechanism and is matched against gaps.json.`,
    "3. Put this provenance comment on the first line of every file, filled in:",
    "   // ssisx-more-test: Author=<name> Date=<yyyy-mm-dd> Targets=<Class.Method>",
    "4. Use only types and member names that exist in the generated code. Check the README or the generated file before referencing one. A file that does not compile is rejected and removed from the generated output.",
    "5. Never invent connection strings, seed data, or input files. A test that needs a real database or real files is not written here -- say so instead.",
    "6. Do not edit anything under the generated output folder. Do not commit anything. Leave the files for the user to review.",
  ],
  steps: ({ genOut, applyCommand }) => [
    "1. Before writing any file, list the candidate tests you plan for each package (file name and what each one asserts), and wait for the user to confirm.",
    "2. Once confirmed, write the files following the rules above.",
    `3. Run: ${applyCommand}`,
    `4. Read ${path.join(genOut, "tests-applied.json")} and report, per file: Applied, Rejected (with its CompilerErrors), or Unverified.`,
    "5. For each Rejected file, fix it in the fills folder and run step 3 again. If a file is still rejected after two attempts, stop and report its compiler errors instead of guessing further.",
  ],
};

/**
 * `selected` scopes the body to those package names. Omitted (or every package
 * selected) means the whole-workspace body.
 */
export function buildApplyTestsInstructions(
  ws: Workspace,
  genOut: string,
  selected?: string[],
): ApplyTestsInstructions {
  return buildAiInstructions(ws, genOut, "apply-tests", listTestablePackages(ws, genOut), selected, APPLY_TESTS_FRAME);
}
