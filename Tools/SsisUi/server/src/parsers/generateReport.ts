// Parses generate-report.md (written by `ssisx generate`, see
// GenerateCommand.cs's own WriteReport) into structured data -- so the UI
// doesn't have to guess table positions by document order (the Tier-1
// decisions / crashed-packages sections are only present at all when
// non-empty, which previously made index-based table matching fragile).
// Every table is located by anchoring on its own fixed heading/header text,
// which is literal and stable in the generator's own source.

export type GeneratePackageRow = {
  package: string;
  filesWritten: number;
  starterTests: number;
  gaps: number;
};

export type CrashedPackageRow = { package: string; diagnosticPath: string };
export type Tier1DecisionRow = { package: string; status: string; gapId: string; detail: string };
export type FailedToLoadRow = { path: string; kind: string; reason: string };

export type GenerateReportSummary = {
  totalFiles: number;
  totalPackages: number;
  totalGaps: number;
  crashedCount: number;
  packages: GeneratePackageRow[];
  crashedPackages: CrashedPackageRow[];
  tier1Decisions: Tier1DecisionRow[];
  failedToLoad: FailedToLoadRow[];
};

function splitRow(line: string): string[] {
  const trimmed = line.trim().replace(/^\|/, "").replace(/\|$/, "");
  return trimmed.split("|").map((c) => c.trim());
}

const SEPARATOR_LINE = /^\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$/;

/** Finds the next GFM table starting at or after `fromIndex`, stopping at the
 * first run of consecutive `|`-prefixed lines. Returns `null` if none is
 * found before the next blank-line gap with no table (e.g. a "_None._"
 * placeholder instead of a real table). */
function parseFirstTable(markdown: string, fromIndex: number): string[][] | null {
  const lines = markdown.slice(fromIndex).split("\n");
  // Skip prose/blank lines between the anchor and the table itself. If the
  // section's content is "_None._" instead of a real table, no "|" line is
  // ever found before the next heading/EOF and this returns null below.
  let i = 0;
  while (i < lines.length && !lines[i].trim().startsWith("|") && !lines[i].trim().startsWith("##")) {
    i++;
  }
  if (i >= lines.length || lines[i].trim().startsWith("##")) return null;
  const sepLine = lines[i + 1] ?? "";
  if (!SEPARATOR_LINE.test(sepLine.trim())) return null;
  const rows: string[][] = [];
  let j = i + 2;
  while (j < lines.length && lines[j].trim().startsWith("|")) {
    rows.push(splitRow(lines[j]));
    j++;
  }
  return rows;
}

function parseTableAfter(markdown: string, anchor: string): string[][] {
  const idx = markdown.indexOf(anchor);
  if (idx === -1) return [];
  return parseFirstTable(markdown, idx) ?? [];
}

export function parseGenerateReport(markdown: string): GenerateReportSummary {
  const summaryMatch = markdown.match(
    /\*\*(\d+) file\(s\) written across (\d+) package\(s\), (\d+) gap\(s\)\.\*\*/,
  );
  const crashedMatch = markdown.match(/\*\*(\d+) package\(s\) crashed during generation/);

  const packages: GeneratePackageRow[] = parseTableAfter(
    markdown,
    "| Package | Files written | Starter tests | Gaps |",
  ).map((r) => ({
    package: r[0] ?? "",
    filesWritten: Number(r[1] ?? 0) || 0,
    starterTests: Number(r[2] ?? 0) || 0,
    gaps: Number(r[3] ?? 0) || 0,
  }));

  const tier1Decisions: Tier1DecisionRow[] = parseTableAfter(markdown, "## Tier-1 decisions").map(
    (r) => ({
      package: r[0] ?? "",
      status: r[1] ?? "",
      gapId: (r[2] ?? "").replace(/`/g, ""),
      detail: r[3] ?? "",
    }),
  );

  const crashedPackages: CrashedPackageRow[] = parseTableAfter(
    markdown,
    "## Packages that crashed during generation",
  ).map((r) => ({ package: r[0] ?? "", diagnosticPath: r[1] ?? "" }));

  const failedToLoad: FailedToLoadRow[] = parseTableAfter(
    markdown,
    "## Packages that failed to load",
  ).map((r) => ({ path: r[0] ?? "", kind: r[1] ?? "", reason: r[2] ?? "" }));

  return {
    totalFiles: summaryMatch ? Number(summaryMatch[1]) : 0,
    totalPackages: summaryMatch ? Number(summaryMatch[2]) : packages.length,
    totalGaps: summaryMatch ? Number(summaryMatch[3]) : 0,
    crashedCount: crashedMatch ? Number(crashedMatch[1]) : crashedPackages.length,
    packages,
    crashedPackages,
    tier1Decisions,
    failedToLoad,
  };
}
