# Independent review: `ssisx` / `Etl.Core` / `svk` — robustness, capability, and cost/effectiveness audit

You are reviewing this SSIS-to-C# migration toolkit (`Tools/SsisExtractor` = `ssisx`,
`Tools/Etl.Core` = the shared runtime, `Tools/SsisValidationKit` = `svk`) as a genuinely
independent, skeptical reviewer — not the team that built it. Nobody here is asking you to
confirm it works; they are asking you to find where it doesn't, where it's wasteful, and where it
could be meaningfully better, and to say so plainly even if that contradicts what the project's
own documentation claims.

**Read first, before anything else:**
1. `Docs/Tool-Handover-Overview.md` — the current, accurate description of what this toolkit does,
   its command surface, the Tier 1/2/3 gap taxonomy, the two test workflows (starter tests vs.
   AI-assisted TEST-ORACLE/LOCAL-DATA fills), streaming/parallelism/transaction design, and the
   chat-driven automation (`/ssisx-extract`, `/ssisx-generate`, `/ssisx-fill`, `/ssisx-test`,
   `/ssisx-rewrite`, `/ssisx-batch`).
2. `CLAUDE.md` at the repo root — the full chronological build log. Do **not** read it front to
   back; it is enormous. Use it as a reference to look up *why* a specific design decision was
   made once you've found something you want to question, and to check whether a limitation you
   noticed is already a known, named, deliberate trade-off (in which case say so and move on) or
   genuinely unaddressed.
3. `Tools/README.md`, `Tools/COPILOT_GUIDE.md`, `Tools/COPILOT_TESTING_GUIDE.md`,
   `Tools/.github/copilot-instructions.md`, and the prompt files under `Tools/.github/prompts/*.md`
   — these are what actually drives the AI-assisted steps day to day; read them as the artifact
   under review, not as background.

Do not trust a claim in any of the above — "0 warnings/0 errors," "byte-for-byte match," "verified
end to end" — without either re-deriving it yourself or explicitly flagging it as unverified in
your report. A large fraction of `CLAUDE.md`'s own history is examples of an *earlier* claim in
that same file turning out to be wrong once someone actually checked — treat every unverified
claim, including recent ones, as a hypothesis, not a fact.

## Ground rules

- All scratch/generation output goes under a fresh directory you create yourself (e.g.
  `D:\tmp\independent-review-<date>`) — never inside this repo's own working tree, and never
  inside any pre-existing sibling `out\`/`Output\` folder that isn't yours. Do not touch
  `fills-library\` or any already-committed generated output.
- Prefer real, evidenced test cases: this repo's own `SSIS/` packages, the fixtures under
  `Tools/SsisExtractor/tests/Ssis.Extract.Tests/Fixtures/`, and — if available on this machine —
  any real third-party SSIS portfolios already cloned locally (check for a sibling
  `SSIS_Packages_From_GitHub`-style folder before assuming none exist). Where you need to
  construct something synthetic to isolate one specific claim, say so and keep it minimal.
- **Build and run things.** A finding backed by "I built it, ran it, and observed X" is worth far
  more than one backed by "reading the code, I believe X" — but a reasoned-through code-path
  finding is still worth reporting if you're explicit that you didn't verify it by running
  anything. Label every finding with which kind of evidence it rests on.
- Do not fix anything you find, and do not send any speculative feature build past the design/
  recommendation stage — this is an audit, not a work session. If you think something is worth
  building, describe the design and give a rough size/risk estimate rather than implementing it.
- Where the answer to a question below is genuinely "this is fine, here's why," say so explicitly
  and state exactly what you checked to reach that conclusion. Don't pad the report with
  restated agreement — a section can legitimately be short.

## What to review

### 1. Robustness — extractor and generator

Both `ssisx extract` and `ssisx generate` are meant to run unattended against a real, messy,
unseen client portfolio via GitHub Copilot with no one watching closely. Stress-test that
assumption:

- Point `extract`/`generate` at genuinely malformed or unusual input: a `.dtsx` with invalid XML,
  a project referencing a missing `.conmgr`/connection manager, an empty folder, a folder with no
  `.dtsx` files at all, a package whose `.dtproj` is missing, a duplicate-`ObjectName` collision
  (this is already handled — confirm it still is), a package with a circular or self-referencing
  precedence constraint if you can construct one, a huge single package (many components) to see
  how output scales. For each: does it fail loudly and clearly, or does it produce a confusing
  error, a silent partial result, or a crash with a raw stack trace and no actionable message?
- Check exit-code discipline end to end: does every command's exit code actually match what
  `Docs/Tool-Handover-Overview.md` section 2 claims (0 clean / 3 gaps-but-written / 2 usage error /
  1 real failure)? Try to find a case where the exit code lies about what actually happened.
- Look for places a single bad package could abort a whole-portfolio `--recursive` run instead of
  being skipped/reported and letting the rest proceed. `Run-Pipeline.ps1`'s own history (see
  CLAUDE.md, "duplicate-package-`ObjectName` skip treated as fatal") is a real example of exactly
  this class of bug that was found and fixed once already — look for siblings of it.
- Assess `ssisx generate`'s behavior under partial/interrupted runs — if it's killed mid-write, is
  the output directory left in a state that's obviously broken (so a re-run or a human notices),
  or could it look deceptively complete?
- Is there anywhere the tool silently degrades a correctness-affecting decision (a coercion, a
  type mapping, a rounding rule) without it showing up as a gap or a warning? The project's own
  stated discipline is "gaps, not guesses" — look for a counterexample.

### 2. Extractor and walkthrough (`svk`) capabilities

- How complete is component/task-type coverage against a realistic modern SSIS portfolio? Section
  6 of the handover doc lists what's supported. Cross-check it against whatever real packages you
  can find (this repo's own `SSIS_From_Sandeep`-style fixtures if present, or any third-party
  portfolio on this machine) and note anything you find that ISN'T in that support table.
- Is `svk walkthrough`'s output actually useful for a human doing a pre-generation sanity check,
  or does it bury the signal (control-flow shape, real risk areas) in noise? Generate a
  walkthrough for a moderately complex package and read it as if you'd never seen the source
  `.dtsx` — could you reconstruct what the package does?
- Is `svk sampledata`'s synthetic data (CLAUDE.md's "schema-correct, not business-realistic, no
  edge-case bias") a real limitation worth closing, or genuinely fine given its stated purpose? Is
  there a cheap way to make it more useful (e.g. biasing toward boundary values for a Conditional
  Split condition) without turning it into a second AI-driven feature?
- Does `ssisx extract`'s own findings/conformance output actually help someone PRIORITIZE which
  packages in a large portfolio to tackle first, or does it just produce a flat, unranked list?

### 3. `apply-fills` metadata — token/cost efficiency and effectiveness (Tier 1/2 gap work packets)

This is the core AI-cost surface for gap-filling. Evaluate it concretely, not just by reading the
prompt-file claims:

- Pick 2-3 real Tier-1 and Tier-2 gaps (a Lookup missing join key, a Script Task/Component seam)
  and actually read the generated work packet under `<out>/gaps/<Package>/*.md`. Is everything
  genuinely needed to answer the gap present, with nothing missing that would force a human/AI to
  go open the original `.dtsx` or a generated `.cs` file anyway? Conversely, is there anything in
  the packet that's redundant, boilerplate repeated across every packet, or unlikely to ever be
  read?
- Measure roughly how large a typical packet is (token/character count) relative to how much of it
  is actually load-bearing information vs. fixed preamble/contract text. Is there real room to
  shrink the fixed portion without losing correctness (e.g. deduplicating across packets for the
  same component, as CLAUDE.md's Phase-3-review notes already partly address for Script
  Components)?
- `AiPacketEmitter`'s "pinned testing API block" and similar fixed constants get embedded verbatim
  into every relevant packet. Is this actually cheaper than referencing a shared doc, given how
  often a packet is opened vs. how large the embedded block is? Is the duplication between the
  embedded block and `Tools/COPILOT_TESTING_GUIDE.md` a real drift risk (they're kept in sync by
  hand, per CLAUDE.md's own admission)?
- Does `ssisx apply-fills`' own console/log output (the deep `apply-fills.log`, per CLAUDE.md's
  most recent round) actually help someone (or an AI) diagnose *why* a fill wasn't applied without
  re-reading the whole gap system from scratch? Deliberately write one fill with a wrong hash, one
  with a missing `partial` keyword, and one nested one directory too deep, and see whether the
  log/report correctly and clearly explains each.
- Would giving the AI step a way to batch-answer multiple gaps of the identical shape (e.g. every
  `LOCAL-DATA` gap across a whole package, or every `TEST-ORACLE` gap for one Script Component) in
  a single pass measurably reduce token cost vs. today's one-packet-at-a-time framing? Estimate,
  don't necessarily build.

### 4. Generated unit tests — can `ssisx generate` itself produce more/better tests?

- Enumerate what starter tests are generated today (per the taxonomy in
  `Docs/Generated-Tests-Plan.md` if present, and by actually reading a generated `.Tests` project
  for a moderately complex package). Where are the real coverage gaps — component types or code
  paths that get ZERO generated test today (not even a `TEST-ORACLE` advisory gap pointing at the
  hole)? CLAUDE.md's own post-Phase-4 review rounds found several instances of exactly this
  ("wired for shape X, not shape Y") — check whether any of those are still open, and hunt for new
  ones the same way (read `PackageGenerator.cs`'s per-flow dispatch and ask, for each flow shape,
  "does anything here call a test emitter for it, and is the call site actually reachable for every
  variant of this shape?").
- Are the generated starter tests testing anything meaningful, or mostly shape/wiring (a `.Name`
  property, a method exists) with weak assertions? Where a test's assertion is trivially always
  true regardless of a real bug (a common failure mode for auto-generated tests), name it.
- Is there a realistic, low-risk way to raise the STRENGTH of a generated assertion (e.g. property-
  based testing over the already-oracle-verified expression engine, or asserting against more than
  one representative row per test) without turning "starter test generation" into something that
  itself needs AI/judgment calls? Distinguish clearly between "the tool should generate this
  deterministically" and "this genuinely needs a human/AI, and is already correctly routed to
  TEST-ORACLE."
- Run `Tools/SsisExtractor/scripts/Run-Coverage.ps1` against a real generated portfolio and look at
  the actual line/branch coverage numbers (own-generated-code vs. shared-`Etl.Core`, per CLAUDE.md's
  own convention). Is the "always-green baseline" actually exercising a meaningful fraction of the
  generated logic, or mostly the trivial paths?

### 5. `apply-tests`/test-gap metadata — token/cost efficiency and effectiveness (TEST-ORACLE, LOCAL-DATA)

Same lens as section 3, applied specifically to the two test-gap kinds:

- Read a real `TEST-ORACLE` packet (Script Component seam, Script Task seam, and an unresolved
  Conditional Split condition if you can find or construct one) and a real `LOCAL-DATA` packet.
  Same questions as section 3: is everything needed present, is anything wasted, how large is the
  fixed-vs-load-bearing ratio?
- `LOCAL-DATA`'s packet names an exact expected file name and schema. Is there anything that could
  make writing a correct fill for this MORE mechanical/less AI-dependent than it is today — e.g.
  could `svk sampledata`'s already-deterministic synthesis be upgraded to close some fraction of
  `LOCAL-DATA` gaps automatically (with the AI only needed for the genuinely-judgment-requiring
  ones), rather than treating every file-based source's real-data test as equally AI-dependent?
- Is there duplication of context between a component's Tier-2 (`SCRIPT-COLUMN`/`SCRIPT-TASK`)
  packet and its companion `TEST-ORACLE` packet (CLAUDE.md documents these as deliberately
  separate work items at the same `Location`) that could be collapsed into one combined
  read/answer pass without weakening the "a test for it is a separate work item" principle?
- `ssisx apply-tests`' own enrichment path (voluntary `MoreTests`, no staleness tracking) — is the
  lack of staleness tracking there a real risk (a stale voluntary test silently passing against
  changed code) or genuinely fine given it's optional enrichment, not a gap?

### 6. Generated code — readability, simplicity, and how that affects testability

- Read the generated `Program.cs`/`{Package}.cs`/`Mapping/*.cs`/`Model/*.cs` for 2-3 real packages
  of increasing complexity (a plain single-flow package, one with a Conditional Split or Lookup,
  and one with real concurrency/multiple flow groups if you can find or generate one). Assess as a
  human maintainer would: is the method-per-component structure (per CLAUDE.md's "method-per-task
  emitter rewrite") actually easy to follow? Is naming clear? Is there unnecessary indirection,
  boilerplate, or repeated logic that could be factored without losing the "each component
  independently testable/verifiable" design goal that motivated the current structure?
- Specifically assess whether the CURRENT structure is actually easier or harder to write MORE
  tests against (by hand or by AI) than a plausible alternative would be — this ties directly into
  section 4. If you think a different structure would make it meaningfully easier to add tests
  later, describe it concretely rather than vaguely.
- Look at the identifier-sanitization behavior (CLAUDE.md, "identifier sanitization" section) — a
  raw SQL/pipeline column name with spaces/punctuation now gets sanitized into a C# identifier. Is
  the RESULT still readable/traceable back to the original column name (e.g. via
  `.HasColumnName(...)`/comments), or does sanitization make the generated code harder to map back
  to the source `.dtsx` once names collide or get suffixed (`_2`, `_3`, ...)?
- Is there dead/unreachable code paths left behind by incremental feature growth (CLAUDE.md
  documents at least one confirmed dead branch — `GenerateMergeJoinFlow`'s own downstream-transform
  gap check) that a reviewer should flag for removal, distinct from a correctness bug?

### 7. Logging during `generate`

- CLAUDE.md's most recent logging round added a shallow `generate.log`/console narration plus
  per-package timing, and a deep `apply-fills.log`. Actually run `ssisx generate` against a
  multi-package portfolio and read the resulting log. Is it genuinely useful for diagnosing WHY a
  specific package ended up with N gaps, or does someone still have to cross-reference
  `generate-report.md`/`gaps.json` by hand to get the real story? Could the log carry a one-line
  "why" pointer per package (e.g. "N gaps, dominant cause: X") without duplicating the full report?
- Is there a meaningful difference in what a human reading `generate.log` in a terminal needs vs.
  what an AI agent's own tool-call context needs (per the token-efficiency discussion in section
  13 of the handover doc) — and does the current shallow/deep split actually serve both well, or
  is one of them under-served?
- Compare `generate`'s logging depth against `apply-fills`' (deliberately deeper, per CLAUDE.md).
  Is that asymmetry justified, or does `generate` — the step that actually decides what gets built
  and what gaps exist — deserve more traceability than it currently has, especially for a run that
  silently produces an unexpectedly LOW gap count (which could mean "package was genuinely simple"
  or could mean "something didn't get analyzed that should have")?

### 8. Anything else

You're explicitly invited to raise findings outside the seven areas above — architecture risk,
maintainability of `Ssis.Extract.Codegen` itself (it's described in CLAUDE.md as the project's
"largest/most-tested file," ~2800+ lines in `PackageGenerator.cs`), test-suite health (the
2-pre-existing-unrelated-failures pattern that recurs throughout CLAUDE.md — is it still just 2,
or has it grown?), documentation drift between what's implemented and what the docs/prompt files
claim, security considerations (credentials, secrets in generated `appsettings.json`, anything a
client would be uncomfortable with), or anything else a genuinely independent reviewer would
consider worth flagging that the team building this might be too close to see.

## Deliverable

Produce a single written **Independent Review Report**, with these sections in this order:

1. **Executive summary** — 5-10 bullets, most important finding first, written for someone who
   will not read the rest of the report.
2. **Findings by area** — one subsection per numbered section above (1-8), each finding stated as:
   what you checked, what you found, evidence tier (built-and-observed vs. reasoned-from-code),
   severity/impact, and — where applicable — a concrete, sized recommendation (not an
   implementation).
3. **Cost/effectiveness assessment** — a direct answer, informed by sections 3 and 5 specifically,
   to: "is the current AI-cost design (packets, prompt files, log quietening) actually achieving
   what `Docs/Tool-Handover-Overview.md` section 13 claims, and where's the biggest remaining
   token-cost lever nobody has pulled yet?"
4. **Top 5 recommendations** — ranked by (impact × how cheaply it could be done), independent of
   which of the 8 areas each came from.
5. **What's genuinely solid** — don't let this report read as pure criticism; name what you tried
   to break and couldn't, plainly.

Write the report as a standalone artifact a project lead could hand to someone who has never seen
this codebase. Cite exact file paths, commands you ran, and commit/line references throughout.
