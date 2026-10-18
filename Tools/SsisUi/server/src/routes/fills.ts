import { Router, type Request, type Response } from "express";
import fs from "node:fs";
import path from "node:path";
import { findWorkspace, type Workspace } from "./workspaces.js";
import { readGaps, type GapSpec } from "../parsers/gaps.js";

// Mounted at /api/workspaces/:id/fills (mergeParams so :id from the parent
// mount is visible here). This is the one place the UI writes arbitrary
// content to disk -- every path is resolved against the workspace's own
// fillsPath and rejected if it would escape that root.
const router = Router({ mergeParams: true });

function genOut(outputPath: string) {
  return path.join(outputPath, "gen");
}

function requireWorkspace(req: Request, res: Response): Workspace | undefined {
  const ws = findWorkspace(req.params.id);
  if (!ws) {
    res.status(404).json({ error: "Workspace not found" });
    return undefined;
  }
  return ws;
}

/**
 * Resolves a relative path against the workspace's fillsPath and guards
 * against escaping it (`../../../etc/passwd`-style). Returns null (and has
 * already written the 400 response) when the path is unsafe.
 */
function resolveSafe(ws: Workspace, relPath: string, res: Response): string | null {
  const root = path.resolve(ws.fillsPath);
  const resolved = path.resolve(root, relPath);
  const withSep = root.endsWith(path.sep) ? root : root + path.sep;
  if (resolved !== root && !resolved.startsWith(withSep)) {
    res.status(400).json({ error: "Path escapes the workspace's fills folder" });
    return null;
  }
  return resolved;
}

// Sanitize a gap's own Location into a safe filename fragment -- strip
// anything that isn't alphanumeric/underscore/dot, collapsing runs of
// disallowed characters to a single underscore.
function sanitizeForFileName(raw: string): string {
  return raw.replace(/[^A-Za-z0-9_.]+/g, "_").replace(/^_+|_+$/g, "") || "Gap";
}

/**
 * Per Ssis.Extract.Codegen.GapDecisions / Ssis.Extract.Model.Analysis.GapDecisionSpec:
 * - EncryptedConnectionManagerSecret / LookupJoinKey are "Tier-1 missing datum"
 *   gaps, answered by writing an entry into <fillsPath>/<Package>.decisions.json
 *   (a dictionary keyed by GapId), consumed by `ssisx generate` itself (NOT
 *   apply-fills) via its own --fills flag.
 * - ScriptTask / ScriptComponentColumn / TestOracle / LocalFileSourceData are
 *   answered by a FILE under <fillsPath>/<Package>/..., consumed by
 *   `ssisx apply-fills`.
 */
function suggestedPath(gap: GapSpec): string {
  switch (gap.Kind) {
    case "EncryptedConnectionManagerSecret":
    case "LookupJoinKey":
      return `${gap.Package}.decisions.json`;
    case "TestOracle":
      return `${gap.Package}/Tests/${sanitizeForFileName(gap.Location)}.Tests.cs`;
    case "LocalFileSourceData":
      // Matched by exact filename against GapSpec.ExpectedFileName -- not
      // free-form, since apply-fills has no header-comment convention for a
      // raw data file.
      return `${gap.Package}/TestData/${gap.ExpectedFileName ?? sanitizeForFileName(gap.Location)}`;
    case "ScriptTask":
    case "ScriptComponentColumn":
    default:
      return `${gap.Package}/${sanitizeForFileName(gap.Location)}.Fills.cs`;
  }
}

router.get("/suggest", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const gapId = req.query.gapId;
  if (typeof gapId !== "string" || gapId.length === 0) {
    res.status(400).json({ error: "gapId query parameter is required" });
    return;
  }

  const { gaps } = readGaps(path.join(ws.outputPath, "gen"));
  const gap = gaps.find((g) => g.GapId === gapId);
  if (!gap) {
    res.status(404).json({ error: "Gap not found" });
    return;
  }

  res.json({ path: suggestedPath(gap) });
});

// IMPORTANT: GET/PUT "/*" must be registered AFTER "/suggest" above, or
// "/suggest" itself would be swallowed as a relative path under fillsPath.
// Express 4's path-to-regexp (v0.1.x) has no NAMED wildcard capture -- the
// matched remainder lands in req.params[0], not a named property.

router.get("/*", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const relPath = (req.params as Record<string, string>)[0] ?? "";
  const resolved = resolveSafe(ws, relPath, res);
  if (resolved === null) return;

  try {
    const content = fs.readFileSync(resolved, "utf8");
    res.json({ content });
  } catch {
    res.status(404).json({ error: "File not found" });
  }
});

router.put("/*", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const relPath = (req.params as Record<string, string>)[0] ?? "";
  if (!relPath) {
    res.status(400).json({ error: "A file path is required" });
    return;
  }
  const resolved = resolveSafe(ws, relPath, res);
  if (resolved === null) return;

  const content = req.body?.content;
  if (typeof content !== "string") {
    res.status(400).json({ error: "body.content must be a string" });
    return;
  }

  fs.mkdirSync(path.dirname(resolved), { recursive: true });
  fs.writeFileSync(resolved, content, "utf8");
  res.json({ ok: true });
});

export default router;
