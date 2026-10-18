import fs from "node:fs";
import path from "node:path";

// Mirrors Svk.Core.WalkthroughClaim / WalkthroughStatus (SvkCore/WalkthroughModels.cs)
// -- one entry per `ssisx conformance` DataFlow rule for a package, written
// once as a stub by `svk walkthrough` and never overwritten by a later
// re-run (only new stubs get appended). Confirmed on disk at
// <out>/svk/walkthrough/claims/<Package>.claims.json -- PascalCase field
// names and PascalCase enum string values (JsonStringEnumConverter, no
// naming policy).
export type WalkthroughStatus = "NotValidated" | "Validated" | "Blocked";

export type WalkthroughClaim = {
  RuleId: string;
  Status: WalkthroughStatus;
  Reviewer: string | null;
  ReviewedDate: string | null;
  Note: string | null;
};

export type ClaimsResult = { available: boolean; claims: WalkthroughClaim[] };

function claimsPath(svkOutPath: string, packageName: string): string {
  return path.join(svkOutPath, "walkthrough", "claims", `${packageName}.claims.json`);
}

/**
 * Reads `<svkOutPath>/walkthrough/claims/<packageName>.claims.json` -- absent
 * entirely ("walkthrough" hasn't been run for this package yet) is a normal
 * state, not an error.
 */
export function readClaims(svkOutPath: string, packageName: string): ClaimsResult {
  try {
    const raw = fs.readFileSync(claimsPath(svkOutPath, packageName), "utf8");
    const claims = JSON.parse(raw) as WalkthroughClaim[];
    return { available: true, claims };
  } catch {
    return { available: false, claims: [] };
  }
}

/**
 * Updates exactly one claim (matched by RuleId) in the package's claims
 * file, leaving every other entry untouched, and writes the whole array
 * back. Returns false (and writes nothing) if the file doesn't exist or no
 * claim with that RuleId is present -- the UI only ever edits an existing,
 * `svk`-populated entry, it never creates one.
 */
export function updateClaim(
  svkOutPath: string,
  packageName: string,
  ruleId: string,
  update: { status: WalkthroughStatus; reviewer: string; note: string },
): boolean {
  const filePath = claimsPath(svkOutPath, packageName);
  let claims: WalkthroughClaim[];
  try {
    claims = JSON.parse(fs.readFileSync(filePath, "utf8")) as WalkthroughClaim[];
  } catch {
    return false;
  }

  const claim = claims.find((c) => c.RuleId === ruleId);
  if (!claim) {
    return false;
  }

  claim.Status = update.status;
  claim.Reviewer = update.reviewer;
  claim.Note = update.note;
  claim.ReviewedDate = new Date().toISOString();

  fs.writeFileSync(filePath, JSON.stringify(claims, null, 2), "utf8");
  return true;
}
