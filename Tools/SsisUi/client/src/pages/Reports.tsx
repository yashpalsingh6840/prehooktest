import { useState, type ReactNode } from "react";
import { useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  getGenerateSummary,
  getLoadFailures,
  getPortfolioDigest,
  type Workspace,
} from "../api/client";
import MarkdownView from "../components/MarkdownView";
import DataTable from "../components/DataTable";
import Badge, { type BadgeColor } from "../components/Badge";
import CodeBlock from "../components/CodeBlock";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";
import EmptyState from "../components/EmptyState";
import { findMarkdownTables, type ParsedTable } from "../utils/parseMarkdownTable";
import GenerateReportView from "./GenerateReportView";
import PageInfo from "../components/PageInfo";
import TabNav from "../components/TabNav";

type Section = "generate" | "load-failures" | "portfolio-digest";

const SECTIONS: { key: Section; label: string }[] = [
  { key: "generate", label: "Report" },
  { key: "load-failures", label: "Load Failures" },
  { key: "portfolio-digest", label: "Portfolio Digest" },
];

const SECTION_KEYS = new Set<string>(SECTIONS.map((s) => s.key));

/** Finds the index of a header named `name` (case-insensitive), or undefined if absent. */
function columnIndex(headers: string[], name: string): number | undefined {
  const i = headers.findIndex((h) => h.toLowerCase() === name.toLowerCase());
  return i === -1 ? undefined : i;
}

/**
 * Splits a markdown document into an ordered list of segments -- prose
 * rendered through MarkdownView, GFM tables rendered through DataTable --
 * so headings/explanatory text stay exactly where they belong relative to
 * each table. `tableOptions` lets a caller tune filter placeholder/
 * expandable column per table index (in document order); any table without
 * an entry gets sensible defaults.
 */
function TabularMarkdown({
  markdown,
  tableOptions = [],
}: {
  markdown: string;
  tableOptions?: { filterPlaceholder?: string; expandableColumnName?: string }[];
}) {
  const tables = findMarkdownTables(markdown);
  if (tables.length === 0) {
    return <MarkdownView markdown={markdown} />;
  }

  const segments: ReactNode[] = [];
  let cursor = 0;
  tables.forEach((table, i) => {
    const prose = markdown.slice(cursor, table.startIndex);
    if (prose.trim()) segments.push(<MarkdownView key={`prose-${i}`} markdown={prose} />);

    const options = tableOptions[i] ?? {};
    const expandableColumnIndex = options.expandableColumnName
      ? columnIndex(table.headers, options.expandableColumnName)
      : undefined;

    segments.push(
      <DataTable
        key={`table-${i}`}
        headers={table.headers}
        rows={table.rows}
        filterPlaceholder={options.filterPlaceholder}
        expandableColumnIndex={expandableColumnIndex}
      />,
    );
    cursor = table.endIndex;
  });
  const trailingProse = markdown.slice(cursor);
  if (trailingProse.trim()) segments.push(<MarkdownView key="prose-last" markdown={trailingProse} />);

  return <>{segments}</>;
}

/** Pairs each GFM table in the digest with the "## Heading" that precedes it
 * -- the digest's own generator (PortfolioDigest.cs) OMITS a section entirely
 * when it has zero rows, so a fixed table-index assumption would silently
 * misattribute every table after the first omitted one. Matching by nearest
 * preceding heading is robust to that. */
function sectionsByHeading(markdown: string): { heading: string; table: ParsedTable }[] {
  const headings = [...markdown.matchAll(/^## (.+)$/gm)].map((m) => ({
    title: m[1].trim(),
    index: m.index ?? 0,
  }));
  return findMarkdownTables(markdown).map((table) => {
    const heading = [...headings].reverse().find((h) => h.index < table.startIndex);
    return { heading: heading?.title ?? "", table };
  });
}

/** The leading fenced ```...``` block -- a fixed-width console-style summary
 * (complexity/task-type/component-type/connection-manager/expression-function
 * histograms) that has no GFM-table equivalent elsewhere in the document, so
 * it's kept as a raw, collapsible reference rather than dropped. */
function extractRawDigestBlock(markdown: string): string | null {
  const match = markdown.match(/```\n([\s\S]*?)```/);
  return match ? match[1].trimEnd() : null;
}

const SECTION_INFO: Record<string, { caption: string; filterPlaceholder?: string }> = {
  "Per package": {
    caption:
      "One row per package, named only as a stable pseudonym (e.g. PKG-003) -- this digest never includes real package names. " +
      "Class/Score come from the complexity scorer (higher score = more complex to migrate); Script units counts Script " +
      "Tasks/Components, which always need manual porting; Coverage % is how much of that package this tool could parse; " +
      "Generation gaps is how many `ssisx generate` gaps remain -- \"0 (ready)\" means the package is fully generatable today.",
    filterPlaceholder: "Filter by package, class...",
  },
  "Generation gap reasons (ssisx generate)": {
    caption:
      "How often each gap reason occurs across the whole portfolio -- a high count means one issue is blocking many " +
      "packages, worth fixing once rather than per-package. Reason text is cut short by design (this digest never includes " +
      "SQL text or file paths); for the full reason on a specific package, use that package's Gaps tab.",
    filterPlaceholder: "Filter reasons...",
  },
  "Generation gap kinds (recurrence = promote to the emitter)": {
    caption:
      "The same Gap Kind shown on the Gaps tab, tallied across the whole portfolio. A Kind that recurs often across many " +
      "packages is a strong candidate for `ssisx` itself to gain built-in support for, instead of fixing it by hand in " +
      "each package one at a time.",
  },
  "Findings by rule": {
    caption:
      "How many times each extractor rule fired across the portfolio (e.g. a hardcoded connection string, an unrouted " +
      "error output). See a package's own Walkthrough tab for the full detail behind a specific rule.",
  },
  "Expression casts": {
    caption: "Which SSIS expression-language type casts appear across the portfolio, and how often.",
  },
  "Unmapped element reasons": {
    caption:
      "XML elements this tool doesn't model yet, grouped by why. A reason recurring often here is a candidate for a " +
      "future extractor update.",
  },
};

type ProtectionLevelInfo = { label: string; risk: BadgeColor; summary: string; action: string };

// Keyed by the exact constant name ssisx's own SsisTypeCodeMaps.ProtectionLevelName
// emits -- see Ssis.Extract.Model/Shared/SsisTypeCodeMaps.cs. Risk/action text
// mirrors RulesEngine.cs's own protection-level findings (the authoritative
// migration-risk wording this tool already uses elsewhere).
const PROTECTION_LEVELS: Record<string, ProtectionLevelInfo> = {
  DontSaveSensitive: {
    label: "Don't Save Sensitive",
    risk: "green",
    summary: "Sensitive values (passwords, etc.) are never saved in the file at all -- the most common, safest default.",
    action: "No action needed -- just supply real credentials yourself whenever the package actually runs.",
  },
  EncryptSensitiveWithUserKey: {
    label: "Encrypt Sensitive (User Key)",
    risk: "amber",
    summary:
      "Every sensitive value is DPAPI-encrypted to the ORIGINAL AUTHOR's own Windows account -- unrecoverable on any " +
      "other machine, including this one.",
    action:
      "Check the Gaps tab for an \"Encrypted secret\" gap naming the affected connection manager(s), and supply the " +
      "real credential out of band.",
  },
  EncryptSensitiveWithPassword: {
    label: "Encrypt Sensitive (Password)",
    risk: "amber",
    summary: "Sensitive values are encrypted with a password set when the package was last saved.",
    action: "You'll need that original save password to recover the credential -- treat it as a secret for any CI/CD pipeline.",
  },
  EncryptAllWithPassword: {
    label: "Encrypt All (Password)",
    risk: "red",
    summary:
      "The ENTIRE package -- not just sensitive values -- is encrypted with a password, which can block this tool from " +
      "reading it at all.",
  action: "Get the save password from whoever authored the package before attempting to extract or migrate it.",
  },
  EncryptAllWithUserKey: {
    label: "Encrypt All (User Key)",
    risk: "red",
    summary:
      "The ENTIRE package is encrypted to the original author's own Windows account, which can block this tool from " +
      "reading it at all on any other machine.",
    action: "Re-save the package (e.g. as Don't Save Sensitive) from the original author's own machine/account first.",
  },
  ServerStorage: {
    label: "Server Storage",
    risk: "gray",
    summary: "Protection is delegated to the SSIS catalog this package is deployed to, not stored in the file itself.",
    action: "No action needed for a standalone-file review.",
  },
};

function ProtectionLevelsCard({ rows }: { rows: string[][] }) {
  return (
    <div className="space-y-2">
      {rows.map(([rawValue, count]) => {
        const info: ProtectionLevelInfo = PROTECTION_LEVELS[rawValue] ?? {
          label: rawValue,
          risk: "gray",
          summary: "Not a recognized protection-level constant.",
          action: "",
        };
        return (
          <div
            key={rawValue}
            className="flex items-start gap-3 rounded-lg border border-gray-200 bg-white p-3 shadow-sm"
          >
            <Badge color={info.risk} className="shrink-0 px-2.5 py-1 text-sm">
              {count}
            </Badge>
            <div className="min-w-0 flex-1">
              <div className="flex flex-wrap items-baseline gap-x-2">
                <strong className="text-sm text-gray-900">{info.label}</strong>
                <span className="font-mono text-xs text-gray-400">{rawValue}</span>
              </div>
              <p className="mt-0.5 text-sm text-gray-600">{info.summary}</p>
              {info.action && <p className="mt-1 text-xs font-medium text-gray-500">{info.action}</p>}
            </div>
          </div>
        );
      })}
    </div>
  );
}

function DigestSection({ heading, table }: { heading: string; table: ParsedTable }) {
  const info = SECTION_INFO[heading];
  return (
    <div className="mb-6">
      <h3 className="mb-1 text-base font-semibold">{heading || "Untitled section"}</h3>
      {info?.caption && <p className="mb-2 text-sm text-gray-500">{info.caption}</p>}
      {heading === "Protection levels" ? (
        <ProtectionLevelsCard rows={table.rows} />
      ) : (
        <DataTable
          headers={table.headers}
          rows={table.rows}
          filterPlaceholder={info?.filterPlaceholder ?? "Filter..."}
        />
      )}
    </div>
  );
}

function PortfolioDigestView({ workspace }: { workspace: Workspace }) {
  const [showRaw, setShowRaw] = useState(false);
  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "portfolio-digest"],
    queryFn: () => getPortfolioDigest(workspace.id),
  });

  if (isLoading) return <LoadingState />;
  if (error) return <ErrorState error={error} />;
  if (!data || !data.available) {
    return (
      <EmptyState message="Nothing here yet -- run `ssisx report` against this workspace's extract output first." />
    );
  }

  const sections = sectionsByHeading(data.markdown);
  const rawBlock = extractRawDigestBlock(data.markdown);

  return (
    <div>
      <PageInfo
        summary="A portfolio-wide survey from ssisx report -- complexity, findings, and (per the Generation Readiness section) how much of this portfolio Generate could produce today, all computed without ever running Generate for real."
        details={
          <p>
            Each section below comes from the same analysis pass over every extracted package.
            "Generation Readiness" specifically answers "what fraction of this portfolio is
            fully generatable right now" -- it runs the generator in-memory to count gaps without
            writing any files, so it stays current with Generate's own real capabilities rather
            than drifting out of sync. Expand "Show full raw digest" below for the complete
            breakdown (task/component type counts, connection managers, expression functions)
            this summary doesn't surface as its own section.
          </p>
        }
      />
      {sections.map(({ heading, table }, i) => (
        <DigestSection key={`${heading}-${i}`} heading={heading} table={table} />
      ))}

      {rawBlock && (
        <div className="mt-2">
          <button
            onClick={() => setShowRaw((v) => !v)}
            className="rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50"
          >
            {showRaw ? "Hide" : "Show"} full raw digest (complexity, task/component type, connection
            manager, expression-function breakdowns)
          </button>
          {showRaw && <CodeBlock language="text">{rawBlock}</CodeBlock>}
        </div>
      )}
    </div>
  );
}

/**
 * "Load Failures" covers two genuinely separate lists that happen to share
 * the same Path/Kind/Reason shape: packages `ssisx extract` couldn't load
 * (load-failures.md, from the last `report` run) and packages `ssisx
 * generate` couldn't load/crashed on during its own, independent package
 * load (generate-report.md). A package can appear in one without the other,
 * so both sections are kept -- clearly labeled -- rather than only showing
 * one and silently losing the other.
 */
function LoadFailuresView({ workspace }: { workspace: Workspace }) {
  const extractQuery = useQuery({
    queryKey: ["artifacts", workspace.id, "load-failures"],
    queryFn: () => getLoadFailures(workspace.id),
  });
  const generateQuery = useQuery({
    queryKey: ["artifacts", workspace.id, "generate-summary"],
    queryFn: () => getGenerateSummary(workspace.id),
  });

  if (extractQuery.isLoading || generateQuery.isLoading) return <LoadingState />;
  if (extractQuery.error) return <ErrorState error={extractQuery.error} />;
  if (generateQuery.error) return <ErrorState error={generateQuery.error} />;

  const extractAvailable = extractQuery.data?.available ?? false;
  const genFailed = generateQuery.data?.failedToLoad ?? [];
  const genCrashed = generateQuery.data?.crashedPackages ?? [];

  if (!extractAvailable && genFailed.length === 0 && genCrashed.length === 0) {
    return <EmptyState message="Nothing here yet -- run Extract and/or Generate first." />;
  }

  return (
    <div className="space-y-6">
      <PageInfo
        summary="Two genuinely separate lists that happen to share the same Path/Kind/Reason shape: what Extract couldn't load, and what Generate separately couldn't load."
        details={
          <p>
            A package can crash during Extract (malformed XML, an unresolvable duplicate{" "}
            <code className="rounded bg-gray-200 px-1 py-0.5">ObjectName</code>, ...) without
            ever being attempted by Generate, or load fine during Extract but crash independently
            when Generate re-loads it for code generation -- so both sections are shown, clearly
            labeled, rather than only one and silently losing the other. Neither list includes a
            package that loaded fine but simply hit a reported gap -- that's the Report tab.
          </p>
        }
      />
      <div>
        <h3 className="mb-2 text-base font-semibold">During Extract</h3>
        {extractAvailable ? (
          <div className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
            <TabularMarkdown
              markdown={extractQuery.data!.markdown}
              tableOptions={[
                { filterPlaceholder: "Filter by path or reason...", expandableColumnName: "Reason" },
              ]}
            />
          </div>
        ) : (
          <EmptyState message="Run `ssisx report` against this workspace's extract output to see this." />
        )}
      </div>

      <div>
        <h3 className="mb-2 text-base font-semibold">During Generate</h3>
        <p className="mb-2 text-xs text-gray-500">
          `ssisx generate` loads packages independently from `ssisx extract` -- a package can fail
          here even when it loaded fine above, or vice versa.
        </p>
        {genFailed.length === 0 && genCrashed.length === 0 ? (
          <EmptyState message="None." />
        ) : (
          <>
            {genFailed.length > 0 && (
              <div className="mb-4 overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
                <table className="w-full border-collapse text-sm">
                  <thead>
                    <tr className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                      <th className="px-3 py-2">Path</th>
                      <th className="px-3 py-2">Kind</th>
                      <th className="px-3 py-2">Reason</th>
                    </tr>
                  </thead>
                  <tbody>
                    {genFailed.map((f, i) => (
                      <tr
                        key={`${f.path}-${i}`}
                        className={`border-t border-gray-100 ${i % 2 === 1 ? "bg-gray-50/50" : ""}`}
                      >
                        <td className="px-3 py-2 font-mono text-xs">{f.path}</td>
                        <td className="px-3 py-2 text-xs">{f.kind}</td>
                        <td className="px-3 py-2 text-xs text-gray-600">{f.reason}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {genCrashed.length > 0 && (
              <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
                <table className="w-full border-collapse text-sm">
                  <thead>
                    <tr className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                      <th className="px-3 py-2">Package</th>
                      <th className="px-3 py-2">Diagnostic File (crashed during generation)</th>
                    </tr>
                  </thead>
                  <tbody>
                    {genCrashed.map((c, i) => (
                      <tr
                        key={`${c.package}-${i}`}
                        className={`border-t border-gray-100 ${i % 2 === 1 ? "bg-gray-50/50" : ""}`}
                      >
                        <td className="px-3 py-2">{c.package}</td>
                        <td className="px-3 py-2 font-mono text-xs">{c.diagnosticPath}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}

export default function Reports({ workspace }: { workspace: Workspace }) {
  const { section: rawSection } = useParams<{ section?: string }>();
  const section: Section = SECTION_KEYS.has(rawSection ?? "") ? (rawSection as Section) : "generate";

  return (
    <div>
      <h2 className="mb-1 text-base font-semibold">Generate</h2>
      <TabNav
        tabs={SECTIONS}
        active={section}
        hrefFor={(key) => `/workspaces/${workspace.id}/reports/${key}`}
      />

      {section === "generate" && <GenerateReportView workspace={workspace} />}
      {section === "load-failures" && <LoadFailuresView workspace={workspace} />}
      {section === "portfolio-digest" && <PortfolioDigestView workspace={workspace} />}
    </div>
  );
}
