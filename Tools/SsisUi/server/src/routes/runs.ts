import { Router } from "express";
import fs from "node:fs";
import { z } from "zod";
import { findWorkspace } from "./workspaces.js";
import {
  cancelRun,
  getLiveEmitter,
  getRun,
  isValidStep,
  listRuns,
  startRun,
  type RunRow,
} from "../pipeline/runner.js";
import { describeStatus, type RunStatus } from "../pipeline/exitCodes.js";
import type { Step } from "../pipeline/commands.js";

const router = Router();

function toWire(row: RunRow) {
  return {
    id: row.id,
    workspaceId: row.workspace_id,
    step: row.step,
    package: row.package,
    status: row.status,
    exitCode: row.exit_code,
    startedAt: row.started_at,
    finishedAt: row.finished_at,
    logPath: row.log_path,
    statusDetail: describeStatus(
      row.step as Step,
      row.status as RunStatus,
      row.exit_code,
    ),
  };
}

const startRunSchema = z.object({
  workspaceId: z.string().min(1),
  step: z.string().min(1),
  package: z.string().min(1).optional(),
});

router.post("/", (req, res) => {
  const parsed = startRunSchema.safeParse(req.body);
  if (!parsed.success) {
    res.status(400).json({ error: parsed.error.flatten().fieldErrors });
    return;
  }

  const { workspaceId, step, package: pkg } = parsed.data;

  // The security boundary: a step name never reaches buildCommand/spawn
  // unless it's exactly one of the 8 whitelisted values.
  if (!isValidStep(step)) {
    res.status(400).json({ error: `Unknown step: ${step}` });
    return;
  }

  const ws = findWorkspace(workspaceId);
  if (!ws) {
    res.status(404).json({ error: "Workspace not found" });
    return;
  }

  // startRun throws before recording anything when a step cannot be set up at
  // all (e.g. a package's .dtsx cannot be found) -- report that as a plain 400.
  try {
    const row = startRun(ws, step, pkg);
    res.status(201).json(toWire(row));
  } catch (err) {
    res.status(400).json({ error: err instanceof Error ? err.message : String(err) });
  }
});

router.get("/", (req, res) => {
  const workspaceId = req.query.workspaceId;
  if (typeof workspaceId !== "string" || workspaceId.length === 0) {
    res.status(400).json({ error: "workspaceId query parameter is required" });
    return;
  }
  res.json(listRuns(workspaceId).map(toWire));
});

router.post("/:id/cancel", (req, res) => {
  const result = cancelRun(req.params.id);
  if (!result.ok) {
    res.status(409).json({ error: result.message ?? "Cannot cancel this run" });
    return;
  }
  res.json(toWire(getRun(req.params.id)!));
});

router.get("/:id", (req, res) => {
  const row = getRun(req.params.id);
  if (!row) {
    res.status(404).json({ error: "Run not found" });
    return;
  }
  res.json(toWire(row));
});

router.get("/:id/stream", (req, res) => {
  const row = getRun(req.params.id);
  if (!row) {
    res.status(404).json({ error: "Run not found" });
    return;
  }

  res.writeHead(200, {
    "Content-Type": "text/event-stream",
    "Cache-Control": "no-cache",
    Connection: "keep-alive",
  });
  res.flushHeaders?.();

  function sendEvent(data: unknown) {
    res.write(`data: ${JSON.stringify(data)}\n\n`);
  }

  // Replay whatever's already in the log file so far, whether the run has
  // already finished or is still running -- a late/refreshed subscriber
  // never misses earlier output.
  let existingLog = "";
  try {
    existingLog = fs.readFileSync(row.log_path, "utf8");
  } catch {
    // Log file not written yet; nothing to replay.
  }
  if (existingLog.length > 0) {
    sendEvent({ chunk: existingLog });
  }

  if (row.status !== "running") {
    sendEvent({ done: true, status: row.status, exitCode: row.exit_code });
    res.end();
    return;
  }

  const emitter = getLiveEmitter(req.params.id);
  if (!emitter) {
    // The run says 'running' in the DB but its emitter already expired (the
    // grace period elapsed) -- re-read the row once more in case it finished
    // in the interim, otherwise there's nothing live left to subscribe to.
    const latest = getRun(req.params.id)!;
    sendEvent({
      done: true,
      status: latest.status,
      exitCode: latest.exit_code,
    });
    res.end();
    return;
  }

  function onData(text: string) {
    sendEvent({ chunk: text });
  }
  function onDone(payload: { status: string; exitCode: number | null }) {
    sendEvent({ done: true, status: payload.status, exitCode: payload.exitCode });
    cleanup();
    res.end();
  }
  function cleanup() {
    emitter?.off("data", onData);
    emitter?.off("done", onDone);
  }

  emitter.on("data", onData);
  emitter.on("done", onDone);

  req.on("close", cleanup);
});

export default router;
