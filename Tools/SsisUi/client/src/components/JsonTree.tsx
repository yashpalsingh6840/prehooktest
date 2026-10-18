import { useState } from "react";

function valueColorClass(v: unknown): string {
  if (v === null) return "text-gray-400";
  switch (typeof v) {
    case "string":
      return "text-green-700";
    case "number":
      return "text-blue-700";
    case "boolean":
      return "text-purple-700";
    default:
      return "text-gray-700";
  }
}

function formatScalar(v: unknown): string {
  if (v === null) return "null";
  if (typeof v === "string") return `"${v}"`;
  return String(v);
}

function JsonNode({
  label,
  value,
  depth,
}: {
  label: string | null;
  value: unknown;
  depth: number;
}) {
  const isObject = value !== null && typeof value === "object";

  if (!isObject) {
    return (
      <div className="py-0.5 font-mono text-xs leading-5">
        {label !== null && <span className="text-gray-500">{label}: </span>}
        <span className={valueColorClass(value)}>{formatScalar(value)}</span>
      </div>
    );
  }

  const isArray = Array.isArray(value);
  const entries: [string, unknown][] = isArray
    ? (value as unknown[]).map((v, i) => [String(i), v])
    : Object.entries(value as Record<string, unknown>);

  // Auto-expand only the first two levels so a large spec.json doesn't
  // render as one enormous open tree on first paint.
  const [open, setOpen] = useState(depth < 2);

  return (
    <div className="py-0.5 font-mono text-xs leading-5">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="flex items-center gap-1 text-left hover:text-blue-700"
      >
        <span className="inline-block w-3 text-gray-400">{open ? "▾" : "▸"}</span>
        {label !== null && <span className="text-gray-500">{label}:</span>}
        <span className="text-gray-400">
          {isArray ? `[${entries.length}]` : `{${entries.length}}`}
        </span>
      </button>
      {open && (
        <div className="ml-3.5 border-l border-gray-100 pl-2">
          {entries.length === 0 ? (
            <div className="py-0.5 italic text-gray-400">(empty)</div>
          ) : (
            entries.map(([k, v]) => (
              <JsonNode key={k} label={k} value={v} depth={depth + 1} />
            ))
          )}
        </div>
      )}
    </div>
  );
}

/** A dependency-free, collapsible tree view for an arbitrary JSON value --
 * used to browse an extracted .spec.json in a nicer shape than a raw code
 * block, while CodeBlock (see ExtractedData.tsx) still covers the literal
 * raw-text view/copy case. */
export default function JsonTree({ data }: { data: unknown }) {
  return (
    <div className="overflow-x-auto rounded-lg border border-gray-200 bg-white p-3">
      <JsonNode label={null} value={data} depth={0} />
    </div>
  );
}
