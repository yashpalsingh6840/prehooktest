// A small, dependency-free GFM table scanner for the generated reports this
// UI renders. These files are machine-written by ssisx itself (see
// CLAUDE.md's own "stable JSON/markdown" discipline), so a straightforward
// line-based parser is enough -- no need to pull in a full remark AST just
// to find `| col | col |` blocks.

export type ParsedTable = {
  headers: string[];
  rows: string[][];
  /** Character offset in the source string where this table's header line starts. */
  startIndex: number;
  /** Character offset just past this table's last line (including its trailing newline, if any). */
  endIndex: number;
};

const SEPARATOR_LINE = /^\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$/;

function splitRow(line: string): string[] {
  let trimmed = line.trim();
  if (trimmed.startsWith("|")) trimmed = trimmed.slice(1);
  if (trimmed.endsWith("|")) trimmed = trimmed.slice(0, -1);
  return trimmed.split("|").map((cell) => cell.trim());
}

/** Finds every GFM table in a markdown document, in document order. */
export function findMarkdownTables(markdown: string): ParsedTable[] {
  const lines = markdown.split("\n");
  // Track each line's start offset in the original string so we can report
  // exact character ranges (the string was split on "\n", which is why each
  // offset below adds back exactly one character per consumed line break).
  const lineStarts: number[] = [];
  let offset = 0;
  for (const line of lines) {
    lineStarts.push(offset);
    offset += line.length + 1; // +1 for the "\n" this split removed
  }

  const tables: ParsedTable[] = [];
  let i = 0;
  while (i < lines.length) {
    const headerLine = lines[i];
    const sepLine = lines[i + 1];
    if (
      headerLine.trim().startsWith("|") &&
      sepLine !== undefined &&
      SEPARATOR_LINE.test(sepLine.trim())
    ) {
      const headers = splitRow(headerLine);
      const rows: string[][] = [];
      let j = i + 2;
      while (j < lines.length && lines[j].trim().startsWith("|")) {
        rows.push(splitRow(lines[j]));
        j++;
      }
      const startIndex = lineStarts[i];
      const lastLineIndex = j - 1;
      // endIndex spans through the last body line's own trailing newline,
      // so slicing [startIndex, endIndex) out of the original string cleanly
      // removes the whole table block (and the surrounding prose can be
      // reassembled from what's left on either side).
      const endIndex =
        lastLineIndex + 1 < lines.length
          ? lineStarts[lastLineIndex + 1]
          : markdown.length;
      tables.push({ headers, rows, startIndex, endIndex });
      i = j;
    } else {
      i++;
    }
  }
  return tables;
}
