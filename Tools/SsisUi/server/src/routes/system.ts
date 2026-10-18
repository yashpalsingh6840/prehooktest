import { Router, type Request, type Response } from "express";
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { z } from "zod";
import { findWorkspace, type Workspace } from "./workspaces.js";
import { isValidStep } from "../pipeline/runner.js";
import { outputFolderForStep } from "../pipeline/commands.js";

// Mounted at /api/workspaces/:id/system (mergeParams so :id from the parent
// mount is visible here). Every action here is local-machine-only by design
// (this UI is a single-developer, localhost-only tool, per its own README)
// -- "open this in Explorer/VS Code" has no meaning for a remote deployment.
const router = Router({ mergeParams: true });

function requireWorkspace(req: Request, res: Response): Workspace | undefined {
  const ws = findWorkspace(req.params.id);
  if (!ws) {
    res.status(404).json({ error: "Workspace not found" });
    return undefined;
  }
  return ws;
}

function generateDir(outputPath: string) {
  return path.join(outputPath, "gen", "generate");
}

/** Spawns Explorer on `folder` and responds -- shared by every route here
 * that ends in "open this folder", once each has decided the folder is
 * safe to open and exists. */
function openInExplorer(folder: string, res: Response): void {
  if (!fs.existsSync(folder)) {
    res.status(404).json({ error: `This folder doesn't exist on disk yet.`, path: folder });
    return;
  }
  if (process.platform !== "win32") {
    res.status(501).json({ error: "Opening a folder is only supported on Windows." });
    return;
  }
  // explorer.exe is unusually forgiving about its exit code (it can return
  // nonzero even on a genuine success), so the response just reflects that
  // the request was launched, not that Explorer itself reports success.
  spawn("explorer.exe", [folder], { windowsHide: false, detached: true }).unref();
  res.json({ ok: true, path: folder });
}

const openFolderSchema = z.object({ step: z.string().min(1) });

router.post("/open-folder", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const parsed = openFolderSchema.safeParse(req.body);
  if (!parsed.success || !isValidStep(parsed.data.step)) {
    res.status(400).json({ error: "Unknown step" });
    return;
  }

  openInExplorer(outputFolderForStep(ws, parsed.data.step), res);
});

const openPathSchema = z.object({ path: z.string().min(1) });

// Opens an arbitrary folder in Explorer -- used by the workspace header's
// "contextual path" indicator, which can point at the workspace's Output
// Path, a subfolder of it, or its separate Fills Path, depending on which
// tab is active. Restricted to a path that resolves under one of this
// workspace's own three configured roots (outputPath/fillsPath/inputPath) --
// never an arbitrary filesystem path a client could otherwise ask this
// server to open.
router.post("/open-path", (req, res) => {
  const ws = requireWorkspace(req, res);
  if (!ws) return;

  const parsed = openPathSchema.safeParse(req.body);
  if (!parsed.success) {
    res.status(400).json({ error: "A path is required" });
    return;
  }

  const resolved = path.resolve(parsed.data.path);
  const roots = [ws.outputPath, ws.fillsPath, ws.inputPath].map((r) => path.resolve(r));
  const allowed = roots.some((root) => {
    const withSep = root.endsWith(path.sep) ? root : root + path.sep;
    return resolved === root || resolved.startsWith(withSep);
  });
  if (!allowed) {
    res.status(400).json({ error: "This path is outside the workspace's own folders" });
    return;
  }

  openInExplorer(resolved, res);
});

// IMPORTANT: a trailing-wildcard route, same Express 4 path-to-regexp caveat
// documented in routes/artifacts.ts's own /generate-file/* route -- the
// matched remainder lands in req.params[0], never a named capture, and the
// same resolve+startsWith escape guard applies for the same reason (relPath
// is attacker-shaped input from the client, even though the only "attacker"
// in this single-developer tool is a typo).
router.post("/open-file/*", (req, res) => {
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
  if (!fs.existsSync(resolved)) {
    res.status(404).json({ error: "File not found" });
    return;
  }

  // `code` is VS Code's own CLI launcher, added to PATH by its installer's
  // "Add to PATH" option (on by default) -- not guaranteed present, so a
  // launch failure is reported back rather than left to throw unhandled.
  const child = spawn("code", [resolved], { windowsHide: false, shell: true });
  child.on("error", (err) => {
    if (!res.headersSent) {
      res.status(500).json({
        error:
          `Couldn't launch VS Code ('code' command): ${err.message}. ` +
          `In VS Code, run "Shell Command: Install 'code' command in PATH" from the Command Palette.`,
      });
    }
  });
  // spawn's "error" event fires asynchronously; a successful launch gives no
  // equivalent "it worked" signal (code exits immediately once the window is
  // handed off). Give the common failure case (the 'code' command missing
  // from PATH, surfaced as a near-immediate ENOENT) a short window to land
  // before responding optimistically.
  setTimeout(() => {
    if (!res.headersSent) {
      res.json({ ok: true, path: resolved });
    }
  }, 200);
});

export default router;
