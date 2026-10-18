import fs from "node:fs";
import path from "node:path";
import type { Workspace } from "../routes/workspaces.js";
import { gateReportsRoot } from "../parsers/gateReports.js";
import {
  ETL_CORE_PATH,
  RUN_COVERAGE_SCRIPT,
  RUN_PIPELINE_SCRIPT,
  VALIDATOR_ROOT,
  VALIDATOR_VENV_PYTHON,
  validatorInvocation,
  resolveSsisx,
  resolveSvk,
} from "./toolResolver.js";

export const STEPS = [
  "extract",
  "walkthrough",
  "sampledata",
  "generate",
  "apply-fills",
  "apply-tests",
  "build",
  "coverage",
  "validate",
  "ai-context",
  "final-summary",
  "validation",
  "all",
] as const;

export type Step = (typeof STEPS)[number];

/**
 * One OS process in a run. A run is a list of these, executed in order.
 * `step` is the logical step this command belongs to, so its exit code is
 * interpreted by that step's own rules (see exitCodes.ts). `bootstrap` marks
 * setup work (creating the validator venv) where any nonzero exit is a hard
 * failure and stops the run.
 */
export type ResolvedCommand = {
  step: Step;
  command: string;
  args: string[];
  cwd?: string;
  bootstrap?: boolean;
};

// The three CLI stages each get their own subfolder under the workspace's
// outputPath so they never collide on disk.
function extractOut(ws: Workspace) {
  return path.join(ws.outputPath, "extract");
}
function svkOut(ws: Workspace) {
  return path.join(ws.outputPath, "svk");
}
function genOut(ws: Workspace) {
  return path.join(ws.outputPath, "gen");
}

/**
 * The folder a step's own output actually lands in -- the single source of
 * truth for both `buildCommands`'s own `--out`/`-GeneratedRoot` arguments
 * and the "open in File Explorer" action (routes/system.ts), so the two can
 * never disagree about where a step writes.
 */
export function outputFolderForStep(ws: Workspace, step: Step): string {
  switch (step) {
    case "extract":
      return extractOut(ws);
    case "walkthrough":
      return path.join(svkOut(ws), "walkthrough");
    case "sampledata":
      return path.join(svkOut(ws), "sampledata");
    case "generate":
    case "apply-fills":
    case "apply-tests":
    case "build":
    case "coverage":
      return path.join(genOut(ws), "generate");
    case "validate":
    case "ai-context":
    case "final-summary":
    case "validation":
      return gateReportsRoot(ws.outputPath);
    case "all":
      return ws.outputPath;
  }
}

/** Packages Extract has produced a spec for, i.e. the packages this workspace knows about. */
export function listPackages(ws: Workspace): string[] {
  const dir = path.join(extractOut(ws), "packages");
  try {
    return fs
      .readdirSync(dir)
      .filter((n) => n.endsWith(".spec.json"))
      .map((n) => n.slice(0, -".spec.json".length))
      .sort();
  } catch {
    return [];
  }
}

const SKIP_DIRS = new Set(["bin", "obj", "node_modules", ".git", ".venv"]);

/**
 * Finds `<pkg>.dtsx` anywhere under the workspace's Input Path. The validator
 * needs the real .dtsx file itself, and Extract's spec does not record the
 * source path, so the input folder is searched by file name (depth-limited,
 * build output skipped).
 */
export function findDtsx(root: string, pkg: string): string | null {
  const target = `${pkg}.dtsx`.toLowerCase();
  const walk = (dir: string, depth: number): string | null => {
    if (depth > 8) return null;
    let entries: fs.Dirent[];
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return null;
    }
    for (const e of entries) {
      if (e.isFile() && e.name.toLowerCase() === target) return path.join(dir, e.name);
    }
    for (const e of entries) {
      if (e.isDirectory() && !SKIP_DIRS.has(e.name.toLowerCase())) {
        const hit = walk(path.join(dir, e.name), depth + 1);
        if (hit) return hit;
      }
    }
    return null;
  };
  return walk(root, 0);
}

function targetPackages(ws: Workspace, pkg: string | undefined): string[] {
  if (pkg) return [pkg];
  const all = listPackages(ws);
  if (all.length === 0) {
    throw new Error("No packages extracted yet -- run Extract first.");
  }
  return all;
}

function validatorBootstrap(): ResolvedCommand[] {
  if (validatorInvocation().usesOverride) return [];
  if (fs.existsSync(VALIDATOR_VENV_PYTHON)) return [];
  return [
    {
      step: "validation",
      bootstrap: true,
      command: "python",
      args: ["-m", "venv", ".venv"],
      cwd: VALIDATOR_ROOT,
    },
    {
      step: "validation",
      bootstrap: true,
      command: VALIDATOR_VENV_PYTHON,
      args: ["-m", "pip", "install", "-e", ".[dev]"],
      cwd: VALIDATOR_ROOT,
    },
  ];
}

/** One validator stage for one package. Throws if the package's .dtsx cannot be found. */
function validatorCommand(
  ws: Workspace,
  step: "validate" | "ai-context" | "final-summary",
  pkg: string,
): ResolvedCommand {
  const root = gateReportsRoot(ws.outputPath);
  const generated = path.join(genOut(ws), "generate", pkg);
  const inv = validatorInvocation();
  const base = { command: inv.command, cwd: VALIDATOR_ROOT, step };

  if (step === "final-summary") {
    return {
      ...base,
      args: [...inv.prefix, "final-summary", "--package", pkg, "--reports-root", root],
    };
  }

  const dtsx = findDtsx(ws.inputPath, pkg);
  if (!dtsx) {
    throw new Error(`No ${pkg}.dtsx found under the Input Path (${ws.inputPath}).`);
  }
  if (step === "validate") {
    return {
      ...base,
      args: [...inv.prefix, "validate", "--dtsx", dtsx, "--generated", generated, "--out", root],
    };
  }
  return {
    ...base,
    args: [
      ...inv.prefix,
      "ai-context",
      "--dtsx",
      dtsx,
      "--generated",
      generated,
      "--out",
      root,
      "--combined",
    ],
  };
}

/**
 * Every OS process a step needs, in order. Phase 1 steps are always one
 * command; the validation steps expand to one command per package (or per
 * stage per package for "validation"), prefixed by venv bootstrap when the
 * validator has never been set up on this machine.
 */
export function buildCommands(ws: Workspace, step: Step, pkg?: string): ResolvedCommand[] {
  switch (step) {
    case "validate":
    case "ai-context":
    case "final-summary":
      return [
        ...validatorBootstrap(),
        ...targetPackages(ws, pkg).map((p) => validatorCommand(ws, step, p)),
      ];
    case "validation":
      return [
        ...validatorBootstrap(),
        // Final Summary is deliberately not here: it runs after the AI review.
        ...targetPackages(ws, pkg).flatMap((p) => [
          validatorCommand(ws, "validate", p),
          validatorCommand(ws, "ai-context", p),
        ]),
      ];
    default:
      return [buildPhase1Command(ws, step, pkg)];
  }
}

function buildPhase1Command(ws: Workspace, step: Step, pkg?: string): ResolvedCommand {
  const pkgArgs = pkg ? ["--package", pkg] : [];

  switch (step) {
    case "extract": {
      const { command, baseArgs } = resolveSsisx();
      return {
        step,
        command,
        args: [
          ...baseArgs,
          "extract",
          "--input",
          ws.inputPath,
          "--out",
          extractOut(ws),
          "--recursive",
          ...pkgArgs,
        ],
      };
    }
    case "walkthrough": {
      const { command, baseArgs } = resolveSvk();
      return {
        step,
        command,
        args: [
          ...baseArgs,
          "walkthrough",
          "--spec",
          extractOut(ws),
          "--out",
          svkOut(ws),
          ...pkgArgs,
        ],
      };
    }
    case "sampledata": {
      const { command, baseArgs } = resolveSvk();
      return {
        step,
        command,
        args: [
          ...baseArgs,
          "sampledata",
          "--spec",
          extractOut(ws),
          "--out",
          svkOut(ws),
          ...pkgArgs,
        ],
      };
    }
    case "generate": {
      const { command, baseArgs } = resolveSsisx();
      return {
        step,
        command,
        args: [
          ...baseArgs,
          "generate",
          "--input",
          ws.inputPath,
          "--out",
          genOut(ws),
          "--recursive",
          "--etl-core",
          ETL_CORE_PATH,
          "--framework",
          ws.framework,
          "--fills",
          ws.fillsPath,
          ...pkgArgs,
        ],
      };
    }
    case "apply-fills": {
      const { command, baseArgs } = resolveSsisx();
      return {
        step,
        command,
        args: [
          ...baseArgs,
          "apply-fills",
          "--out",
          genOut(ws),
          "--fills",
          ws.fillsPath,
        ],
      };
    }
    case "apply-tests": {
      const { command, baseArgs } = resolveSsisx();
      return {
        step,
        command,
        args: [
          ...baseArgs,
          "apply-tests",
          "--out",
          genOut(ws),
          "--fills",
          ws.fillsPath,
        ],
      };
    }
    case "build": {
      return {
        step,
        command: "dotnet",
        args: [
          "build",
          path.join(genOut(ws), "generate", "Generated.slnx"),
          "-c",
          "Release",
        ],
      };
    }
    case "coverage": {
      const args = [
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        RUN_COVERAGE_SCRIPT,
        "-GeneratedRoot",
        path.join(genOut(ws), "generate"),
      ];
      if (pkg) {
        args.push("-Package", pkg);
      }
      return { step, command: "powershell.exe", args };
    }
    case "all": {
      const args = [
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        RUN_PIPELINE_SCRIPT,
        "-InputPath",
        ws.inputPath,
        "-OutputPath",
        ws.outputPath,
        "-FillsPath",
        ws.fillsPath,
        "-Framework",
        ws.framework,
        "-Quiet",
      ];
      if (pkg) {
        args.push("-Package", pkg);
      }
      return { step, command: "powershell.exe", args };
    }
    default:
      throw new Error(`Step ${step} has no Phase 1 command`);
  }
}
