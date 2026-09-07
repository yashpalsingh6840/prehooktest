# Independent review: pipeline-wiring gaps and "verified by building, not by running" gaps

You are reviewing `D:\PoC\SSIS\Tools\SsisExtractor` (and its sibling `Tools\Etl.Core`) with fresh
eyes, hunting for two SPECIFIC classes of bug, not doing a general code review. Both classes were
found for real, more than once, in the same session, only by actually RUNNING generated output
end to end -- never by `dotnet build` succeeding or a golden-file diff matching. Read
`D:\PoC\SSIS\CLAUDE.md`'s own "Not fixed yet"/most-recent sections for the full history if you
want context, but do not trust it, a green test suite, or any commit message as proof of anything
here -- build and run things yourself.

**The three real bugs that motivated this review**, so you know the shape to look for:

1. `Run-Coverage.ps1`'s own `-Package` parameter, forwarded from `Run-Pipeline.ps1` as a
   comma-joined string via `powershell -File`, silently matched ZERO test projects -- PowerShell's
   `-File` argument parser does not reliably split a comma-joined value into a multi-element
   array for a `[string[]]` parameter the way an interactive prompt would.
2. A newly-added "Data Flow Task" starter test called the generated `DFT_X(uow, ct)` method
   directly, which threw `NullReferenceException` on EVERY package, always -- that method reads a
   private `_load` field that only the package's own top-level `RunAsync()` populates; nothing
   caught this until the generated test was actually executed (a `dotnet build` of the same
   project succeeded cleanly).
3. `Run-Pipeline.ps1`'s own `generate` step never forwarded `-FillsPath` to `ssisx generate`
   itself (only to the later `apply-fills` step) -- so a Tier-1 `<Package>.decisions.json`
   acknowledgment sitting in the real fills directory was silently never read, for either package
   tested, across multiple pipeline runs, with nothing in the console output making this obvious.

All three share a root shape: **a value or an assumption that's correct in ONE code path (a
single manual CLI invocation, an existing test written against the real bootstrap) silently stops
being correct in an ADJACENT path (a wrapper script, a new test bypassing the bootstrap) that
looks equivalent but isn't.** That's what you're hunting for -- not general bugs, not style,
not missing features.

## Part A -- every script-to-script and script-to-CLI parameter handoff

Enumerate EVERY place one piece of this tool's own tooling invokes another, and for each, check
whether every flag that conceptually should flow through actually does, by reading the exact
argument list built at the call site (not by trusting a comment or doc claiming it does):

- `Tools/SsisExtractor/scripts/Run-Pipeline.ps1` -- its own calls to `ssisx extract`, `svk
  walkthrough`, `svk sampledata`, `ssisx generate`, `ssisx apply-fills`, and `Run-Coverage.ps1`.
  `-FillsPath`/`-EtlCorePath`/`-Framework`/`-Package`/`-Configuration` are the parameters already
  known to matter; check each one is actually threaded to every step that needs it, not just the
  step it was first added for.
- `Tools/SsisExtractor/scripts/Run-Coverage.ps1` -- confirm the `-Package` fix (the defensive
  `-split ','`) actually works for every shape PowerShell might hand it (a single name, a
  comma-joined string as one array element, a genuine multi-element array from an interactive
  call) -- construct at least 2 of these three shapes for real and confirm the match still works,
  don't just read the code and assume.
- Every `.github/prompts/ssisx-*.prompt.md` file (`ssisx-extract`, `ssisx-walkthrough`,
  `ssisx-generate`, `ssisx-fill`, `ssisx-test`, `ssisx-more-tests`, `ssisx-rewrite`) -- these are
  followed by an AGENT, not a script, so "wiring" here means: does one step's own instructions
  correctly tell the agent to reuse the exact same `--out`/`--fills`/`--framework` value the
  previous step used, or could an agent following them literally end up passing a DIFFERENT value
  to two steps that must agree? `ssisx-rewrite.prompt.md` sequencing the other five is the highest
  value target -- trace its own Step 0 resume-detection logic too, the same way `apply-fills`'s
  own decisions-path lookup was silently wrong.
- `ssisx apply-fills`/`ssisx apply-tests`'s own `--fills` default (`<out>/fills`) vs. what
  `ssisx generate` itself defaults to and reports in `HOW-TO-FILL-GAPS.md` -- confirm these two
  independently-computed defaults can never disagree for the same `--out`, by reading both
  computations side by side, not by assuming they must match because they look similar.
- Any other CLI flag in `Ssis.Extract.Cli` that has a DEFAULT value used when omitted (grep for
  `??=`, `?? Path.Combine`, or similar fallback assignments across `GenerateCommand.cs`,
  `ApplyFillsCommand.cs`, `ApplyTestsCommand.cs`, `ConformanceCommand.cs`) -- for each, ask: is
  there a DIFFERENT command elsewhere in this tool with a similarly-named flag whose default
  points somewhere else, such that a caller reasonably assuming "the default is always the same
  place" would be wrong? Name every one you find, whether or not it's currently causing a bug.

## Part B -- every generated starter test that calls back INTO the package class, hunting for a hidden bootstrap-order assumption

The root cause of bug 2 above is narrow but not unique: `ComponentTestEmitter.cs`'s NEWEST
emitter (`EmitDataFlowStepTest`/`EmitConditionalSplitDataFlowTest`) had to be rewritten to avoid
calling `package.DFT_X(uow, ct)` directly, because doing so silently depended on the package's own
`RunAsync()` having already run. Read EVERY starter-test emitter in
`Tools/SsisExtractor/src/Ssis.Extract.Codegen/` (`ComponentTestEmitter.cs`,
`SqlStatementTestEmitter.cs`, `TransformTestEmitter.cs`, `RouterTestEmitter.cs`,
`RunAsyncFailureTestEmitter.cs`) and for each generated `[Fact]`, trace exactly what it calls:

- Does it call a plain, already-public "leaf" method (`package.{Source}()`, `package.
  {Entity}Destination()`, a bare `new {TransformClassName}()`) -- these are safe, since they don't
  depend on the package's own private mutable state at all?
- Or does it call a method that is itself defined as part of the package's own step-dispatch
  surface (anything named like a control-flow task -- `SQL_X`, `DFT_X`, `SEQ_X`, `FST_X`,
  `FEL_X`) -- these are exactly the ones that read `_load`/`lKP_CountryCache`/any other
  package-level field only populated by `RunAsync()`'s own bootstrap, or by a container method
  further up the tree? For EVERY such call site you find, confirm (by actually generating,
  building, AND RUNNING a real fixture that exercises it -- not by reading the code and assuming
  it's fine) that whatever field it reads is either genuinely safe to be null/default in this
  context, or is independently seeded by the test itself before the call.
- `PackageClassEmitter.cs`'s own `Log<T>`/`Opt<T>` were widened from `private` to `internal`
  specifically to let a test reconstruct a Conditional Split's own per-branch sink from outside
  the class. Check whether any OTHER starter test needs similar access to package-private state
  that it currently can't reach, and whether that gap is currently silently un-tested (no test at
  all) or, worse, silently WRONG (a test that compiles and passes but doesn't actually exercise
  what its own name/summary claims -- e.g. by disproportionately asserting something trivially
  true regardless of whether the real logic ran).

For this whole section, the standard of evidence matters: **generate a real package (or several
of the fixtures under `Tools/SsisExtractor/tests/Ssis.Extract.Tests/Fixtures/`), build the whole
generated solution, and actually run `dotnet test` (not `--filter Category!=Integration` only --
run the full set at least once) before concluding a call site is safe.** A finding backed only by
reading the code is still worth reporting, but say so explicitly and separately from a finding you
confirmed by running it -- this project has been burned specifically by trusting "it compiles"
and "the golden file matches" as if they were proof of runtime correctness.

## Part C -- anything else in the same shape

While you're in there, name (don't necessarily fix) any other place you notice a value being
silently assumed equal across two code paths that could diverge -- a hardcoded path, a duplicated
literal (e.g. a filename suffix, a default port, a magic string) that exists in two files with no
single source of truth, or a test double (`FakeUnitOfWork`, `PackageHarness`) whose own behavior
differs from the real thing in a way nothing currently asserts against. Don't go looking for
unrelated feature gaps or style issues -- stay inside "two things that should agree silently
don't, or could stop agreeing."

## Hard rules

- ALL scratch/generation output goes under `D:\tmp\<something-descriptive>` -- never
  `D:\PoC\Tools\out`, never left as stray files in the repo working tree, never inside
  `Tools\SsisExtractor` itself.
- Do not fix anything you find, and do not touch any file under `Tools\SsisExtractor\scripts\` or
  `src\`/`tests\` to "confirm" a fix works -- reproduce the BROKEN behavior only, then report it.
  A human decides what happens next.
- Distinguish, for every finding, whether you CONFIRMED it by actually generating+building+running
  something, or only REASONED about it by reading code -- these are different tiers of evidence
  and must be labeled as such, not blended together.
- Full regression before you start and again before you finish: `dotnet build-server shutdown`,
  then `Tools/SsisExtractor/scripts/Run-Tests.ps1` (or `dotnet test SsisExtractor.slnx --filter
  "FullyQualifiedName!~GeneratedSolutionBuildTests"` for the fast subset). Report the exact pass
  count both times and flag immediately if it's not the same.

## Reporting

One list of findings, ranked most-severe first (a silently-wrong result outranks a silently-dropped
flag outranks a cosmetic duplication). For each: exact file/line, exact repro command, expected vs.
actual, and which evidence tier (confirmed-by-running vs. reasoned-from-code) it rests on. If a
whole section comes back clean, say so explicitly and name exactly what you checked -- "looks fine"
alone is not an acceptable conclusion for any section. Keep the final report under 1200 words
excluding any raw command output you quote.
