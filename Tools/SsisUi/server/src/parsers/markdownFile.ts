import fs from "node:fs";

export type MarkdownFileResult = { available: boolean; markdown: string };

/**
 * Reads a single markdown artifact file (generate-report.md, load-failures.md,
 * portfolio-digest.md, HOW-TO-FILL-GAPS.md, coverage-report.md, ...). A
 * missing file is a normal "that pipeline step hasn't produced this yet"
 * state, never a thrown error -- every caller gets `available: false` and an
 * empty string back rather than a 404/500.
 */
export function readMarkdownFile(filePath: string): MarkdownFileResult {
  try {
    const markdown = fs.readFileSync(filePath, "utf8");
    return { available: true, markdown };
  } catch {
    return { available: false, markdown: "" };
  }
}
