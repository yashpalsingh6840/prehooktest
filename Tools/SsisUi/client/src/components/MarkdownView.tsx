import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import type { ComponentPropsWithoutRef } from "react";
import MermaidDiagram from "./MermaidDiagram";
import CodeBlock from "./CodeBlock";

// react-markdown v10 gives a fenced block's language as a `language-xxx`
// className on <code>, but a BARE ``` fence (no language) gets no className
// at all -- identical to genuine inline code. The className alone can't
// distinguish them. A second, reliable signal does: per CommonMark, an
// inline code span can never contain a literal "\n" (internal newlines are
// normalized to spaces during parsing), while a fenced block's content
// always preserves its real newlines, even a one-line block. So treat the
// code as a block whenever EITHER signal fires.
function CodeRenderer({ className, children, ...rest }: ComponentPropsWithoutRef<"code">) {
  const match = /language-(\w+)/.exec(className || "");
  const raw = String(children);
  const isBlock = !!match || raw.includes("\n");

  if (!isBlock) {
    return (
      <code className="rounded bg-gray-100 px-1 py-0.5 text-[0.85em] text-gray-800" {...rest}>
        {children}
      </code>
    );
  }

  // Strip a trailing fence newline and normalize stray \r (source files are
  // CRLF-terminated) so CodeBlock/MermaidDiagram never render a dangling
  // blank line or literal \r.
  const text = raw.replace(/\r/g, "").replace(/\n$/, "");
  const language = match?.[1];
  if (language === "mermaid") {
    return <MermaidDiagram code={text} />;
  }
  return <CodeBlock language={language}>{text}</CodeBlock>;
}

// A thin, consistently-styled wrapper around react-markdown. remark-gfm is
// required for GitHub-flavored tables (every artifact this UI renders --
// generate-report.md, gaps, portfolio-digest.md -- uses `| col | col |`
// tables, which plain CommonMark does not render as tables at all).
// Typographic styling (headings, tables, lists) comes from Tailwind's
// typography plugin (`prose`); fenced code blocks are handled entirely by
// our own CodeRenderer/CodeBlock/MermaidDiagram instead, so `pre` is passed
// through untouched -- otherwise CodeBlock's own bordered box would end up
// nested inside react-markdown's default <pre>, double-wrapping it.
export default function MarkdownView({ markdown }: { markdown: string }) {
  return (
    <div className="prose prose-sm max-w-none prose-table:text-sm prose-th:bg-gray-100">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          pre: ({ children }) => <>{children}</>,
          code: CodeRenderer,
          // A wide GFM table (e.g. a walkthrough's per-component table) can
          // overflow the content area with no way to see the rest of it --
          // wrap every table in a horizontally-scrollable container as a
          // universal safety net for any table rendered through this view.
          table: ({ children, ...rest }) => (
            <div className="overflow-x-auto">
              <table {...rest}>{children}</table>
            </div>
          ),
        }}
      >
        {markdown}
      </ReactMarkdown>
    </div>
  );
}
