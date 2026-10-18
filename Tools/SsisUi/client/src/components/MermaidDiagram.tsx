import { useEffect, useId, useRef, useState } from "react";
import mermaid from "mermaid";

// Initialized once at module scope -- mermaid.initialize is not meant to be
// called per-render, and every diagram in this app shares the same theme.
mermaid.initialize({ startOnLoad: false, theme: "default", securityLevel: "loose" });

export default function MermaidDiagram({ code }: { code: string }) {
  const id = useId().replace(/:/g, "-");
  const containerRef = useRef<HTMLDivElement>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setError(null);
    mermaid
      .render(`mermaid-${id}`, code)
      .then(({ svg }) => {
        if (!cancelled && containerRef.current) containerRef.current.innerHTML = svg;
      })
      .catch((e) => {
        // A malformed diagram must not crash the page it's embedded in --
        // fall back to showing the raw source instead.
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      });
    return () => {
      cancelled = true;
    };
  }, [code, id]);

  if (error) {
    return (
      <div className="rounded border border-red-200 bg-red-50 p-3 text-xs text-red-800">
        <p className="mb-1 font-semibold">Couldn't render this diagram: {error}</p>
        <pre className="overflow-x-auto whitespace-pre-wrap text-gray-700">{code}</pre>
      </div>
    );
  }

  return (
    <div className="my-3 overflow-x-auto rounded-lg border border-gray-200 bg-white p-3">
      {/* The SVG is inline by default, which pins it to the left edge; make it a
          centered block instead. */}
      <div ref={containerRef} className="[&>svg]:mx-auto [&>svg]:block" />
    </div>
  );
}
