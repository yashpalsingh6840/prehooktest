import type { Workspace } from "../routes/workspaces.js";
import { resolveSsisx } from "./toolResolver.js";

/**
 * The one instruction builder behind every AI-authored popup (Apply Fills, Apply
 * Tests). Each feature supplies only what is genuinely its own: its packages, its
 * rules, and its steps. The header, paths, scope line, and the `ssisx` command
 * line are built here once, so the two popups cannot drift apart.
 */

export type AiInstructions<P extends { name: string }> = {
  command: string;
  /** Every package the feature can work on, regardless of the selection -- drives the picker. */
  allPackages: P[];
  /** The packages the body below is scoped to. */
  packages: P[];
  body: string;
};

/** What a feature's rules and steps can refer to. */
export type InstructionContext = {
  ws: Workspace;
  genOut: string;
  /** The full `ssisx <subcommand> --out ... --fills ...` line for this feature. */
  applyCommand: string;
};

export type InstructionFrame<P> = {
  /** First line of the body, e.g. "Apply fills". Shown as "<title> for workspace \"<name>\"". */
  title: string;
  /** The one package line shown when nothing is in scope. */
  noPackagesLine: string;
  packageLine: (p: P) => string;
  rules: (ctx: InstructionContext) => string[];
  steps: (ctx: InstructionContext) => string[];
};

function quote(p: string): string {
  return p.includes(" ") ? `"${p}"` : p;
}

/** The exact command line for `ssisx <subcommand> --out <genOut> --fills <fillsPath>`. */
export function ssisxCommandLine(ws: Workspace, genOut: string, subcommand: string): string {
  const { command, baseArgs } = resolveSsisx();
  return [
    quote(command),
    ...baseArgs.map(quote),
    subcommand,
    "--out",
    quote(genOut),
    "--fills",
    quote(ws.fillsPath),
  ].join(" ");
}

/**
 * Builds one popup's instructions. `selected` scopes the body to those package
 * names. Omitted (or every package selected) means the whole-workspace body.
 * Unknown names are ignored, so only real packages ever reach the text.
 */
export function buildAiInstructions<P extends { name: string }>(
  ws: Workspace,
  genOut: string,
  subcommand: string,
  allPackages: P[],
  selected: string[] | undefined,
  frame: InstructionFrame<P>,
): AiInstructions<P> {
  const applyCommand = ssisxCommandLine(ws, genOut, subcommand);
  const known = new Set(allPackages.map((p) => p.name));
  const scopedNames = selected?.filter((n) => known.has(n));
  const scoped = scopedNames !== undefined && scopedNames.length < allPackages.length;
  const packages = scoped ? allPackages.filter((p) => scopedNames!.includes(p.name)) : allPackages;
  const ctx: InstructionContext = { ws, genOut, applyCommand };

  const packageLines = packages.length === 0 ? [frame.noPackagesLine] : packages.map(frame.packageLine);

  const body = [
    `# ${frame.title} for workspace "${ws.name}"`,
    "",
    "Use these paths exactly:",
    `- Generated output (--out): ${genOut}`,
    `- Fills folder (--fills): ${ws.fillsPath}`,
    `- Framework: ${ws.framework}`,
    "",
    scoped
      ? "Scope: work ONLY on the packages listed below. Do not touch any other package."
      : "Scope: all packages listed below.",
    "Packages:",
    ...packageLines,
    "",
    "## Rules",
    ...frame.rules(ctx),
    "",
    "## Steps",
    ...frame.steps(ctx),
  ].join("\n");

  return { command: applyCommand, allPackages, packages, body };
}
