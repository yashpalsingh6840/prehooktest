import fs from "node:fs";
import path from "node:path";

// Mirrors `ssisx apply-fills`'s own `fills-applied.json` manifest -- one row
// per seam/gap it found across `fills/<Package>/**`, whether or not it was
// actually applied.
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

/**
 * Reads `<genOutPath>/fills-applied.json`, written fresh on every
 * `ssisx apply-fills` run. Absent entirely ("apply-fills" hasn't been run
 * for this workspace yet) is a normal state, not an error.
 */
export function readFillsApplied(genOutPath: string): FillsAppliedResult {
  const fillsPath = path.join(genOutPath, "fills-applied.json");
  try {
    const raw = fs.readFileSync(fillsPath, "utf8");
    const fills = JSON.parse(raw) as FillRecordSpec[];
    return { available: true, fills };
  } catch {
    return { available: false, fills: [] };
  }
}
