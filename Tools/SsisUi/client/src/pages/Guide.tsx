import type { ReactNode } from "react";
import { Link } from "react-router-dom";

/** The in-app setup and usage guide, opened from the info icon in the header. Static
 * content: it describes the tool; it does not read any workspace state. */

function Section({ id, title, children }: { id: string; title: string; children: ReactNode }) {
  return (
    <section id={id} className="scroll-mt-20 rounded-lg border border-gray-200 bg-white p-5 shadow-sm">
      <h2 className="mb-3 text-base font-semibold text-gray-900">{title}</h2>
      <div className="space-y-3 text-sm leading-relaxed text-gray-700">{children}</div>
    </section>
  );
}

function Code({ children }: { children: ReactNode }) {
  return (
    <pre className="overflow-x-auto rounded bg-gray-900 px-3 py-2 font-mono text-xs text-gray-100">{children}</pre>
  );
}

function Table({ head, rows }: { head: string[]; rows: ReactNode[][] }) {
  return (
    <div className="overflow-x-auto">
      <table className="min-w-full border-collapse text-left text-sm">
        <thead>
          <tr className="border-b border-gray-200 text-xs uppercase tracking-wide text-gray-500">
            {head.map((h) => (
              <th key={h} className="px-3 py-2 font-semibold">
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((cells, i) => (
            <tr key={i} className="border-b border-gray-100 align-top">
              {cells.map((c, j) => (
                <td key={j} className="px-3 py-2">
                  {c}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

const CONTENTS: [string, string][] = [
  ["quick-start", "Quick start"],
  ["prerequisites", "Prerequisites"],
  ["install", "Installing and starting the UI"],
  ["exes", "Setting up the tool executables"],
  ["settings", "Settings page"],
  ["workspaces", "Creating and managing workspaces"],
  ["pipeline", "Running the pipeline"],
  ["gaps", "Gaps and fills"],
  ["results", "Where to read results"],
  ["troubleshooting", "Troubleshooting"],
];

export default function Guide() {
  return (
    <main className="mx-auto max-w-4xl space-y-5 p-6">
      <div>
        <h1 className="text-xl font-semibold">Setup and pipeline guide</h1>
        <p className="mt-1 text-sm text-gray-600">
          Everything needed to set up the tools, configure a workspace, and run the pipeline.
          <Link to="/" className="ml-1 text-blue-700 hover:underline">
            Back to workspaces
          </Link>
        </p>
      </div>

      <nav className="rounded-lg border border-gray-200 bg-white p-4 text-sm">
        <div className="mb-2 font-semibold text-gray-900">Contents</div>
        <ol className="grid list-decimal gap-1 pl-5 sm:grid-cols-2">
          {CONTENTS.map(([id, label]) => (
            <li key={id}>
              <a href={`#${id}`} className="text-blue-700 hover:underline">
                {label}
              </a>
            </li>
          ))}
        </ol>
      </nav>

      <Section id="quick-start" title="Quick start">
        <ol className="list-decimal space-y-1 pl-5">
          <li>Install the prerequisites below.</li>
          <li>Start the UI from <code>Tools\SsisUi</code> (see Installing and starting).</li>
          <li>
            Publish the two command-line tools once: run <code>Publish-Survey.ps1</code> (see
            Setting up the tool executables).
          </li>
          <li>Check the Settings page shows an exe path, not "dotnet run".</li>
          <li>Create a workspace pointing at your SSIS folder, then run the pipeline from its Dashboard.</li>
        </ol>
      </Section>

      <Section id="prerequisites" title="Prerequisites">
        <Table
          head={["Software", "Needed for", "Check with"]}
          rows={[
            ["Windows 10 or 11", "The folder picker and the PowerShell scripts", "--"],
            ["Node.js 22.5 or newer, with npm", "Running the UI", "node -v"],
            [".NET SDK (10.x)", "Building ssisx, svk, and the generated solutions", "dotnet --list-sdks"],
            ["Python 3 on PATH", "The validator (created on first use)", "python --version"],
            ["PowerShell 5.1 or 7", "Running Publish-Survey.ps1 and the pipeline", "powershell -v"],
          ]}
        />
        <p>
          You do not need SQL Server, SSIS, or Visual Studio to use this UI. The generated packages
          need SQL Server only when you run them.
        </p>
      </Section>

      <Section id="install" title="Installing and starting the UI">
        <p>From <code>Tools\SsisUi</code>, install once, then start:</p>
        <Code>{`npm install
npm run dev`}</Code>
        <p>
          This starts the API on <code>http://localhost:5174</code> and the front end on
          <code> http://localhost:5173</code> (Vite prints the exact port). Open the front-end address
          in your browser. Press Ctrl+C in the terminal to stop both.
        </p>
      </Section>

      <Section id="exes" title="Setting up the tool executables">
        <p>
          The UI runs three tools. Two are .NET command-line programs and one is a Python project.
        </p>
        <Table
          head={["Tool", "What it does", "How it gets set up"]}
          rows={[
            [
              "SSIS Extract/Rewrite (ssisx.exe)",
              "Reads packages, generates the C# rewrite",
              "Publish-Survey.ps1 builds it into Tools\\SsisExtractor\\scripts\\",
            ],
            [
              "SSIS Walkthrough (svk.exe)",
              "Human-readable walkthroughs and sample data",
              "Publish-Survey.ps1 builds it into the same folder",
            ],
            [
              "SSIS Rewrite Validation",
              "Validation gates and AI evidence packets",
              "Created automatically in its own .venv on first validation run (needs internet once)",
            ],
          ]}
        />
        <p className="font-semibold text-gray-900">Publish the .NET tools (once, and after changing their source):</p>
        <Code>{`cd D:\\PoC\\SSIS\\Tools\\SsisExtractor
.\\scripts\\Publish-Survey.ps1`}</Code>
        <p>
          This takes about a minute and writes <code>ssisx.exe</code> and <code>svk.exe</code> into
          <code> scripts\</code>. If PowerShell blocks the script, run it with
          <code> powershell -ExecutionPolicy Bypass -File .\scripts\Publish-Survey.ps1</code>. That
          applies to this one run only.
        </p>
        <p>
          Until the exes exist, the pipeline still runs, but through <code>dotnet run</code>. That works,
          just slower. The Settings page says which mode each tool is in.
        </p>
      </Section>

      <Section id="settings" title="Settings page">
        <p>
          Each tool has a row showing its status and a text box for an override path. Leave the box
          alone to use the automatic location. Use an override only when the exe lives somewhere else.
        </p>
        <ul className="list-disc space-y-1 pl-5">
          <li>
            <strong>Status line:</strong> "Using your override" (your path), "Using published exe" (the
            normal case), "dotnet run fallback" (publish the exe to fix), or the validator's venv.
          </li>
          <li>
            <strong>Save</strong> (floppy icon) stores the path. It must end in <code>.exe</code> and the
            file must exist.
          </li>
          <li>
            <strong>Clear</strong> (X icon) removes the override and returns to automatic detection.
          </li>
        </ul>
      </Section>

      <Section id="workspaces" title="Creating and managing workspaces">
        <p>
          A workspace is one SSIS input folder plus the output and fills folders for it. Click the
          <strong> plus</strong> icon on the Workspaces page to create one.
        </p>
        <Table
          head={["Field", "What to enter"]}
          rows={[
            ["Name", "Any label for this set of packages"],
            ["Input folder", "The folder holding your .dtsx, .dtproj or .ispac files (searched recursively)"],
            ["Output folder", "Where extracted specs, generated code, and reports are written"],
            ["Fills folder", "Where your hand-written and AI-written fills live. Keep it outside the output folder, so regenerating does not remove it"],
          ]}
        />
        <ul className="list-disc space-y-1 pl-5">
          <li><strong>Edit</strong> (pencil icon) changes a workspace's folders.</li>
          <li>
            <strong>Delete</strong> (bin icon) removes the workspace from this UI only. No files on disk
            are deleted.
          </li>
        </ul>
      </Section>

      <Section id="pipeline" title="Running the pipeline">
        <p>
          Open a workspace and use the Dashboard. Each card is one step. Run the steps top to bottom:
        </p>
        <Table
          head={["Step", "What it produces"]}
          rows={[
            ["Extract", "A spec.json per package: the control flow and every pipeline component"],
            ["Walkthrough", "A human-readable walkthrough of each package (svk)"],
            ["Sample Data", "Synthetic sample files for the packages' sources (svk)"],
            ["Generate", "The generated C# solution and the gap list"],
            ["Apply Fills", "Copies your fills into the generated solution"],
            ["Apply Tests", "Adds extra tests you wrote, and checks them against the build"],
            ["Build and Test", "Builds the generated solution and runs its unit tests"],
            ["Coverage", "Measures how much of the generated code the tests reach"],
            ["Validation", "Gate reports and AI review evidence (validator)"],
          ]}
        />
        <p className="font-semibold text-gray-900">Controls on each card:</p>
        <ul className="list-disc space-y-1 pl-5">
          <li><strong>Run</strong>: start the step the first time.</li>
          <li><strong>Rerun</strong>: start it again once it has run before.</li>
          <li>
            A disabled button means an earlier step is missing. Hover it to see which one.
          </li>
          <li><strong>Cancel</strong> stops a running step.</li>
        </ul>
        <p>
          Output streams live into the Dashboard. Each run is kept in the run history, and its full log
          is saved in <code>&lt;output folder&gt;\.ssisui-logs\</code>.
        </p>
      </Section>

      <Section id="gaps" title="Gaps and fills">
        <p>
          Generation never guesses. When something cannot be translated, it is listed as a gap, and the
          gap says what is needed. Gaps come in three kinds:
        </p>
        <ul className="list-disc space-y-1 pl-5">
          <li><strong>Tier 1 (a decision or a value):</strong> answer it in the Gaps tab's form.</li>
          <li><strong>Tier 2 (script logic):</strong> write the C# port of a Script Task or Script Component.</li>
          <li><strong>Tier 3 (tool limitation):</strong> nothing to fill. The tool itself needs a fix.</li>
        </ul>
        <p>
          Fills go in the fills folder, never in the generated output. After adding or changing a fill,
          run <strong>Apply Fills</strong> again.
        </p>
      </Section>

      <Section id="results" title="Where to read results">
        <ul className="list-disc space-y-1 pl-5">
          <li><strong>Reports:</strong> generation, load-failure, and portfolio summaries.</li>
          <li><strong>Gaps:</strong> every gap, grouped and filterable, with its work packet.</li>
          <li><strong>Fills:</strong> which fills applied, and which are stale or orphaned.</li>
          <li><strong>Applied Tests:</strong> results of the Apply Tests step (after it has run).</li>
          <li><strong>Coverage:</strong> per-package coverage, with the full report in a drawer.</li>
          <li><strong>Validation:</strong> gate verdicts and per-package validation status.</li>
        </ul>
      </Section>

      <Section id="troubleshooting" title="Troubleshooting">
        <Table
          head={["Symptom", "What to do"]}
          rows={[
            ["Page says it cannot reach the API", "The UI was not started. Run npm run dev from Tools\\SsisUi"],
            ["Port 5174 already in use", "Another process holds it. Close it, then start again"],
            ["Settings says dotnet run fallback", "Run Publish-Survey.ps1 (see Setting up the tool executables)"],
            ["Validation step waits a long time on its first run", "The validator's venv is installing packages. Wait a few minutes; it needs internet once"],
            ["A fill seems ignored", "Check it sits in the fills folder for its package, and that its provenance line matches the current gap. Stale fills are reported, not applied"],
            ["Start over with a clean database", "Stop the UI, delete Tools\\SsisUi\\server\\data\\ssisui.db. Workspaces and run history are lost; files on disk are kept"],
          ]}
        />
      </Section>
    </main>
  );
}