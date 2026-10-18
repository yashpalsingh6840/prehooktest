import { useState } from "react";
import NavIcon from "../components/NavIcon";
import PageInfo from "../components/PageInfo";
import { useQuery } from "@tanstack/react-query";
import {
  getSampleDataFileContent,
  getSampleDataFiles,
  sampleDataDownloadUrl,
  type SampleDataFile,
  type Workspace,
} from "../api/client";
import CodeBlock from "../components/CodeBlock";
import Badge, { type BadgeColor } from "../components/Badge";
import LoadingState from "../components/LoadingState";
import ErrorState from "../components/ErrorState";
import EmptyState from "../components/EmptyState";

const KIND_COLORS: Record<string, BadgeColor> = {
  sql: "blue",
  "expected-sql": "green",
  "reference-sql": "purple",
  "schema-sql": "amber",
  csv: "gray",
  excel: "gray",
  text: "gray",
  other: "gray",
};

const KIND_TO_LANGUAGE: Record<string, string> = {
  sql: "sql",
  "expected-sql": "sql",
  "reference-sql": "sql",
  "schema-sql": "sql",
  csv: "csv",
  text: "text",
};

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);

  async function handleCopy() {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard API unavailable (non-secure context, permissions, ...) --
      // silently do nothing rather than throw, same tolerance as every other
      // best-effort action in this UI.
    }
  }

  // Icon-only; the tooltip says what it does and flips to "Copied!" briefly.
  return (
    <button
      type="button"
      onClick={handleCopy}
      title={copied ? "Copied!" : "Copy file content to clipboard"}
      aria-label="Copy file content to clipboard"
      className="flex h-7 w-7 cursor-pointer items-center justify-center rounded border border-gray-300 bg-white text-gray-700 shadow-sm hover:bg-gray-50"
    >
      <NavIcon name="copy" className="h-3.5 w-3.5" />
    </button>
  );
}

function DownloadButton({
  workspaceId,
  packageName,
  fileName,
  iconOnly = false,
}: {
  workspaceId: string;
  packageName: string;
  fileName: string;
  iconOnly?: boolean;
}) {
  return (
    <a
      href={sampleDataDownloadUrl(workspaceId, packageName, fileName)}
      download
      title="Download this file"
      aria-label="Download this file"
      className={
        iconOnly
          ? "flex h-7 w-7 cursor-pointer items-center justify-center rounded border border-gray-300 bg-white text-gray-700 shadow-sm hover:bg-gray-50"
          : "flex cursor-pointer items-center gap-1.5 rounded border border-gray-300 bg-white px-2.5 py-1 text-xs font-medium text-gray-700 hover:bg-gray-50"
      }
    >
      <NavIcon name="download" className="h-3.5 w-3.5" />
      {!iconOnly && "Download"}
    </a>
  );
}

function FilePreview({
  workspace,
  packageName,
  file,
}: {
  workspace: Workspace;
  packageName: string;
  file: SampleDataFile;
}) {
  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "sampledata-file", packageName, file.name],
    queryFn: () => getSampleDataFileContent(workspace.id, packageName, file.name),
  });

  if (isLoading) return <LoadingState label="Loading file..." />;
  if (error) return <ErrorState error={error} />;
  if (!data) return <EmptyState message="Couldn't load this file." />;

  if (data.binary) {
    return (
      <div className="rounded-lg border border-gray-200 bg-white p-4 text-sm">
        <p className="mb-3 text-gray-700">
          Binary file — {formatSize(data.sizeBytes ?? 0)}
        </p>
        <DownloadButton workspaceId={workspace.id} packageName={packageName} fileName={file.name} />
      </div>
    );
  }

  return (
    <div className="relative">
      <div className="absolute right-3 top-3 z-10 flex items-center gap-2">
        <CopyButton text={data.content ?? ""} />
        <DownloadButton workspaceId={workspace.id} packageName={packageName} fileName={file.name} iconOnly />
      </div>
      <CodeBlock className="mb-3" language={KIND_TO_LANGUAGE[file.kind] ?? "text"}>{data.content ?? ""}</CodeBlock>
    </div>
  );
}

export default function SampleDataBrowser({
  workspace,
  packageName,
}: {
  workspace: Workspace;
  packageName: string;
}) {
  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspace.id, "sampledata-files", packageName],
    queryFn: () => getSampleDataFiles(workspace.id, packageName),
  });

  const [selectedName, setSelectedName] = useState<string | null>(null);

  if (isLoading) return <LoadingState label="Loading sample data..." />;
  if (error) return <ErrorState error={error} />;
  if (!data || !data.available) {
    return (
      <EmptyState message="No sample data generated for this package yet -- run the Sample Data step first." />
    );
  }

  const selected = data.files.find((f) => f.name === selectedName) ?? data.files[0];

  return (
    <div>
      <PageInfo
        summary="Deterministic, schema-correct sample data synthesized by svk from this package's already-extracted spec -- not real client data."
        details={
          <>
            <p>
              <code className="rounded bg-gray-200 px-1 py-0.5">svk sampledata</code> reads{" "}
              <code className="rounded bg-gray-200 px-1 py-0.5">packages/&lt;Package&gt;.spec.json</code>{" "}
              (written by a prior <code className="rounded bg-gray-200 px-1 py-0.5">ssisx extract</code>{" "}
              run) and invents values matching every source/destination column's own extracted
              type -- it never reads a real database or file this package was built against.
            </p>
            <ul className="list-disc space-y-1 pl-4">
              <li>
                <strong>schema.sql</strong> -- a runnable CREATE TABLE script for every
                destination table in this package.
              </li>
              <li>
                <strong>.csv / other per-source files</strong> -- synthetic input rows for each
                source, one row set per source component.
              </li>
              <li>
                <strong>&lt;Lookup&gt;.reference.sql</strong> -- a seeded reference table for
                each Lookup with a resolvable join key, built so both the match and no-match
                paths get exercised.
              </li>
              <li>
                <strong>&lt;Destination&gt;.expected.sql</strong> -- the expected contents of
                that destination table after a correct run, for comparing against what the
                generated pipeline actually produced.
              </li>
            </ul>
            <p>
              Re-running with the same seed reproduces this byte-for-byte. The same files can
              also auto-close a package's own <code className="rounded bg-gray-200 px-1 py-0.5">LocalFileSourceData</code>{" "}
              gaps (see the Gaps page) instead of someone hand-supplying real test data.
            </p>
          </>
        }
      />
      <p className="mb-3 break-all font-mono text-xs text-gray-400">{data.path}</p>
      <div className="flex gap-4">
        <div className="w-64 shrink-0 space-y-1">
          {data.files.map((f) => (
            <button
              key={f.name}
              onClick={() => setSelectedName(f.name)}
              className={`flex w-full flex-col gap-1 rounded border px-2 py-1.5 text-left text-xs transition-colors ${
                selected?.name === f.name
                  ? "border-blue-300 bg-blue-50"
                  : "border-gray-200 bg-white hover:bg-gray-50"
              }`}
            >
              <span className="break-all font-medium text-gray-800">{f.name}</span>
              <span className="flex items-center gap-1.5">
                <Badge color={KIND_COLORS[f.kind] ?? "gray"}>{f.kind}</Badge>
                <span className="text-gray-400">{formatSize(f.sizeBytes)}</span>
              </span>
            </button>
          ))}
        </div>

        <div className="min-w-0 flex-1">
          {selected ? (
            <FilePreview workspace={workspace} packageName={packageName} file={selected} />
          ) : (
            <EmptyState message="Select a file to preview it." />
          )}
        </div>
      </div>
    </div>
  );
}
