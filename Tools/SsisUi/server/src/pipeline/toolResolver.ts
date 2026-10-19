import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { db } from "../db.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// server/src/pipeline/toolResolver.ts -> up 4 levels lands on Tools/.
export const TOOLS_ROOT = path.resolve(__dirname, "..", "..", "..", "..");

export const RUN_PIPELINE_SCRIPT = path.join(
  TOOLS_ROOT,
  "SsisExtractor",
  "scripts",
  "Run-Pipeline.ps1",
);
export const RUN_COVERAGE_SCRIPT = path.join(
  TOOLS_ROOT,
  "SsisExtractor",
  "scripts",
  "Run-Coverage.ps1",
);
export const ETL_CORE_PATH = path.join(TOOLS_ROOT, "Etl.Core");

// SSIS_RewriteValidationTool is a Python project run as `python -m
// ssis_validator.cli` from its own folder. The venv is created on first use
// by the validation commands (see commands.ts validatorBootstrap).
export const VALIDATOR_ROOT = path.join(TOOLS_ROOT, "SSIS_RewriteValidationTool");
export const VALIDATOR_VENV_PYTHON = path.join(VALIDATOR_ROOT, ".venv", "Scripts", "python.exe");

export type ResolvedTool = { command: string; baseArgs: string[] };

export type ToolName = "ssisx" | "svk";

// Settings-table keys an override is stored under -- see server/src/routes/settings.ts.
const OVERRIDE_KEYS: Record<ToolName, string> = {
  ssisx: "ssisxExePath",
  svk: "svkExePath",
};

export function getOverride(tool: ToolName): string | null {
  const row = db
    .prepare("SELECT value FROM settings WHERE key = ?")
    .get(OVERRIDE_KEYS[tool]) as { value: string } | undefined;
  return row?.value ?? null;
}

/** `null`/empty clears the override, reverting to auto-detect. */
export function setOverride(tool: ToolName, value: string | null): void {
  const key = OVERRIDE_KEYS[tool];
  if (!value) {
    db.prepare("DELETE FROM settings WHERE key = ?").run(key);
    return;
  }
  db.prepare(
    `INSERT INTO settings (key, value) VALUES (?, ?)
     ON CONFLICT(key) DO UPDATE SET value = excluded.value`,
  ).run(key, value);
}

export type ResolutionMode = "override" | "published" | "dotnet-run" | "venv";
export type ResolutionInfo = { mode: ResolutionMode; path: string };

const VALIDATOR_OVERRIDE_KEY = "validatorExePath";

export function getValidatorOverride(): string | null {
  const row = db
    .prepare("SELECT value FROM settings WHERE key = ?")
    .get(VALIDATOR_OVERRIDE_KEY) as { value: string } | undefined;
  const value = row?.value ?? null;
  return value && fs.existsSync(value) ? value : null;
}

/** `null`/empty clears the override, reverting to the venv's `python -m` path. */
export function setValidatorOverride(value: string | null): void {
  if (!value) {
    db.prepare("DELETE FROM settings WHERE key = ?").run(VALIDATOR_OVERRIDE_KEY);
    return;
  }
  db.prepare(
    `INSERT INTO settings (key, value) VALUES (?, ?)
     ON CONFLICT(key) DO UPDATE SET value = excluded.value`,
  ).run(VALIDATOR_OVERRIDE_KEY, value);
}

/** Full invocation prefix for the validator CLI: a published `ssis_validator.exe`
 * when overridden, otherwise the venv's own interpreter running the module. */
export function validatorInvocation(): { command: string; prefix: string[]; usesOverride: boolean } {
  const override = getValidatorOverride();
  if (override) return { command: override, prefix: [], usesOverride: true };
  return {
    command: VALIDATOR_VENV_PYTHON,
    prefix: ["-m", "ssis_validator.cli"],
    usesOverride: false,
  };
}

/**
 * Mirrors Run-Pipeline.ps1's own Resolve-Tool logic exactly: prefer a
 * published exe sitting next to the scripts folder; fall back to
 * `dotnet run --project <csproj> -c Release --` otherwise (the expected
 * path on a dev machine with no publish step run yet). A Settings-page
 * override, when set AND the file genuinely still exists on disk, wins
 * over both -- this is the fast path for someone who manually published
 * the exes somewhere auto-detection doesn't look.
 */
function resolveTool(
  tool: ToolName,
  exeName: string,
  csprojRelativeToScripts: string,
): ResolvedTool {
  const override = getOverride(tool);
  if (override && fs.existsSync(override)) {
    return { command: override, baseArgs: [] };
  }
  const scriptsDir = path.join(TOOLS_ROOT, "SsisExtractor", "scripts");
  const exePath = path.join(scriptsDir, exeName);
  if (fs.existsSync(exePath)) {
    return { command: exePath, baseArgs: [] };
  }
  const csprojPath = path.join(scriptsDir, csprojRelativeToScripts);
  return {
    command: "dotnet",
    baseArgs: ["run", "--project", csprojPath, "-c", "Release", "--"],
  };
}

const SSISX_EXE = "ssisx.exe";
const SSISX_CSPROJ = path.join(
  "..",
  "src",
  "Ssis.Extract.Cli",
  "Ssis.Extract.Cli.csproj",
);
const SVK_EXE = "svk.exe";
const SVK_CSPROJ = path.join(
  "..",
  "..",
  "SsisValidationKit",
  "src",
  "SvkCli",
  "SvkCli.csproj",
);

export function resolveSsisx(): ResolvedTool {
  return resolveTool("ssisx", SSISX_EXE, SSISX_CSPROJ);
}

export function resolveSvk(): ResolvedTool {
  return resolveTool("svk", SVK_EXE, SVK_CSPROJ);
}

function describeOne(
  tool: ToolName,
  exeName: string,
  csprojRelativeToScripts: string,
): ResolutionInfo {
  const override = getOverride(tool);
  if (override && fs.existsSync(override)) {
    return { mode: "override", path: override };
  }
  const scriptsDir = path.join(TOOLS_ROOT, "SsisExtractor", "scripts");
  const exePath = path.join(scriptsDir, exeName);
  if (fs.existsSync(exePath)) {
    return { mode: "published", path: exePath };
  }
  const csprojPath = path.join(scriptsDir, csprojRelativeToScripts);
  return {
    mode: "dotnet-run",
    path: `dotnet run --project ${csprojPath} -c Release --`,
  };
}

/** What's actually in effect right now for each tool -- what the Settings page shows. */
export function describeResolution(): Record<ToolName, ResolutionInfo> & { validator: ResolutionInfo } {
  const inv = validatorInvocation();
  return {
    ssisx: describeOne("ssisx", SSISX_EXE, SSISX_CSPROJ),
    svk: describeOne("svk", SVK_EXE, SVK_CSPROJ),
    validator: inv.usesOverride
      ? { mode: "override", path: inv.command }
      : { mode: "venv", path: `${inv.command} ${inv.prefix.join(" ")}` },
  };
}

/** The exe path each tool's Settings box shows when no override is saved: where the
 * published exe is expected (ssisx/svk, next to the scripts folder), or the venv
 * interpreter the validator actually runs with. Auto-detection looks at these same
 * locations, so the box and the resolver can never disagree. */
export function defaultExePaths(): { ssisx: string; svk: string; validator: string } {
  const scriptsDir = path.join(TOOLS_ROOT, "SsisExtractor", "scripts");
  return {
    ssisx: path.join(scriptsDir, SSISX_EXE),
    svk: path.join(scriptsDir, SVK_EXE),
    validator: VALIDATOR_VENV_PYTHON,
  };
}

// --- Publish script (Publish-Survey.ps1) -----------------------------------
// Builds the self-contained ssisx.exe into the scripts folder. When no ssisx exe
// exists, a pipeline run runs this first (see commands.ts publishBootstrap).
const PUBLISH_SCRIPT_KEY = "publishScriptPath";

export function defaultPublishScript(): string {
  return path.join(TOOLS_ROOT, "SsisExtractor", "scripts", "Publish-Survey.ps1");
}

export function getPublishScriptOverride(): string | null {
  const row = db
    .prepare("SELECT value FROM settings WHERE key = ?")
    .get(PUBLISH_SCRIPT_KEY) as { value: string } | undefined;
  const value = row?.value ?? null;
  return value && fs.existsSync(value) ? value : null;
}

/** `null`/empty clears the saved path, reverting to the checked-in default. */
export function setPublishScriptOverride(value: string | null): void {
  if (!value) {
    db.prepare("DELETE FROM settings WHERE key = ?").run(PUBLISH_SCRIPT_KEY);
    return;
  }
  db.prepare(
    `INSERT INTO settings (key, value) VALUES (?, ?)
     ON CONFLICT(key) DO UPDATE SET value = excluded.value`,
  ).run(PUBLISH_SCRIPT_KEY, value);
}

/** The publish script a run may execute: the saved path if it still exists, else the
 * checked-in default if present, else null (no publish possible). */
export function resolvePublishScript(): string | null {
  const override = getPublishScriptOverride();
  if (override) return override;
  const def = defaultPublishScript();
  return fs.existsSync(def) ? def : null;
}

/** True when no ssisx.exe is found anywhere, so ssisx would run through `dotnet run`. */
export function ssisxExeMissing(): boolean {
  return resolveSsisx().command === "dotnet";
}