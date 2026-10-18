import type { VerdictCounts, ValidationOverview } from "../api/client";

/** Verdicts that mean "something differs between the .dtsx and the generated code". */
export const PROBLEM_VERDICTS = ["Mismatch", "LeftOnly", "RightOnly", "Unrecognized"] as const;

/** Real problems across the given gate verdict counts (a null gate was not run). */
export function countProblems(gates: (VerdictCounts | null)[]): number {
  return gates.reduce(
    (sum, counts) =>
      sum +
      PROBLEM_VERDICTS.reduce((s, verdict) => s + (counts?.[verdict] ?? 0), 0),
    0,
  );
}

/** A package is clean when no gate reports a real problem and nothing is left for a person. */
export function isClean(overview: ValidationOverview): boolean {
  return (
    countProblems([overview.gateCounts["1"], overview.gateCounts["2"], overview.gateCounts["3"]]) === 0 &&
    overview.bucketCounts.humanNeedsReview === 0
  );
}
