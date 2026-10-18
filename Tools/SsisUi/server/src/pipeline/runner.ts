import { spawn, type ChildProcess } from "node:child_process";
import { EventEmitter } from "node:events";
import fs from "node:fs";
import path from "node:path";
import { randomUUID } from "node:crypto";
import { db } from "../db.js";
import type { Workspace } from "../routes/workspaces.js";
import { buildCommands, STEPS, type ResolvedCommand, type Step } from "./commands.js";
import { interpretExitCode, type RunStatus } from "./exitCodes.js";
import { TOOLS_ROOT } from "./toolResolver.js";

export type RunRow = {
  id: string;
  workspace_id: string;
  step: string;
  package: string | null;
  status: string;
  exit_code: number | null;
  started_at: string;
  finished_at: string | null;
  log_path: string;
};

export function isValidStep(step: string): step is Step {
  return (STEPS as readonly string[]).includes(step);
}

// One in-memory event emitter per currently-running (or recently-finished)
// run, so an SSE subscriber gets live 'data'/'done' events without polling
// the DB/log file. Entries are removed a short grace period after 'done' so
// a late-arriving subscriber can still catch the final event once.
const liveRuns = new Map<string, EventEmitter>();
const GRACE_PERIOD_MS = 10_000;

// The actual OS process for a run still in flight in *this* server process
// lifetime, so Cancel has something to kill. Cleared as soon as 'close'
// fires -- never kept around for a finished run.
const liveProcesses = new Map<string, ChildProcess>();

// Runs a Cancel request has already been issued for, so the eventual 'close'
// handler reports 'cancelled' rather than whatever exit code a killed
// process happens to produce (often nonzero/null, which would otherwise read
// as an indistinguishable 'failed').
const cancelling = new Set<string>();

export function getLiveEmitter(runId: string): EventEmitter | undefined {
  return liveRuns.get(runId);
}

// A run can outlive the server process that started it (restart, crash,
// laptop sleep) while its DB row is still 'running' -- with no live process
// or emitter left for it, every Run button on the dashboard stays disabled
// forever with no way to recover short of editing the database by hand. Any
// row still 'running' when a fresh server process starts up can only be
// orphaned like this (a brand-new process has no memory of a live child from
// a previous one), so it's reconciled to 'cancelled' immediately.
function reconcileOrphanedRuns(): void {
  const now = new Date().toISOString();
  db.prepare(
    `UPDATE runs SET status = 'cancelled', finished_at = ? WHERE status = 'running'`,
  ).run(now);
}
reconcileOrphanedRuns();

export function getRun(runId: string): RunRow | undefined {
  return db.prepare("SELECT * FROM runs WHERE id = ?").get(runId) as
    | RunRow
    | undefined;
}

export function listRuns(workspaceId: string): RunRow[] {
  return db
    .prepare(
      "SELECT * FROM runs WHERE workspace_id = ? ORDER BY started_at DESC",
    )
    .all(workspaceId) as RunRow[];
}

/** Ranks a run status so the worst of several commands wins. */
const SEVERITY: Record<RunStatus, number> = {
  success: 0,
  warning: 1,
  failed: 2,
  cancelled: 0,
};

type RunOutcome = { status: RunStatus; exitCode: number | null };

export function startRun(
  ws: Workspace,
  step: Step,
  pkg: string | undefined,
): RunRow {
  // Throws (e.g. a package's .dtsx cannot be found) before anything is
  // recorded, so the route can report it as a plain 400.
  const commands = buildCommands(ws, step, pkg);

  const logDir = path.join(ws.outputPath, ".ssisui-logs");
  fs.mkdirSync(logDir, { recursive: true });

  const id = randomUUID();
  const logPath = path.join(logDir, `${id}.log`);
  const startedAt = new Date().toISOString();

  db.prepare(
    `INSERT INTO runs (id, workspace_id, step, package, status, exit_code, started_at, finished_at, log_path)
     VALUES (?, ?, ?, ?, 'running', NULL, ?, NULL, ?)`,
  ).run(id, ws.id, step, pkg ?? null, startedAt, logPath);

  const emitter = new EventEmitter();
  liveRuns.set(id, emitter);

  const logStream = fs.createWriteStream(logPath, { flags: "a" });
  runCommandAt(id, commands, 0, { status: "success", exitCode: 0 }, logStream, emitter);

  return getRun(id)!;
}

/**
 * Runs commands[index], then the next, and so on. Each command's exit code is
 * interpreted by its own logical step; the run's status is the worst of them.
 * A bootstrap command failing stops the chain. A cancel stops it too (see
 * the close handler). Writes one run row at the end, via finishRun.
 */
function runCommandAt(
  id: string,
  commands: ResolvedCommand[],
  index: number,
  worst: RunOutcome,
  logStream: fs.WriteStream,
  emitter: EventEmitter,
): void {
  const cmd = commands[index];
  if (!cmd) {
    finishRun(id, worst, logStream, emitter);
    return;
  }

  const header = `$ ${cmd.command} ${cmd.args.map((a) => (a.includes(" ") ? `"${a}"` : a)).join(" ")}\n\n`;
  logStream.write(header);
  emitter.emit("data", header);

  const child = spawn(cmd.command, cmd.args, {
    cwd: cmd.cwd ?? path.join(TOOLS_ROOT, "SsisExtractor"),
    shell: false,
    windowsHide: true,
  });
  liveProcesses.set(id, child);

  function onChunk(chunk: Buffer) {
    const text = chunk.toString("utf8");
    logStream.write(text);
    emitter.emit("data", text);
  }

  child.stdout?.on("data", onChunk);
  child.stderr?.on("data", onChunk);

  child.on("error", (err) => {
    const text = `\n[ssisui] failed to launch process: ${err.message}\n`;
    logStream.write(text);
    emitter.emit("data", text);
  });

  child.on("close", (code) => {
    liveProcesses.delete(id);
    if (cancelling.delete(id)) {
      finishRun(id, { status: "cancelled", exitCode: code }, logStream, emitter);
      return;
    }

    const status: RunStatus =
      cmd.bootstrap && code !== 0 ? "failed" : interpretExitCode(cmd.step, code);
    const next: RunOutcome =
      SEVERITY[status] > SEVERITY[worst.status] ? { status, exitCode: code } : worst;

    if (cmd.bootstrap && code !== 0) {
      finishRun(id, next, logStream, emitter);
      return;
    }
    runCommandAt(id, commands, index + 1, next, logStream, emitter);
  });
}

function finishRun(
  id: string,
  outcome: RunOutcome,
  logStream: fs.WriteStream,
  emitter: EventEmitter,
): void {
  if (outcome.status === "cancelled") {
    logStream.write("\n[ssisui] run cancelled by user\n");
    emitter.emit("data", "\n[ssisui] run cancelled by user\n");
  }
  const finishedAt = new Date().toISOString();
  db.prepare(
    "UPDATE runs SET status = ?, exit_code = ?, finished_at = ? WHERE id = ?",
  ).run(outcome.status, outcome.exitCode, finishedAt, id);
  logStream.end();
  emitter.emit("done", { status: outcome.status, exitCode: outcome.exitCode });
  setTimeout(() => {
    liveRuns.delete(id);
  }, GRACE_PERIOD_MS);
}

/**
 * Stops a running step. If the OS process is still live in this server
 * process (the normal case), it's killed -- via `taskkill /t /f` on Windows,
 * since `dotnet build`/`dotnet test` spawn their own MSBuild/VSTest child
 * processes that a plain `child.kill()` would leave running. If the run is
 * 'running' in the DB but has no live process here (this server process
 * restarted since it was started -- reconcileOrphanedRuns only runs once, at
 * boot, so a row started and orphaned in between two boots would land here),
 * there's nothing left to kill; the DB row is force-marked 'cancelled'
 * directly so the dashboard unblocks without waiting for a restart.
 */
export function cancelRun(runId: string): { ok: boolean; message?: string } {
  const row = getRun(runId);
  if (!row) return { ok: false, message: "Run not found" };
  if (row.status !== "running") {
    return { ok: false, message: `Run is already ${row.status}` };
  }

  const child = liveProcesses.get(runId);
  if (child && child.pid) {
    cancelling.add(runId);
    if (process.platform === "win32") {
      spawn("taskkill", ["/pid", String(child.pid), "/t", "/f"], {
        windowsHide: true,
      });
    } else {
      child.kill("SIGTERM");
    }
    return { ok: true };
  }

  // Orphaned: no live handle to kill, just unblock the row.
  const now = new Date().toISOString();
  db.prepare(
    "UPDATE runs SET status = 'cancelled', finished_at = ? WHERE id = ?",
  ).run(now, runId);
  const emitter = liveRuns.get(runId);
  if (emitter) {
    emitter.emit("done", { status: "cancelled", exitCode: row.exit_code });
    liveRuns.delete(runId);
  }
  return { ok: true };
}
