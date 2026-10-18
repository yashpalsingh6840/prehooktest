import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";

// Renders one table cell's raw text (which may contain `` `code` `` spans or
// **bold**) inline, without react-markdown's default <p> block wrapper --
// a table cell must never introduce a block-level element.
export default function InlineMarkdown({ text }: { text: string }) {
  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      components={{
        p: ({ children }) => <>{children}</>,
        code: ({ children, ...rest }) => (
          <code className="rounded bg-gray-100 px-1 py-0.5 text-[0.85em] text-gray-800" {...rest}>
            {children}
          </code>
        ),
      }}
    >
      {text}
    </ReactMarkdown>
  );
}
