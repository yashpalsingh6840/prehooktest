# SSIS Rewrite Studio

A local, single-developer review UI for the `ssisx` / `svk` toolchain and the SSIS
validation pipeline. It runs the tools for you, streams their output live, and shows the
results (reports, gaps, fills, coverage, validation) in one place.

SsisUi is a developer tool. It is not part of the client-facing deliverable under `Tools/`
(`SsisExtractor/`, `SsisValidationKit/`, `Etl.Core/`). It runs on the same Windows machine
as the packages it reviews.

---

## 1. Requirements

| Requirement | Why | Notes |
|---|---|---|
| Windows 10/11 | The folder picker and `Run-Pipeline.ps1` are Windows-only (PowerShell, `powershell.exe`). | Required. |
| Node.js 22.5 or newer, with npm | Runs the Express server and the Vite dev server. The server uses the built-in `node:sqlite` module, which needs 22.5+. | Tested with Node 24. Install from nodejs.org. Check with `node -v`. |
| .NET SDK able to build `Tools/SsisExtractor` | Builds `ssisx` and `svk` (the solution targets net10.0). Also runs `dotnet build`/`dotnet test` on generated packages. | Check with `dotnet --list-sdks`. Not needed if you publish the exes and point Settings at them. |
| Python 3 on `PATH` | Creates the validator's virtual environment on first use. | Only needed for the Validation pages. Check with `python --version`. |
| PowerShell 5.1 or 7 | Runs `Run-Pipeline.ps1` and `Run-Coverage.ps1`. | Ships with Windows. |

You do not need SQL Server, SSIS, Visual Studio, or the GAC to use SsisUi itself. The
generated packages need SQL Server only when you run them, not when you generate them.

---

## 2. Folder layout

SsisUi reads its tools from the rest of `Tools/`. Relative to `Tools/SsisUi/`:

| Path | What it is |
|---|---|
| `client/` | React + Vite + Tailwind front end (the screens you see). |
| `server/` | Express + SQLite back end (runs pipeline steps, serves reports). |
| `server/data/ssisui.db` | The SQLite database (workspaces, run history, settings). Created on first start. |
| `../SsisExtractor/` | `ssisx` source, and `scripts/Run-Pipeline.ps1`, `Run-Coverage.ps1`. |
| `../SsisValidationKit/` | `svk` source. |
| `../Etl.Core/` | Shared runtime library copied into every generated solution. |
| `../SSIS_RewriteValidationTool/` | The Python validator (`ssis_validator`). Its `.venv` is created inside this folder. |

Keep this layout intact. SsisUi finds the tools by these relative paths unless you override
them in Settings (see section 5).

---

## 3. Installation

Run these from `Tools/SsisUi/`:

```powershell
npm install
```

This installs the root tooling and both workspaces (`server` and `client`). The first run
takes a few minutes.

No database setup is needed. The server creates `server/data/` and `ssisui.db` on first
start.

---

## 4. Running

From `Tools/SsisUi/`:

```powershell
npm run dev
```

This starts two processes with `concurrently`:

| Process | Address | Notes |
|---|---|---|
| Express API (`server`) | `http://localhost:5174` | Fixed port. Health check: `http://localhost:5174/api/health`. |
| Vite dev server (`client`) | `http://localhost:5173` (typically) | Vite prints the actual port. It proxies `/api/*` to `:5174`. |

Open the Vite URL in a browser. You do not need to open the API port.

To stop, press `Ctrl+C` once in the terminal. Both processes stop.

### Production-style run

`npm run build -w client` writes a static build to `client/dist/`. The current server does
not serve that folder, so use `npm run dev` for day-to-day work.

---

## 5. Configuration

### 5.1 Ports

| Setting | Where | Default |
|---|---|---|
| API port | `server/src/index.ts` (`PORT`) | `5174` |
| Vite port | Vite picks it. Set `server.port` in `client/vite.config.mts` to pin it. | `5173` |
| API proxy target | `client/vite.config.mts` (`proxy["/api"]`) | `http://localhost:5174` |

If you change the API port, change the proxy target to match.

### 5.2 The tool executables (Settings page)

Open **Settings** (`/settings`). Each tool has a box and a status line.

| Tool | Setting key | What SsisUi uses, in order |
|---|---|---|
| `ssisx` | `ssisxExePath` | 1. Your override, if set and the file exists. 2. `Tools/SsisExtractor/scripts/ssisx.exe`, if it exists. 3. `dotnet run --project …Ssis.Extract.Cli.csproj -c Release --` (slower). |
| `svk` | `svkExePath` | 1. Your override, if set and the file exists. 2. `Tools/SsisExtractor/scripts/svk.exe`, if it exists. 3. `dotnet run` against `SsisValidationKit`. |
| Validator | `validatorExePath` | 1. Your override (used as given, see 5.5). 2. The venv Python at `SSIS_RewriteValidationTool/.venv/Scripts/python.exe`, running `-m ssis_validator.cli`. |

The status line under each box tells you which one is active:

- **"Using your override: …"**: your saved path.
- **"Using published exe at …"**: the exe next to the scripts folder.
- **"Using "dotnet run" fallback …"**: slower. Publish the exe to remove this (see 5.3).
- **"Using the validator's own Python venv …"**: the default for the validator.

The box shows the saved override if there is one, otherwise the path SsisUi expects. Pressing
**Save** on the auto-detected path does not save it as an override, so a later publish is
still picked up automatically. **Clear** removes an override.

Overrides are stored in the `settings` table of `ssisui.db`.

### 5.3 Publishing the exes (recommended)

The `dotnet run` fallback rebuilds the tool every time it runs. Publish once for speed:

```powershell
cd D:\PoC\SSIS\Tools\SsisExtractor
.\scripts\Publish-Survey.ps1   # see the script header for its options
```

If you publish somewhere else, paste the full path into the Settings box and press Save.

### 5.4 The validator environment

The first Validation run creates `SSIS_RewriteValidationTool\.venv` and runs
`pip install -e .[dev]` inside it. This takes a minute and needs internet access once. After
that, the venv is reused.

To reset it, delete `SSIS_RewriteValidationTool\.venv` and run a validation step again.

### 5.5 Validator override is not checked for existence

For `ssisx` and `svk`, an override that points at a missing file is ignored and the next
option is used. For the validator, an override is used as given. A wrong path makes the
validation step fail when it runs, not when you save. Check the status line after saving.

### 5.6 Other environment

- `SSISUI_INITIAL` is set internally by the folder picker to its starting folder. You do
  not set it yourself.
- No other environment variables are read.

---

## 6. Setting up a workspace

A **workspace** is one SSIS input folder plus its output folders.

1. Open `http://localhost:5173`. The **Workspace Picker** (`/`) opens.
2. Click **New workspace**. Browse to the folder that holds your `.dtsx` / `.dtproj` /
   `.ispac` files. The server-side folder browser shows the folder tree.
3. Set the output folder (where `extract`, `generate`, and the reports are written) and the
   fills folder (where your hand-written and AI-written fills live).
4. Save. You land on the workspace dashboard (`/workspaces/:id`).

Each workspace has its own run history and artifacts. Workspaces can be edited and deleted from
the picker. Deleting a workspace removes its record in SsisUi; it does not delete files on disk.

---

## 7. Using the workspace

Each workspace page has these tabs and screens:

| Screen | What it does |
|---|---|
| **Dashboard / Pipeline** | Run the steps: Extract, Walkthrough, Sample Data, Generate, Apply Fills, Build, Test, Coverage. Output streams live over SSE. Run history is listed. |
| **Reports** | Read-only viewer for `generate-report.md`, `load-failures.md`, `portfolio-digest.md`. |
| **Gaps** | `gaps.json`, grouped and filterable, with each gap's AI work packet. Opens the fill editor. |
| **Fills** | `fills-applied.json`: which fills applied, which are stale or orphaned. |
| **Fill editor** | Tier-1 decisions use a structured form (`<Package>.decisions.json`). Tier-2, test-oracle, and local-data fills use a Monaco editor. Saving triggers the right follow-up run (Generate or Apply Fills). |
| **Walkthrough** | Per-package component sequence, and the editable Rule Reviews table (`<Package>.claims.json`). |
| **Extracted Data** | Pretty tree or raw JSON of each `spec.json`. The Package Explorer drawer opens from here. |
| **Sample Data** | Browse the synthetic sample data `svk sampledata` wrote. |
| **Coverage** | Coverage numbers from `coverage-report.md`, per package and as averages, with a full-report drawer and an explanation panel. Polls while a coverage run is active. |
| **Applied Tests** | Results of the apply-tests step (`tests-applied.json`). Shown only after that step has run. |
| **Validation** | Gate reports and per-package validation status, from the Python validator. |
| **Settings** | Tool paths (section 5). |

The Dashboard runs the same commands as the command-line tools. A run you start here is
reproducible from a terminal with the same arguments. Run history records the command, exit
code, and log.

### The usual order

1. Extract (`ssisx extract`).
2. Walkthrough and Sample Data (`svk walkthrough`, `svk sampledata`), optional.
3. Generate (`ssisx generate`). Writes the C# project and the gap list.
4. Fill the gaps (Gaps tab), then Apply Fills.
5. Build and Test the generated solution.
6. Coverage.

Steps 4 and 5 can repeat. Run Generate again after changing a fill that has no effect until
you Apply Fills.

---

## 8. Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| Browser shows "Failed to fetch" or `/api` errors | The API is not running. Run `npm run dev` from `Tools/SsisUi/`, and check port 5174 is free. |
| `EADDRINUSE` on 5174 | Another process holds the port. Stop it, or change `PORT` in `server/src/index.ts` and the proxy target in `vite.config.mts`. |
| Status line says "dotnet run fallback" | Publish the exes (section 5.3), or paste their path in Settings. |
| Validator step fails right after you saved an override | The override path is wrong (section 5.5). Check the status line and the path. |
| Validator step hangs on first run | The venv is installing packages. Wait a few minutes. Check internet access. |
| Folder picker does nothing | It runs `powershell.exe`. Check that Windows PowerShell is installed and not blocked by policy. |
| Pipeline step fails with "No packages extracted yet" | Run Extract first. |
| A fill is ignored after Apply Fills | Check the file is under the fills folder for its package, and its `// ssisx-fill:` provenance line matches the current gap (see the Fills tab). A stale or orphaned fill is reported, not applied. |
| Database is corrupt or you want a clean slate | Stop the server, delete `server/data/ssisui.db*`, and start again. Workspaces and run history are lost. The packages and generated output on disk are not touched. |

Run history (command, exit code, log path) is stored per run. Open the run in the Dashboard to see its output.

---

## 9. Developer notes

- Server: `server/src/` with `routes/` (HTTP), `pipeline/` (spawning tools and building
  commands), `parsers/` (reading reports and JSON), and `db.ts` (SQLite schema). Build
  check: `npm run build -w server` (`tsc`).
- Client: `client/src/` with `pages/` (screens), `components/`, `api/client.ts` (typed API
  calls), `utils/`. Type check: `npx tsc --noEmit -p client/tsconfig.json`.
- Styling: Tailwind v4 through `@tailwindcss/vite`. Markdown: `react-markdown` with GFM.
  Diagrams: Mermaid. Code editing: Monaco.
- State: TanStack Query. Keys follow `["artifacts", workspaceId, ...]`, `["step-status",
  workspaceId]`, `["runs", workspaceId]`.
- The database is local and single-user. There is no authentication. Do not expose the
  ports beyond `localhost`.

## 10. Status

All planned functionality is built:

- **Workspaces**: SQLite-backed create, edit, delete. Server-side folder browser.
- **Pipeline runner**: spawns `ssisx`, `svk`, `dotnet build`/`test`, `Run-Pipeline.ps1`, and
  `Run-Coverage.ps1`, streaming output over SSE and recording run history.
- **Reports, Gaps, Fills**: read-only viewers for the generated reports and gap packets.
- **Fill editor**: structured Tier-1 form, Monaco for code and text fills, with the matching
  follow-up run.
- **Walkthrough**: per-package component sequence with editable rule reviews.
- **Coverage**: charts, full-report drawer, explanation panel. Polls during a run.
- **Applied Tests**: results page for the apply-tests step.
- **Validation**: gate reports and per-package status from the Python validator.
- **Settings**: tool path overrides with live resolution status.
