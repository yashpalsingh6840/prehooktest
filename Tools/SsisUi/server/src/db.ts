import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { DatabaseSync } from "node:sqlite";

// NOTE: the plan called for `better-sqlite3`, but this machine has no
// prebuilt binary for this Node version (v24.20.0) and no Visual Studio C++
// toolset installed to build it from source (confirmed: `npm install`
// failed with "find VS ... could not find a version of Visual Studio 2017
// or newer"). Node 24 ships a built-in, synchronous SQLite module
// (`node:sqlite`, stable -- no --experimental flag needed, confirmed via a
// direct probe) with the same prepare/run/get/all shape, so that is used
// instead. Zero native dependencies, same synchronous API this file relies
// on throughout.

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const dataDir = path.join(__dirname, "..", "data");
fs.mkdirSync(dataDir, { recursive: true });

const dbPath = path.join(dataDir, "ssisui.db");
export const db = new DatabaseSync(dbPath);

db.exec("PRAGMA journal_mode = WAL;");

db.exec(`
  CREATE TABLE IF NOT EXISTS workspaces (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    input_path TEXT NOT NULL,
    output_path TEXT NOT NULL,
    fills_path TEXT NOT NULL,
    framework TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
  );
`);

db.exec(`
  CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
  );
`);

db.exec(`
  CREATE TABLE IF NOT EXISTS runs (
    id TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    step TEXT NOT NULL,
    package TEXT,
    status TEXT NOT NULL,
    exit_code INTEGER,
    started_at TEXT NOT NULL,
    finished_at TEXT,
    log_path TEXT NOT NULL
  );
`);
