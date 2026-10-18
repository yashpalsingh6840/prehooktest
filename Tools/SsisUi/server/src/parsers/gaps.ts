import fs from "node:fs";
import path from "node:path";

// Mirrors Ssis.Extract.Model.Analysis.GapSpec (gaps.json), written by
// `ssisx generate` directly at the `--out` root -- confirmed on disk at
// Tools/SsisExtractor/out/verify-probe/gaps.json.
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

/**
 * Reads `<genOutPath>/gaps.json` -- an array of GapSpec written fresh on
 * every `ssisx generate` run. Absent entirely ("generate" hasn't been run for
 * this workspace yet) is a normal state, not an error.
 */
export function readGaps(genOutPath: string): GapsResult {
  const gapsPath = path.join(genOutPath, "gaps.json");
  try {
    const raw = fs.readFileSync(gapsPath, "utf8");
    const gaps = JSON.parse(raw) as GapSpec[];
    return { available: true, gaps };
  } catch {
    return { available: false, gaps: [] };
  }
}

/**
 * Reads one gap's own work-packet markdown, at
 * `<genOutPath>/gaps/<gap.Package>/<gap.PacketPath>` -- confirmed on disk
 * against a real ENCRYPTED-SECRET packet at
 * Tools/SsisExtractor/out/verify-probe/gaps/Package_Advanced/....
 */
export function readGapPacket(
  genOutPath: string,
  gap: Pick<GapSpec, "Package" | "PacketPath">,
): string | null {
  if (!gap.PacketPath) {
    return null;
  }
  const packetPath = path.join(
    genOutPath,
    "gaps",
    gap.Package,
    gap.PacketPath,
  );
  try {
    return fs.readFileSync(packetPath, "utf8");
  } catch {
    return null;
  }
}
