import Badge, { type BadgeColor } from "./Badge";
import DataTable from "./DataTable";
import EmptyState from "./EmptyState";

/** One row of gaps.json -- mirrors server/src/parsers/gaps.ts GapSpec. */
export type GapRecord = {
  GapId: string;
  Package: string;
  Kind: string;
  Tier: string;
  Location: string;
  Reason: string;
  IsBlocking: boolean;
  PacketPath: string | null;
};

/** One row of fills-applied.json -- mirrors server/src/parsers/fillsApplied.ts FillRecordSpec. */
export type FillRecord = {
  Package: string;
  FileName: string;
  Seam: string;
  GapId: string | null;
  Status: string;
  Author: string | null;
  Date: string | null;
  RecordedEvidenceSha256: string | null;
  CurrentEvidenceSha256: string | null;
  ConformanceRuleId: string | null;
  Synthetic: boolean;
};

const TIER_LABELS: Record<string, string> = {
  Advisory: "Advisory",
  MissingDatum: "Missing Datum",
  MissingLogic: "Missing Logic",
  MissingToolSupport: "No Tool Support",
};

// Same colors as GapBadge, so a tier reads the same here as on the Gaps page.
const TIER_COLORS: Record<string, BadgeColor> = {
  Advisory: "blue",
  MissingDatum: "amber",
  MissingLogic: "red",
  MissingToolSupport: "purple",
};

// Same colors as FillsStatus, so a status reads the same here as above it.
const STATUS_COLORS: Record<string, BadgeColor> = {
  Applied: "green",
  Stale: "amber",
  Orphaned: "red",
  Unattributed: "gray",
};

type Chip = { label: string; count: number; color: BadgeColor };

/** Counts per distinct value, in first-seen order so the chips follow the file. */
function countBy<T>(items: T[], key: (item: T) => string): Map<string, number> {
  const counts = new Map<string, number>();
  for (const item of items) {
    const k = key(item);
    counts.set(k, (counts.get(k) ?? 0) + 1);
  }
  return counts;
}

function SummaryChips({ chips }: { chips: Chip[] }) {
  return (
    <div className="mb-3 flex flex-wrap gap-2">
      {chips.map((chip) => (
        <Badge key={chip.label} color={chip.color}>
          {chip.label}: {chip.count}
        </Badge>
      ))}
    </div>
  );
}

export function GapsTable({ gaps }: { gaps: GapRecord[] }) {
  if (gaps.length === 0) return <EmptyState message="No gaps recorded in this file." />;

  const chips: Chip[] = [
    { label: "Total", count: gaps.length, color: "gray" },
    { label: "Blocking", count: gaps.filter((g) => g.IsBlocking).length, color: "red" },
    ...[...countBy(gaps, (g) => g.Tier)].map(([tier, count]) => ({
      label: TIER_LABELS[tier] ?? tier,
      count,
      color: TIER_COLORS[tier] ?? "gray",
    })),
  ];

  const rows = gaps.map((g) => [
    g.Package,
    TIER_LABELS[g.Tier] ?? g.Tier,
    g.Kind,
    g.IsBlocking ? "Yes" : "No",
    g.Location,
    g.Reason,
    g.PacketPath ?? "—",
  ]);

  return (
    <>
      <SummaryChips chips={chips} />
      <DataTable
        headers={["Package", "Tier", "Kind", "Blocking", "Location", "Reason", "Work packet"]}
        rows={rows}
        filterPlaceholder="Filter gaps..."
        expandableColumnIndex={5}
        truncateAt={160}
      />
    </>
  );
}

/** Compares the hash recorded when the fill was written with the gap's hash
 * now. Only "Matches" means the fill still answers the same evidence. */
function evidenceLabel(f: FillRecord): string {
  if (!f.RecordedEvidenceSha256) return "Not recorded";
  if (!f.CurrentEvidenceSha256) return "No current gap";
  return f.RecordedEvidenceSha256 === f.CurrentEvidenceSha256 ? "Matches" : "Changed";
}

export function FillsTable({ fills }: { fills: FillRecord[] }) {
  if (fills.length === 0) return <EmptyState message="No fills recorded in this file." />;

  const chips: Chip[] = [
    { label: "Total", count: fills.length, color: "gray" },
    ...[...countBy(fills, (f) => f.Status)].map(([status, count]) => ({
      label: status,
      count,
      color: STATUS_COLORS[status] ?? "gray",
    })),
  ];

  const rows = fills.map((f) => [
    f.Package,
    f.FileName,
    f.Seam,
    f.GapId ?? "—",
    f.Status,
    f.Author ?? "—",
    f.Date ?? "—",
    evidenceLabel(f),
    f.ConformanceRuleId ?? "—",
    f.Synthetic ? "Yes" : "No",
  ]);

  return (
    <>
      <SummaryChips chips={chips} />
      <DataTable
        headers={["Package", "File", "Seam", "Gap", "Status", "Author", "Date", "Evidence", "Conformance rule", "Synthetic"]}
        rows={rows}
        filterPlaceholder="Filter fills..."
      />
    </>
  );
}
