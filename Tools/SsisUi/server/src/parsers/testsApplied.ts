import fs from "node:fs";
import path from "node:path";

// Mirrors `ssisx apply-tests`' own `tests-applied.json` manifest -- one row per
// `fills/<Package>/MoreTests/*.cs` file it found, with what happened to it.
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

/**
 * Reads `<genOutPath>/tests-applied.json`, written fresh on every
 * `ssisx apply-tests` run. Absent entirely means apply-tests has not run for
 * this workspace yet -- a normal state, not an error.
 */
export function readTestsApplied(genOutPath: string): TestsAppliedResult {
  const testsPath = path.join(genOutPath, "tests-applied.json");
  try {
    const raw = fs.readFileSync(testsPath, "utf8");
    const tests = JSON.parse(raw) as MoreTestRecordSpec[];
    return { available: true, tests };
  } catch {
    return { available: false, tests: [] };
  }
}
