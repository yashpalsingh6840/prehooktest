# SsisUi

A local, single-developer review UI for the `ssisx`/`svk` toolchain. Dev-only — not part
of the client-facing deliverable that lives elsewhere under `Tools/`.

## Running

```
npm install
npm run dev
```

Run both from `Tools/SsisUi/`. This starts the Express backend on `:5174` and the Vite
dev server (prints its own port, typically `:5173`), which proxies `/api/*` requests to
the backend so the client can call `/api/...` with no CORS setup.

## Status

All planned functionality is built and verified:

- **Workspaces** — SQLite-backed (`node:sqlite`) CRUD with a server-side folder browser,
  picked/created from the Workspace Picker screen (`/`).
- **Pipeline runner** — each workspace detail page (`/workspaces/:id`) has a Dashboard
  tab that really spawns `ssisx extract/generate/apply-fills`, `svk walkthrough/
  sampledata`, `dotnet build`, and `Run-Coverage.ps1`/`Run-Pipeline.ps1`, streaming
  output live over SSE and recording run history.
- **Reports / Gaps / Fills tabs** — read-only viewers for `generate-report.md`,
  `load-failures.md`, `portfolio-digest.md`, `gaps.json` (grouped, filterable, with each
  gap's AI work packet rendered), and `fills-applied.json`.
- **Fill editor** — from the Gaps tab, a Tier-1 structured decision form
  (`<Package>.decisions.json`) or a Monaco code/text editor for Tier-2/TestOracle/
  LocalFileSourceData fills, each wired to trigger the correct follow-up run (Generate
  vs. Apply-Fills).
- **Walkthrough tab** — per-package rendered component-sequence view plus an editable
  "Rule Reviews" table backed by `<Package>.claims.json`.
- **Settings** (`/settings`) — override where `ssisx.exe`/`svk.exe` are resolved from.

Styled with Tailwind v4 (`@tailwindcss/vite`).
