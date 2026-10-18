import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { getGeneratedFileContent, openFileInVsCode } from "../api/client";
import CodeBlock from "./CodeBlock";
import MarkdownView from "./MarkdownView";
import NavIcon from "./NavIcon";
import LoadingState from "./LoadingState";
import ErrorState from "./ErrorState";
import EmptyState from "./EmptyState";

const EXT_TO_LANGUAGE: Record<string, string> = {
  cs: "csharp",
  csproj: "xml",
  slnx: "xml",
  json: "json",
  md: "markdown",
  sql: "sql",
  xml: "xml",
  config: "xml",
};

function languageFor(fileName: string): string {
  const ext = fileName.split(".").pop()?.toLowerCase() ?? "";
  return EXT_TO_LANGUAGE[ext] ?? "text";
}

function OpenInVsCodeButton({ workspaceId, relPath }: { workspaceId: string; relPath: string }) {
  const mutation = useMutation({
    mutationFn: () => openFileInVsCode(workspaceId, relPath),
  });

  return (
    // Pinned to the top-right corner of the content box (its parent is relative).
    <div className="absolute right-3 top-3 z-10 flex items-center gap-2">
      {mutation.isError && (
        <span className="text-xs text-red-700">
          {mutation.error instanceof Error ? mutation.error.message : String(mutation.error)}
        </span>
      )}
      {mutation.isSuccess && <span className="text-xs text-green-700">Opened in VS Code</span>}
      <button
        onClick={() => mutation.mutate()}
        disabled={mutation.isPending}
        title="Open in VS Code"
        aria-label="Open in VS Code"
        className="flex h-7 w-7 items-center justify-center rounded border border-gray-300 bg-white text-gray-700 shadow-sm hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
      >
        <NavIcon name="extracted" className="h-3.5 w-3.5" />
      </button>
    </div>
  );
}

function FilePreview({ workspaceId, relPath }: { workspaceId: string; relPath: string }) {
  const { data, isLoading, error } = useQuery({
    queryKey: ["artifacts", workspaceId, "generate-file", relPath],
    queryFn: () => getGeneratedFileContent(workspaceId, relPath),
  });

  if (isLoading) return <LoadingState label="Loading file..." />;
  if (error) return <ErrorState error={error} />;
  if (!data) return <EmptyState message="Couldn't load this file." />;

  if (relPath.toLowerCase().endsWith(".md")) {
    return (
      <div className="relative">
        <OpenInVsCodeButton workspaceId={workspaceId} relPath={relPath} />
        <div className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
          <MarkdownView markdown={data.content} />
        </div>
      </div>
    );
  }

  return (
    <div className="relative">
      <OpenInVsCodeButton workspaceId={workspaceId} relPath={relPath} />
      <CodeBlock className="mb-3" language={languageFor(relPath)}>{data.content}</CodeBlock>
    </div>
  );
}

/** A simple list-on-the-left, content-on-the-right file browser for a flat
 * list of relative paths (production files or starter tests generated for
 * one package) -- same shape as SampleDataBrowser.tsx's own layout, reused
 * here for a different file family. `files` are relative to
 * `<out>/gen/generate/`, already how getGeneratedFileContent expects them. */
export default function FileBrowserPane({
  workspaceId,
  files,
  emptyMessage,
}: {
  workspaceId: string;
  files: string[];
  emptyMessage: string;
}) {
  const [selected, setSelected] = useState<string | null>(null);
  const active = selected && files.includes(selected) ? selected : files[0];

  if (files.length === 0) {
    return <EmptyState message={emptyMessage} />;
  }

  return (
    <div className="flex gap-4">
      <div className="max-h-96 w-80 shrink-0 space-y-0.5 overflow-y-auto">
        {files.map((f) => (
          <button
            key={f}
            onClick={() => setSelected(f)}
            title={f}
            className={`block w-full truncate rounded border px-2 py-1 text-left font-mono text-xs transition-colors ${
              active === f
                ? "border-blue-300 bg-blue-50 text-blue-800"
                : "border-transparent text-gray-700 hover:bg-gray-50"
            }`}
          >
            {f}
          </button>
        ))}
      </div>
      <div className="min-w-0 flex-1">
        {active ? (
          <FilePreview workspaceId={workspaceId} relPath={active} />
        ) : (
          <EmptyState message="Select a file to preview it." />
        )}
      </div>
    </div>
  );
}
