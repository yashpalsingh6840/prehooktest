# SSIS Rewrite Studio — Setup Guide

A short guide to setting up the tools on a new Windows machine, from nothing installed. Work through the sections in order.

| Part | Needed for |
|---|---|
| 1. .NET SDKs | `ssisx` (extractor), `svk` (walkthrough), the generated C# solutions |
| 2. Python | The validator (`SSIS_RewriteValidationTool`) |
| 3. Node.js | The web UI (`SsisUi`) |
| 4. Settings page | Telling the UI where the tool executables are |

Run all commands in PowerShell from the `Tools` folder unless stated otherwise.

---

## 1. .NET SDKs

The tools target **.NET 8** and **.NET 10**. Install both SDKs (not just the runtime):

1. Download the **.NET 8 SDK** and the **.NET 10 SDK** from https://dotnet.microsoft.com/download.
2. Check both are installed:

   ```powershell
   dotnet --list-sdks
   ```

   You should see a line starting with `8.` and a line starting with `10.`.

3. Build the solution (this also needs internet access once, to download NuGet packages):

   ```powershell
   dotnet build Tools.slnx -c Release
   ```

   Expected result: `Build succeeded` with `0 Error(s)`.

4. Create the self-contained `ssisx.exe` and `svk.exe` used by the UI and the survey:

   ```powershell
   .\SsisExtractor\scripts\Publish-Survey.ps1
   ```

   The exes are written into `SsisExtractor\scripts\`. They are not in git, so re-run this after any source change.

If step 3 or 4 fails with `NU…` or `NETSDK…` errors, check that both SDKs are listed in step 2 and that nuget.org is reachable.

---

## 2. Python (for the validator)

1. Install **Python 3.12 or 3.13** from https://www.python.org/downloads/. Python 3.11 or newer works, and the latest stable 3.14 should too. Avoid beta releases.
2. During install, tick **Add python.exe to PATH**.
3. Turn off the Windows Store alias: Windows Settings → *Apps* → *Advanced app settings* → *App execution aliases* → switch **python.exe** off.
4. Check it:

   ```powershell
   python --version
   ```

   It must print a version number, not open the Microsoft Store.

5. Set up the validator:

   ```powershell
   cd SSIS_RewriteValidationTool
   python -m venv .venv
   .venv\Scripts\python -m pip install -e ".[dev]"
   .venv\Scripts\python -m pytest tests/ -q
   ```

   Expected result: `13 passed, 47 skipped`. The skipped tests need sample files that are not shipped, so they skip by design.

**If step 5 fails** with `ConnectionResetError`, `Retrying ... /simple/` or `No matching distribution found for setuptools`, the machine cannot reach PyPI. Use this fallback:

a. **On a machine with internet access**, build the wheelhouse (any folder):

   ```powershell
   python -m pip download sqlglot pytest setuptools -d wheelhouse --only-binary=:all:
   ```

   Check that the `wheelhouse` folder holds these eight files: `colorama`, `iniconfig`, `packaging`, `pluggy`, `pygments`, `pytest`, `setuptools` and `sqlglot` (`.whl` files). A `NativeCommandError` line printed afterwards is only pip's upgrade notice.

b. **Copy the `wheelhouse` folder** to the target machine so that it sits next to `SSIS_RewriteValidationTool`, directly inside `Tools`, with no extra subfolder:

   ```
   Tools\
   ├── SSIS_RewriteValidationTool\
   └── wheelhouse\        <- the eight .whl files
   ```

c. **Install from the wheelhouse**, using the **full path** (a relative path such as `..\wheelhouse` does not work):

   ```powershell
   cd SSIS_RewriteValidationTool
   Remove-Item -Recurse -Force .venv -ErrorAction SilentlyContinue
   python -m venv .venv
   .venv\Scripts\python -m pip install --no-index --find-links "C:\full\path\to\Tools\wheelhouse" -e ".[dev]"
   .venv\Scripts\python -m pytest tests/ -q
   ```

   Replace `C:\full\path\to\Tools\wheelhouse` with the real location. The install should end with `Successfully installed ... ssis-validator-0.1.0`, and the tests should show `13 passed, 47 skipped`.

If the folder is not found, check it with `Test-Path "C:\full\path\to\Tools\wheelhouse"`. The full guide, with troubleshooting, is in [SSIS_RewriteValidationTool\README.md](SSIS_RewriteValidationTool/README.md#offline-install-no-access-to-pypi).

Do not copy a `.venv` folder from another machine. It only works on the machine that created it.

---

## 3. Node.js (for the web UI)

1. Install **Node.js 24 LTS** from https://nodejs.org. Node 22.5 or newer also works.
2. Check it:

   ```powershell
   node -v
   npm -v
   ```

3. Install the UI's packages (once, and after any update to `package.json`):

   ```powershell
   cd SsisUi
   npm install
   ```

4. Start the app:

   ```powershell
   npm run dev
   ```

   This starts two servers:
   - **API:** `http://localhost:5174` (fixed port). Check it with `http://localhost:5174/api/health`.
   - **Web UI:** `http://localhost:5173` (Vite prints the actual port if it differs).

   Open the web UI address in a browser. Keep the PowerShell window open while you work.

If you see `EADDRINUSE` on port 5174, another program is using it. Stop that program and run `npm run dev` again.

---

## 4. Settings page (in the UI)

Open **Settings** in the web UI. It shows one section per tool. Each section has:

- a line saying **how the tool is currently being found**,
- a text box with the path to use,
- a **Save** button (disk icon) and a **Clear** button (cross icon).

| Section | Tool | Override file |
|---|---|---|
| SSIS Extract/Rewrite EXE Path | `ssisx.exe` | `ssisx.exe` |
| SSIS Walkthrough EXE Path | `svk.exe` | `svk.exe` |
| SSIS Rewrite Validation EXE Path | the validator | `ssis_validator.exe` |

**How the UI finds each tool, in order:**

1. **Your override**, if you saved one. The path is used exactly as typed.
2. **The published exe** in `SsisExtractor\scripts\` (for `ssisx` and `svk`), created by `Publish-Survey.ps1`.
3. **A fallback:** `dotnet run` for `ssisx`/`svk` (works, but is slower on every run), or the validator's own `.venv` for the validator.

**What to do:**

- **Normally, do nothing.** After step 1 (`Publish-Survey.ps1`), the UI finds the exes automatically.
- **To use exes from another location**, paste the full path (for example `C:\Tools\ssisx.exe`) and press **Save**. The UI checks that the file exists.
- **To go back to automatic detection**, press **Clear**.

Pressing **Save** on the path the UI already detects does not save an override, so later updates to the published exe are still picked up.

---

## Quick check: everything works

1. `dotnet --list-sdks` shows 8.x and 10.x.
2. `python --version` prints a version number.
3. `npm run dev` runs and `http://localhost:5174/api/health` responds.
4. In **Settings**, the first two sections say *Using published exe…* and the validator section says *Using the validator's own Python venv…*.

If all four are true, the machine is ready.
