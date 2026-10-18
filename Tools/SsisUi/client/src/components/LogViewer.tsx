import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";

/** Status buckets an apply-fills trace line can belong to. The first matching
 * pattern wins, so an error line that also mentions "stale" reads as an error. */
const LEVELS = [
  { key: "error", label: "Errors", pattern: /\b(error|failed|failure|exception)\b/i, cls: "text-red-300", chip: "bg-red-900/60 text-red-200 border-red-700" },
  { key: "stale", label: "Stale", pattern: /\bstale\b/i, cls: "text-amber-300", chip: "bg-amber-900/60 text-amber-200 border-amber-700" },
  { key: "orphaned", label: "Orphaned", pattern: /\borphan(ed)?\b/i, cls: "text-rose-300", chip: "bg-rose-900/60 text-rose-200 border-rose-700" },
  { key: "unattributed", label: "Unattributed", pattern: /\bunattributed\b/i, cls: "text-gray-300", chip: "bg-gray-700 text-gray-200 border-gray-500" },
  { key: "applied", label: "Applied", pattern: /\bapplied\b/i, cls: "text-emerald-300", chip: "bg-emerald-900/60 text-emerald-200 border-emerald-700" },
] as const;

type LevelKey = (typeof LEVELS)[number]["key"] | "other";

function levelOf(line: string): LevelKey {
  for (const l of LEVELS) if (l.pattern.test(line)) return l.key;
  return "other";
}

function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/** Dark, monospace, searchable view of a saved log file -- same look as the
 * live run console (LogPane). Each line is tagged by status so a long trace can
 * be narrowed to just errors, or just the fills that were applied or refused. */
export default function LogViewer({
  text,
  follow = false,
  heightClass = "max-h-[32rem]",
  emptyText = "(no output)",
  footer,
}: {
  text: string;
  /** Keep the view pinned to the newest line as text grows (live output). */
  follow?: boolean;
  heightClass?: string;
  emptyText?: string;
  /** Rendered under the log body, inside the same frame (e.g. a run status). */
  footer?: ReactNode;
}) {
  const [query, setQuery] = useState("");
  const [wrap, setWrap] = useState(false);
  const [hidden, setHidden] = useState<Set<LevelKey>>(() => new Set());
  const [copied, setCopied] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (follow && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [text, follow]);

  const lines = useMemo(() => text.split(/\r?\n/).map((line, i) => ({ n: i + 1, line, level: levelOf(line) })), [text]);

  // Only offer chips for levels that actually occur in this file.
  const present = useMemo(() => new Set(lines.map((l) => l.level)), [lines]);

  const needle = query.trim();
  const matcher = useMemo(
    () => (needle ? new RegExp(escapeRegex(needle), "gi") : null),
    [needle],
  );

  const visible = lines.filter((l) => !hidden.has(l.level));
  const matchCount = matcher
    ? visible.reduce((n, l) => n + (l.line.match(matcher)?.length ?? 0), 0)
    : 0;

  function toggle(key: LevelKey) {
    setHidden((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  function copy() {
    navigator.clipboard.writeText(text).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    });
  }

  return (
    <div className="overflow-hidden rounded-lg border border-gray-700 bg-gray-950 font-mono text-[0.8rem] text-gray-200">
      <div className="flex flex-wrap items-center gap-2 border-b border-gray-700 px-3 py-2">
        <input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Search log..."
          className="min-w-[12rem] flex-1 rounded border border-gray-600 bg-gray-900 px-2 py-1 text-xs text-gray-100 placeholder:text-gray-500"
        />
        {needle && (
          <span className="text-xs text-gray-400">
            {matchCount} match{matchCount === 1 ? "" : "es"}
          </span>
        )}
        <label className="flex items-center gap-1 text-xs text-gray-400">
          <input type="checkbox" checked={wrap} onChange={(e) => setWrap(e.target.checked)} />
          Wrap
        </label>
        <button
          type="button"
          onClick={copy}
          className="rounded border border-gray-600 px-2 py-1 text-xs text-gray-200 hover:bg-gray-800"
        >
          {copied ? "Copied" : "Copy"}
        </button>
      </div>

      {present.size > 1 && (
        <div className="flex flex-wrap gap-1.5 border-b border-gray-700 px-3 py-2">
          {LEVELS.filter((l) => present.has(l.key)).map((l) => {
            const on = !hidden.has(l.key);
            return (
              <button
                key={l.key}
                type="button"
                aria-pressed={on}
                onClick={() => toggle(l.key)}
                className={`rounded-full border px-2.5 py-0.5 text-xs ${l.chip} ${on ? "" : "opacity-40 line-through"}`}
              >
                {l.label}
              </button>
            );
          })}
        </div>
      )}

      <div ref={scrollRef} className={`${heightClass} overflow-auto py-2`}>
        {!text ? (
          <p className="px-3 py-2 text-gray-500">{emptyText}</p>
        ) : visible.length === 0 ? (
          <p className="px-3 py-2 text-gray-500">No lines match the current filters.</p>
        ) : (
          visible.map((l) => (
            <div key={l.n} className="flex gap-3 px-3 hover:bg-gray-900/60">
              <span className="w-10 shrink-0 select-none text-right text-gray-600">{l.n}</span>
              <span
                className={`min-w-0 flex-1 ${wrap ? "whitespace-pre-wrap break-words" : "whitespace-pre"} ${
                  LEVELS.find((x) => x.key === l.level)?.cls ?? "text-gray-300"
                }`}
              >
                {matcher ? highlight(l.line, matcher) : l.line || " "}
              </span>
            </div>
          ))
        )}
      </div>
      {footer && <div className="flex items-center gap-2 border-t border-gray-700 px-3 py-1.5">{footer}</div>}
    </div>
  );
}

function highlight(line: string, matcher: RegExp) {
  const parts: ReactNode[] = [];
  let last = 0;
  for (const m of line.matchAll(matcher)) {
    const start = m.index ?? 0;
    if (start > last) parts.push(line.slice(last, start));
    parts.push(
      <mark key={start} className="rounded bg-yellow-400/80 text-gray-900">
        {m[0]}
      </mark>,
    );
    last = start + m[0].length;
  }
  if (last < line.length) parts.push(line.slice(last));
  return parts;
}
