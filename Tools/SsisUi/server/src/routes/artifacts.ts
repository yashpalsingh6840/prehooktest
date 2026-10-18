import { Router, type Request, type Response } from "express";
import { z } from "zod";
import fs from "node:fs";
import path from "node:path";
import { spawn } from "node:child_process";
import { findWorkspace, type Workspace } from "./workspaces.js";
import { readGaps, readGapPacket } from "../parsers/gaps.js";
import { readFillsApplied } from "../parsers/fillsApplied.js";
import { readTestsApplied } from "../parsers/testsApplied.js";
import { readMarkdownFile } from "../parsers/markdownFile.js";
import { readClaims, updateClaim } from "../parsers/claims.js";
import { summarizePackageSpec } from "../parsers/packageSpec.js";
import { parseGenerateReport } from "../parsers/generateReport.js";
import {
  isValidPackageName,
  readPackageValidation,
  summarizeValidation,
  validationStatus,
} from "../parsers/gateReports.js";
import { buildApplyFillsInstructions } from "../pipeline/applyFillsInstructions.js";
import { buildApplyTestsInstructions } from "../pipeline/applyTestsInstructions.js";

// Mounted at /api/workspaces/:id/artifacts (mergeParams so :id from the
// parent mount is visible here).
const router = Router({ mergeParams: true });

function extractOut(outputPath: string) {
  return path.join(outputPath, "extract");
}
function svkOut(outputPath: string) {
  return path.join(outputPath, "svk");
}
function genOut(outputPath: string) {
  return path.join(outputPath, "gen");
}
function generateDir(outputPath: string) {
  return path.join(genOut(outputPath), "generate");
}

function requireWorkspace(req: Request, res: Response): Workspace | undefined {
  const ws = findWorkspace(req.params.id);
  if (!ws) {
    res.status(404).json({ error: "Workspace not found" });
    return undefined;
  }
  return ws;
}

/** True when `dir` exists and holds at least one entry passing `accept`. */
function hasEntry(dir: string, accept: (name: string) => boolean = () => true): boolean {
  try {
    return fs.readdirSync(dir).some(accept);
  } catch {
    return false;
  }
}

// Which pipeline steps have actually produced their output for this workspace.
// The sidebar uses this to hide a menu until its step has run. Checked against
// the files each step writes, so a run from the CLI counts too.
/** When `file` was last written, as ISO text, or null if it doesn't exist. */
function mtimeOf(file: string): string | null {
  try {
    return fs.statSync(file).mtime.toISOString();
  } catch {
    return null;
  }
}

router.get("/step-status", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  const gen = genOut(ws.outputPath);
  const extractPackages = path.join(extractOut(ws.outputPath), "packages");
  const walkthroughDir = path.join(svkOut(ws.outputPath), "walkthrough");
  const sampledataDir = path.join(svkOut(ws.outputPath), "sampledata");
  const gapsFile = path.join(gen, "gaps.json");
  const reportFile = path.join(gen, "generate-report.md");
  const fillsFile = path.join(gen, "fills-applied.json");
  const coverageFile = path.join(gen, "generate", "coverage-report.md");

  const validation = validationStatus(ws.outputPath);
  const done = {
    extract: hasEntry(extractPackages, (n) => n.endsWith(".spec.json")),
    walkthrough: hasEntry(walkthroughDir, (n) => n.endsWith(".md")),
    sampledata: hasEntry(sampledataDir),
    generate: fs.existsSync(gapsFile) || fs.existsSync(reportFile),
    applyFills: fs.existsSync(fillsFile),
    applyTests: fs.existsSync(path.join(genOut(ws.outputPath), "tests-applied.json")),
    coverage: fs.existsSync(coverageFile),
    validate: validation.validate.done,
    aiContext: validation.aiContext.done,
    aiReview: validation.aiReview.done,
    finalSummary: validation.finalSummary.done,
  };
  // When each step's output was last written. The Pipeline cards use this to
  // show "Ran <time>" for steps run outside this UI (e.g. by Copilot).
  const completedAt = {
    extract: mtimeOf(extractPackages),
    walkthrough: mtimeOf(walkthroughDir),
    sampledata: mtimeOf(sampledataDir),
    generate: mtimeOf(gapsFile) ?? mtimeOf(reportFile),
    applyFills: mtimeOf(fillsFile),
    applyTests: mtimeOf(path.join(gen, "tests-applied.json")),
    coverage: mtimeOf(coverageFile),
    validate: validation.validate.completedAt,
    aiContext: validation.aiContext.completedAt,
    aiReview: validation.aiReview.completedAt,
    finalSummary: validation.finalSummary.completedAt,
  };
  res.json({ done, completedAt });
});

// Validation (SSIS_RewriteValidationTool) output, read-only. A package that has
// never been validated is simply absent from the list / has null fields.
router.get("/validation", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  res.json(summarizeValidation(ws.outputPath));
});

router.get("/validation/:pkg", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  const pkg = req.params.pkg;
  if (!isValidPackageName(pkg)) {
    res.status(400).json({ error: "Invalid package name" });
    return;
  }
  res.json(readPackageValidation(ws.outputPath, pkg));
});

router.get("/gaps", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  res.json(readGaps(genOut(ws.outputPath)));
});

router.get("/gaps/:gapId/packet", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const { gaps } = readGaps(genOut(ws.outputPath));
  const gap = gaps.find((g) => g.GapId === req.params.gapId);
  if (!gap) {
    res.status(404).json({ error: "Gap not found" });
    return;
  }
  if (!gap.PacketPath) {
    res.status(404).json({ error: "This gap has no work packet" });
    return;
  }

  const markdown = readGapPacket(genOut(ws.outputPath), gap);
  if (markdown === null) {
    res.status(404).json({ error: "Work packet file not found on disk" });
    return;
  }
  res.json({ markdown });
});

router.get("/fills-status", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  res.json(readFillsApplied(genOut(ws.outputPath)));
});

router.get("/tests-applied", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  res.json(readTestsApplied(genOut(ws.outputPath)));
});

// The four generate/apply-fills output files the Fills page shows raw. Keys
// are a fixed whitelist so the :name param never reaches the filesystem as a
// path -- an unknown name is a 404, not a lookup.
const OUTPUT_FILES: Record<string, { file: string; kind: "markdown" | "json" | "text" }> = {
  gaps: { file: "gaps.json", kind: "json" },
  "fills-applied": { file: "fills-applied.json", kind: "json" },
  "tests-applied": { file: "tests-applied.json", kind: "json" },
  "apply-fills-log": { file: "apply-fills.log", kind: "text" },
};

router.get("/output-file/:name", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  const spec = OUTPUT_FILES[req.params.name];
  if (!spec) {
    res.status(404).json({ error: "Unknown output file" });
    return;
  }
  const filePath = path.join(genOut(ws.outputPath), spec.file);
  const result = readMarkdownFile(filePath);
  res.json({ ...result, path: filePath, kind: spec.kind });
});

// Opens Windows Explorer with the file highlighted. Same fixed whitelist as
// the read route above, so a request can only ever reveal one of these files.
router.post("/output-file/:name/reveal", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  const spec = OUTPUT_FILES[req.params.name];
  if (!spec) {
    res.status(404).json({ error: "Unknown output file" });
    return;
  }
  const filePath = path.join(genOut(ws.outputPath), spec.file);
  if (!fs.existsSync(filePath)) {
    res.status(404).json({ error: `Not produced yet: ${filePath}` });
    return;
  }
  // explorer.exe always exits 1 even on success, so the exit code is ignored.
  spawn("explorer.exe", [`/select,${filePath}`], { detached: true, stdio: "ignore" }).unref();
  res.json({ ok: true, path: filePath });
});

// Shared instruction body for the Apply Fills popup (Copilot and Claude both
// paste it; only their invocation line differs). Built from this workspace's
// own paths and gaps.json, so it never goes stale.
router.get("/apply-tests-instructions", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  // Optional ?packages=A,B scopes the body; omitted means every package.
  const raw = typeof req.query.packages === "string" ? req.query.packages : undefined;
  const selected = raw === undefined ? undefined : raw.split(",").filter((n) => n.length > 0);
  res.json(buildApplyTestsInstructions(ws, genOut(ws.outputPath), selected));
});

router.get("/apply-fills-instructions", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  const gen = genOut(ws.outputPath);
  const { gaps } = readGaps(gen);
  // Optional ?packages=A,B scopes the body; omitted means every package.
  const raw = typeof req.query.packages === "string" ? req.query.packages : undefined;
  const selected = raw === undefined ? undefined : raw.split(",").filter((n) => n.length > 0);
  res.json(buildApplyFillsInstructions(ws, gen, gaps, selected));
});

router.get("/report", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  res.json(readMarkdownFile(path.join(genOut(ws.outputPath), "generate-report.md")));
});

// Structured, per-section data parsed out of generate-report.md -- used by
// the per-package Generate Report view instead of rendering the raw markdown
// tables directly, which (a) can't distinguish "no gaps for this package"
// from "no gaps at all" per row, and (b) had no way to open the files it
// describes.
router.get("/generate-summary", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const result = readMarkdownFile(path.join(genOut(ws.outputPath), "generate-report.md"));
  if (!result.available) {
    res.json({ available: false });
    return;
  }
  res.json({ available: true, ...parseGenerateReport(result.markdown) });
});

router.get("/how-to-fill-gaps", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  res.json(readMarkdownFile(path.join(genOut(ws.outputPath), "HOW-TO-FILL-GAPS.md")));
});

router.get("/coverage", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  res.json(
    readMarkdownFile(
      path.join(genOut(ws.outputPath), "generate", "coverage-report.md"),
    ),
  );
});

// Written by `ssisx report` (a separate CLI command from `ssisx extract`),
// confirmed on disk at <extractOut>/metadata/report/load-failures.md --
// see ReportCommand.cs. This UI's own pipeline does not currently invoke
// `ssisx report` as a step, so this will read `available: false` for a
// workspace that has only ever run `extract`/`generate` through this UI;
// the endpoint still parses correctly if that file was produced some other
// way (e.g. a manual `ssisx report` run against the same --out).
router.get("/load-failures", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  res.json(
    readMarkdownFile(
      path.join(extractOut(ws.outputPath), "metadata", "report", "load-failures.md"),
    ),
  );
});

router.get("/portfolio-digest", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  res.json(
    readMarkdownFile(
      path.join(extractOut(ws.outputPath), "metadata", "report", "portfolio-digest.md"),
    ),
  );
});

router.get("/walkthrough", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const walkthroughDir = path.join(svkOut(ws.outputPath), "walkthrough");
  let entries: fs.Dirent[] = [];
  try {
    entries = fs.readdirSync(walkthroughDir, { withFileTypes: true });
  } catch {
    res.json({ available: false, packages: [] });
    return;
  }

  // Only "*.walkthrough.md" FILES directly in this folder -- the sibling
  // "claims/" subfolder (one .claims.json per package) is a directory, so
  // it's never matched by this filter and is never rendered this phase.
  let packages = entries
    .filter((e) => e.isFile() && e.name.endsWith(".walkthrough.md"))
    .map((e) => {
      const pkg = e.name.slice(0, -".walkthrough.md".length);
      const markdown = fs.readFileSync(path.join(walkthroughDir, e.name), "utf8");
      return { package: pkg, markdown };
    })
    .sort((a, b) => a.package.localeCompare(b.package));

  // `svk walkthrough` never deletes a stale file -- if a workspace's
  // `--input` is later changed and Extract re-run against the new input,
  // walkthrough.md files from a PREVIOUS input's packages stay on disk
  // forever and would otherwise still show up here and in the sidebar,
  // leading to a 404 the moment one is clicked (the package summary/sample
  // data endpoints read straight from the CURRENT extract output). Cross-
  // check against the current `extract/packages/*.spec.json` listing and
  // drop anything that's no longer there -- but only when that listing is
  // itself readable; if Extract hasn't been run for this output at all,
  // fail open rather than hiding every walkthrough.
  try {
    const currentPackages = new Set(
      fs
        .readdirSync(path.join(extractOut(ws.outputPath), "packages"))
        .filter((f) => f.endsWith(".spec.json"))
        .map((f) => f.slice(0, -".spec.json".length)),
    );
    packages = packages.filter((p) => currentPackages.has(p.package));
  } catch {
    // No current extract listing to check against -- show what's on disk.
  }

  res.json({ available: packages.length > 0, packages });
});

router.get("/packages", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const packagesDir = path.join(extractOut(ws.outputPath), "packages");
  let entries: string[] = [];
  try {
    entries = fs.readdirSync(packagesDir).filter((f) => f.endsWith(".spec.json"));
  } catch {
    res.json({ packages: [] });
    return;
  }
  const packages = entries
    .map((f) => f.slice(0, -".spec.json".length))
    .sort((a, b) => a.localeCompare(b));
  res.json({ packages });
});

/** A package name is used directly in a file path -- reject anything that
 * could escape the "packages" folder rather than relying on the ".spec.json"
 * suffix alone to contain it. */
function isSafePackageName(name: string): boolean {
  return !!name && !name.includes("/") && !name.includes("\\") && !name.includes("..");
}

function packageSpecPath(ws: Workspace, pkg: string) {
  return path.join(extractOut(ws.outputPath), "packages", `${pkg}.spec.json`);
}

router.get("/packages/:package", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  if (!isSafePackageName(req.params.package)) {
    res.status(400).json({ error: "Invalid package name" });
    return;
  }

  const specPath = packageSpecPath(ws, req.params.package);
  let spec: unknown;
  try {
    spec = JSON.parse(fs.readFileSync(specPath, "utf8"));
  } catch {
    res.status(404).json({ error: "No extracted spec found for this package" });
    return;
  }

  try {
    res.json(summarizePackageSpec(spec as Record<string, unknown>));
  } catch (e) {
    res.status(500).json({ error: e instanceof Error ? e.message : String(e) });
  }
});

// The raw extracted spec, verbatim -- for the "Extracted Data" page's
// pretty-tree/raw-JSON views. Deliberately separate from the summarized
// `/packages/:package` endpoint above (which only ever exposes the fields
// PackageComponents.tsx needs) so a reviewer can inspect anything the
// extractor captured, not just what this UI's own summary surfaces.
router.get("/packages/:package/raw", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  if (!isSafePackageName(req.params.package)) {
    res.status(400).json({ error: "Invalid package name" });
    return;
  }

  const specPath = packageSpecPath(ws, req.params.package);
  let raw: string;
  try {
    raw = fs.readFileSync(specPath, "utf8");
  } catch {
    res.status(404).json({ error: "No extracted spec found for this package" });
    return;
  }

  let json: unknown;
  try {
    json = JSON.parse(raw);
  } catch {
    res.status(500).json({ error: "The extracted spec file on disk is not valid JSON" });
    return;
  }

  res.json({ json, path: specPath });
});

// Aggregate counts across the whole workspace, for the Dashboard's portfolio
// charts -- deliberately read fresh from gaps.json/every package spec on
// each request rather than cached, matching this project's "everything
// except workspaces/run-history lives on disk, read live" design.
router.get("/portfolio-stats", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const { available: gapsAvailable, gaps } = readGaps(genOut(ws.outputPath));
  const gapsByTier: Record<string, number> = {};
  const gapsByKind: Record<string, number> = {};
  const gapsByPackage: Record<string, number> = {};
  let blockingGaps = 0;
  let advisoryGaps = 0;
  for (const g of gaps) {
    gapsByTier[g.Tier] = (gapsByTier[g.Tier] ?? 0) + 1;
    gapsByKind[g.Kind] = (gapsByKind[g.Kind] ?? 0) + 1;
    gapsByPackage[g.Package] = (gapsByPackage[g.Package] ?? 0) + 1;
    if (g.IsBlocking) blockingGaps++;
    else advisoryGaps++;
  }

  const packagesDir = path.join(extractOut(ws.outputPath), "packages");
  let specFiles: string[] = [];
  try {
    specFiles = fs.readdirSync(packagesDir).filter((f) => f.endsWith(".spec.json"));
  } catch {
    specFiles = [];
  }

  // Best-effort: one unreadable/unsummarizable spec is skipped, not fatal --
  // this endpoint feeds a dashboard chart, not a correctness-critical read.
  const roleCounts: Record<string, number> = {};
  for (const file of specFiles) {
    try {
      const spec = JSON.parse(fs.readFileSync(path.join(packagesDir, file), "utf8"));
      const summary = summarizePackageSpec(spec as Record<string, unknown>);
      for (const [role, count] of Object.entries(summary.roleCounts)) {
        roleCounts[role] = (roleCounts[role] ?? 0) + (count as number);
      }
    } catch {
      // skip
    }
  }

  res.json({
    gapsAvailable,
    totalGaps: gaps.length,
    blockingGaps,
    advisoryGaps,
    gapsByTier,
    gapsByKind,
    gapsByPackage,
    packageCount: specFiles.length,
    roleCounts,
  });
});

/** Recursively lists every FILE under `dir` (skipping bin/obj), as paths
 * relative to `root` with forward slashes -- so a path collected here is
 * directly usable, unmodified, against the /generate-file/* route below
 * (also rooted at the same `root`). */
function listFilesRecursive(root: string, dir: string, out: string[]) {
  let entries: fs.Dirent[];
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    if (entry.name === "bin" || entry.name === "obj") continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      listFilesRecursive(root, full, out);
    } else {
      out.push(path.relative(root, full).split(path.sep).join("/"));
    }
  }
}

router.get("/generate/:package/files", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  if (!isSafePackageName(req.params.package)) {
    res.status(400).json({ error: "Invalid package name" });
    return;
  }

  const root = generateDir(ws.outputPath);
  const productionFiles: string[] = [];
  listFilesRecursive(root, path.join(root, req.params.package), productionFiles);
  const testFiles: string[] = [];
  listFilesRecursive(root, path.join(root, `${req.params.package}.Tests`), testFiles);
  productionFiles.sort((a, b) => a.localeCompare(b));
  testFiles.sort((a, b) => a.localeCompare(b));

  res.json({
    available: productionFiles.length > 0 || testFiles.length > 0,
    productionFiles,
    testFiles,
  });
});

// IMPORTANT: a trailing-wildcard route, same Express 4 path-to-regexp caveat
// as /sampledata/:package/file/* above -- the matched remainder lands in
// req.params[0], never a named capture.
router.get("/generate-file/*", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const relPath = (req.params as Record<string, string>)[0] ?? "";
  const root = generateDir(ws.outputPath);
  const resolvedRoot = path.resolve(root);
  const resolved = path.resolve(resolvedRoot, relPath);
  const withSep = resolvedRoot.endsWith(path.sep) ? resolvedRoot : resolvedRoot + path.sep;
  if (resolved !== resolvedRoot && !resolved.startsWith(withSep)) {
    res.status(400).json({ error: "Path escapes the generate folder" });
    return;
  }

  try {
    const content = fs.readFileSync(resolved, "utf8");
    res.json({ content });
  } catch {
    res.status(404).json({ error: "File not found" });
  }
});

type SampleDataFile = { name: string; kind: string; sizeBytes: number };

function classifySampleDataFile(name: string): string {
  if (name === "schema.sql") return "schema-sql";
  if (name.endsWith(".expected.sql")) return "expected-sql";
  if (name.endsWith(".reference.sql")) return "reference-sql";
  if (name.endsWith(".sql")) return "sql";
  if (name.endsWith(".csv")) return "csv";
  if (name.endsWith(".xlsx")) return "excel";
  if (name.endsWith(".txt")) return "text";
  return "other";
}

const TEXT_KINDS = new Set(["sql", "expected-sql", "reference-sql", "schema-sql", "csv", "text"]);

function sampleDataDir(ws: Workspace, pkg: string) {
  return path.join(svkOut(ws.outputPath), "sampledata", pkg);
}

/** Resolves a relative path against a sample-data package root, rejecting
 * anything that would escape it. Returns null (and has already written the
 * 400 response) when the path is unsafe. */
function resolveSampleDataFile(root: string, relPath: string, res: Response): string | null {
  const resolvedRoot = path.resolve(root);
  const resolved = path.resolve(resolvedRoot, relPath);
  const withSep = resolvedRoot.endsWith(path.sep) ? resolvedRoot : resolvedRoot + path.sep;
  if (resolved !== resolvedRoot && !resolved.startsWith(withSep)) {
    res.status(400).json({ error: "Path escapes the sample-data folder" });
    return null;
  }
  return resolved;
}

router.get("/sampledata/:package", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const dir = sampleDataDir(ws, req.params.package);
  let names: string[] = [];
  try {
    names = fs.readdirSync(dir).filter((n) => fs.statSync(path.join(dir, n)).isFile());
  } catch {
    res.json({ available: false, files: [], path: dir });
    return;
  }

  const files: SampleDataFile[] = names
    .map((name) => ({
      name,
      kind: classifySampleDataFile(name),
      sizeBytes: fs.statSync(path.join(dir, name)).size,
    }))
    .sort((a, b) => a.name.localeCompare(b.name));

  res.json({ available: files.length > 0, files, path: dir });
});

// IMPORTANT: these two trailing-wildcard routes must stay AFTER
// "/sampledata/:package" above -- Express 4's path-to-regexp has no named
// wildcard capture, the matched remainder lands in req.params[0].

router.get("/sampledata/:package/file/*", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const relPath = (req.params as Record<string, string>)[0] ?? "";
  const dir = sampleDataDir(ws, req.params.package);
  const resolved = resolveSampleDataFile(dir, relPath, res);
  if (resolved === null) return;

  const kind = classifySampleDataFile(path.basename(resolved));
  if (!TEXT_KINDS.has(kind)) {
    let sizeBytes: number;
    try {
      sizeBytes = fs.statSync(resolved).size;
    } catch {
      res.status(404).json({ error: "File not found" });
      return;
    }
    res.json({ binary: true, sizeBytes });
    return;
  }

  try {
    const content = fs.readFileSync(resolved, "utf8");
    res.json({ binary: false, content });
  } catch {
    res.status(404).json({ error: "File not found" });
  }
});

router.get("/sampledata/:package/download/*", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const relPath = (req.params as Record<string, string>)[0] ?? "";
  const dir = sampleDataDir(ws, req.params.package);
  const resolved = resolveSampleDataFile(dir, relPath, res);
  if (resolved === null) return;

  res.download(resolved, (err) => {
    if (err && !res.headersSent) {
      res.status(404).json({ error: "File not found" });
    }
  });
});

const updateClaimSchema = z.object({
  ruleId: z.string().min(1),
  status: z.enum(["NotValidated", "Validated", "Blocked"]),
  reviewer: z.string(),
  note: z.string(),
});

router.get("/walkthrough/:package/claims", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;
  res.json(readClaims(svkOut(ws.outputPath), req.params.package));
});

router.put("/walkthrough/:package/claims", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const parsed = updateClaimSchema.safeParse(req.body);
  if (!parsed.success) {
    res.status(400).json({ error: parsed.error.issues.map((i) => i.message).join("; ") });
    return;
  }

  const { ruleId, status, reviewer, note } = parsed.data;
  const ok = updateClaim(svkOut(ws.outputPath), req.params.package, ruleId, {
    status,
    reviewer,
    note,
  });
  if (!ok) {
    res.status(404).json({ error: "No claim with that ruleId found for this package" });
    return;
  }

  res.json(readClaims(svkOut(ws.outputPath), req.params.package));
});

export default router;
