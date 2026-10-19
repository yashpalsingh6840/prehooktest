/** The explanation shown when the info icon on the Coverage heading is opened:
 * what coverage means, what is included, and how the Coverage step produces it. */
export default function CoverageInfoContent() {
  return (
    <div className="space-y-5 text-sm leading-relaxed text-gray-700">
      <section>
        <h3 className="mb-1 font-semibold text-gray-900">What coverage means</h3>
        <p>
          Coverage is the share of the generated C# code that the tests actually ran.{" "}
          <strong>Line %</strong> counts statements the tests executed.{" "}
          <strong>Branch %</strong> counts decision paths (each side of an <code>if</code>, a
          <code> ?:</code>, a <code>switch</code>) the tests took. A line can be covered while one of
          its branches is not, so branch % is usually the lower number.
        </p>
      </section>

      <section>
        <h3 className="mb-1 font-semibold text-gray-900">Own code vs shared Etl.Core</h3>
        <p>
          Every generated package references <strong>Etl.Core</strong>, a shared runtime library (SQL
          bulk copy, Excel and fixed-width readers, the step runner). The report splits each package's
          numbers:
        </p>
        <ul className="mt-1 list-inside list-disc space-y-1">
          <li>
            <strong>Own generated code</strong> -- only that package's own classes. This is the honest
            figure for judging a starter-test baseline.
          </li>
          <li>
            <strong>Shared Etl.Core</strong> -- the library's own coverage. Much of it is integration-only
            code (real database writes, real file readers) that unit tests are not meant to exercise, so
            it reads low.
          </li>
          <li>
            <strong>Line % / Branch %</strong> -- blended across both, so they are dragged down by Etl.Core.
            Read them as the overall picture, not as the package's own quality.
          </li>
        </ul>
      </section>

      <section>
        <h3 className="mb-1 font-semibold text-gray-900">How the Coverage step produces the numbers</h3>
        <ol className="list-inside list-decimal space-y-1">
          <li>
            The Coverage step (Pipeline page, or the end of Apply Tests) runs{" "}
            <code>Run-Coverage.ps1</code> against the generated output.
          </li>
          <li>
            For each package with a <code>{"<Package>.Tests"}</code> project, it runs{" "}
            <code>dotnet test --filter Category!=Integration --collect:"XPlat Code Coverage"</code>. The
            filter means <strong>unit tests only</strong>: tests tagged <code>Category=Integration</code>{" "}
            need a real database or real files, so they are excluded.
          </li>
          <li>
            The coverage collector (coverlet) writes <code>coverage.cobertura.xml</code> under that
            project's <code>TestResults</code> folder. The script reads only the file this run produced,
            never an older one.
          </li>
          <li>
            It reads the overall line and branch rates, then adds up covered and total lines per class.
            Classes whose source file is under <code>Etl.Core</code> go to the shared column; all others
            go to the package's own column.
          </li>
          <li>
            It writes <code>coverage-report.md</code> next to the generated solution. This page reads that
            file.
          </li>
        </ol>
      </section>

      <section>
        <h3 className="mb-1 font-semibold text-gray-900">What is included</h3>
        <ul className="list-inside list-disc space-y-1">
          <li>The starter tests ssisx generated for each package.</li>
          <li>Extra tests added with Apply Tests (MoreTests), since they are ordinary unit tests.</li>
          <li>
            A package whose tests fail still gets coverage recorded. Its <strong>Test result</strong> column
            shows FAIL, so its numbers reflect the code the failing run reached.
          </li>
          <li>
            A package with no <code>.Tests</code> project (for example, one blocked on gaps) does not appear
            at all.
          </li>
        </ul>
      </section>

      <section>
        <h3 className="mb-1 font-semibold text-gray-900">What is not included</h3>
        <ul className="list-inside list-disc space-y-1">
          <li>
            Integration tests (<code>Category=Integration</code>). They exercise real SQL and real files, so
            their coverage is not in these numbers.
          </li>
          <li>Code that is not generated, such as the production packages' original SSIS source.</li>
        </ul>
      </section>

      <section>
        <h3 className="mb-1 font-semibold text-gray-900">When the numbers change</h3>
        <p>
          The report shows the last coverage run, not live data. It refreshes after a Coverage step, and
          after <strong>Done, refresh coverage</strong> in the Apply Tests popup. While a coverage run is in
          progress, this page checks for updates every few seconds.
        </p>
      </section>
    </div>
  );
}
