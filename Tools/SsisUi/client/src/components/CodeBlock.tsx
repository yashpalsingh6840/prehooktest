import { Prism as SyntaxHighlighter } from "react-syntax-highlighter";
import { oneLight } from "react-syntax-highlighter/dist/esm/styles/prism";

// Syntax-highlighted fenced code block for every language except mermaid
// (which renders as an actual diagram -- see MermaidDiagram). Prism's full
// grammar set already covers json/csharp/sql/vbnet/xml/powershell/etc; an
// unrecognized language just falls back to plain monospace text.
export default function CodeBlock({
  language,
  children,
  className = "my-3",
}: {
  language?: string;
  children: string;
  /** Outer margin classes. Defaults to my-3; pass e.g. "mb-3" to drop the top gap. */
  className?: string;
}) {
  return (
    <div className={`${className} overflow-x-auto rounded-lg border border-gray-200`}>
      <SyntaxHighlighter
        language={language || "text"}
        style={oneLight}
        customStyle={{ margin: 0, fontSize: "0.8rem", lineHeight: 1.5, padding: "0.75rem" }}
        wrapLongLines={false}
      >
        {children}
      </SyntaxHighlighter>
    </div>
  );
}
