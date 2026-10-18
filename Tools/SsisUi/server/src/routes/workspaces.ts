import { Router } from "express";
import { randomUUID } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { z } from "zod";
import { db } from "../db.js";

const router = Router();

const workspaceSchema = z.object({
  name: z.string().min(1, "Name is required"),
  inputPath: z.string().min(1, "Input path is required"),
  outputPath: z.string().min(1, "Output path is required"),
  fillsPath: z.string().min(1, "Fills path is required"),
  framework: z.enum(["net8.0", "net10.0"], {
    errorMap: () => ({ message: "Framework must be 'net8.0' or 'net10.0'" }),
  }),
});

type WorkspaceRow = {
  id: string;
  name: string;
  input_path: string;
  output_path: string;
  fills_path: string;
  framework: string;
  created_at: string;
  updated_at: string;
};

export type Workspace = ReturnType<typeof toWire>;

// Maps the DB's snake_case columns onto the camelCase shape the client expects
// over the wire. Never expose raw column names to callers.
function toWire(row: WorkspaceRow) {
  return {
    id: row.id,
    name: row.name,
    inputPath: row.input_path,
    outputPath: row.output_path,
    fillsPath: row.fills_path,
    framework: row.framework,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

// Shared lookup for other modules (e.g. the pipeline runner) that need a
// workspace row in its wire shape without going through the HTTP layer.
export function findWorkspace(id: string): Workspace | undefined {
  const row = db
    .prepare("SELECT * FROM workspaces WHERE id = ?")
    .get(id) as WorkspaceRow | undefined;
  return row ? toWire(row) : undefined;
}

router.get("/", (_req, res) => {
  // Latest run per workspace, so the list can show when it last ran. Null for a
  // workspace that has never run a step.
  const rows = db
    .prepare(
      `SELECT w.*,
         (SELECT MAX(r.started_at) FROM runs r WHERE r.workspace_id = w.id) AS last_run_at
       FROM workspaces w
       ORDER BY w.updated_at DESC`,
    )
    .all() as (WorkspaceRow & { last_run_at: string | null })[];
  res.json(rows.map((row) => ({ ...toWire(row), lastRunAt: row.last_run_at })));
});

router.get("/:id", (req, res) => {
  const row = db
    .prepare("SELECT * FROM workspaces WHERE id = ?")
    .get(req.params.id) as WorkspaceRow | undefined;
  if (!row) {
    res.status(404).json({ error: "Workspace not found" });
    return;
  }
  res.json(toWire(row));
});

router.post("/", (req, res) => {
  const parsed = workspaceSchema.safeParse(req.body);
  if (!parsed.success) {
    res.status(400).json({ error: parsed.error.flatten().fieldErrors });
    return;
  }

  const { name, inputPath, outputPath, fillsPath, framework } = parsed.data;
  const id = randomUUID();
  const now = new Date().toISOString();

  db.prepare(
    `INSERT INTO workspaces (id, name, input_path, output_path, fills_path, framework, created_at, updated_at)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
  ).run(id, name, inputPath, outputPath, fillsPath, framework, now, now);

  const row = db
    .prepare("SELECT * FROM workspaces WHERE id = ?")
    .get(id) as WorkspaceRow;
  res.status(201).json(toWire(row));
});

router.put("/:id", (req, res) => {
  const existing = db
    .prepare("SELECT * FROM workspaces WHERE id = ?")
    .get(req.params.id) as WorkspaceRow | undefined;
  if (!existing) {
    res.status(404).json({ error: "Workspace not found" });
    return;
  }

  const parsed = workspaceSchema.safeParse(req.body);
  if (!parsed.success) {
    res.status(400).json({ error: parsed.error.flatten().fieldErrors });
    return;
  }

  const { name, inputPath, outputPath, fillsPath, framework } = parsed.data;
  const now = new Date().toISOString();

  db.prepare(
    `UPDATE workspaces
     SET name = ?, input_path = ?, output_path = ?, fills_path = ?, framework = ?, updated_at = ?
     WHERE id = ?`,
  ).run(name, inputPath, outputPath, fillsPath, framework, now, req.params.id);

  const row = db
    .prepare("SELECT * FROM workspaces WHERE id = ?")
    .get(req.params.id) as WorkspaceRow;
  res.json(toWire(row));
});

// This only ever removes the saved workspace CONFIG row from ssisui.db. It
// never touches the input/output/fills folders on disk -- actual file
// deletion is a separate, later concern with its own confirmation story.
/** True when `child` is `parent` itself or sits somewhere beneath it. Compared
 * case-insensitively, since Windows paths are. */
function isSameOrInside(child: string, parent: string): boolean {
  const c = path.resolve(child).toLowerCase();
  const p = path.resolve(parent).toLowerCase();
  return c === p || c.startsWith(p.endsWith(path.sep) ? p : p + path.sep);
}

// Deletes the workspace's output folder from disk. Refuses any path that could
// take source packages or hand-written fills with it: the input or fills
// folder, anything containing them, anything inside them, or a drive root.
function removeOutputFolder(ws: Workspace): string | null {
  const out = path.resolve(ws.outputPath);
  if (path.parse(out).root === out) return "Refusing to delete a drive root.";
  for (const keep of [ws.inputPath, ws.fillsPath]) {
    if (!keep) continue;
    if (isSameOrInside(keep, out) || isSameOrInside(out, keep)) {
      return `Refusing to delete the output folder: it overlaps the ${keep === ws.inputPath ? "input" : "fills"} folder.`;
    }
  }
  fs.rmSync(out, { recursive: true, force: true });
  return null;
}

router.delete("/:id", (req, res) => {
  const ws = findWorkspace(req.params.id);
  if (!ws) {
    res.status(404).json({ error: "Workspace not found" });
    return;
  }

  // Check the folder before touching the database, so a refused delete leaves
  // the workspace entry in place for the user to fix.
  if (req.query.deleteOutput === "true") {
    const refusal = removeOutputFolder(ws);
    if (refusal) {
      res.status(409).json({ error: refusal });
      return;
    }
  }

  db.prepare("DELETE FROM workspaces WHERE id = ?").run(req.params.id);
  res.status(204).end();
});

export default router;
