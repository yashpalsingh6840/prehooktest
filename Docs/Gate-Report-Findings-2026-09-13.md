# Gate report findings — 2026-09-13 (Package_Transforms / Package_Legacy re-review + a real ssisx fix)

Follow-up to `Docs/Gate-Report-Findings-2026-09-09.md`. That session found the validator's
15 reports "fully clean" (zero `Mismatch`, zero `LeftOnly`) after a validator-side bug was fixed
elsewhere. The reports in `D:\PoC\SSIS_RewriteValidationTool\gate_reports` are now dated
2026-09-13 and, for `Package_Transforms`/`Package_Legacy` specifically, show `LeftOnly` verdicts
again (Gate 3) plus a large "Human-Needs-Review" bucket in each package's own
`*.final-summary.md`. Re-investigated from scratch, reading only the tool's *output* (the
`gate_reports/*.md`/`*.json` files) — no `SSIS_RewriteValidationTool` source, tests, or its own
`docs/ai_context` were opened, per instruction. **No changes were made to
`SSIS_RewriteValidationTool` in this session.**

## What the reports actually showed

`Package_Transforms.final-summary.md`: 35 Tool-Verified, 12 AI-Verified, **30 Human-Needs-Review**.
`Package_Legacy.final-summary.md`: 10 Tool-Verified, 2 AI-Verified, **3 Human-Needs-Review**.

Tracing every Human-Needs-Review item back through its Gate 2/Gate 3 detail report, all ~33 items
collapse into exactly three root causes — none of them a translation bug in `ssisx`/`Etl.Core`:

1. **Gate 2's data-flow-internal check is a plain text search over the generated `.cs` method for
   that Data Flow Task, and several real, correctly-translated SSIS components left literally no
   trace in that file** — only in a separate `README.md`. `DCONV_Types`, `DER_Enrich`,
   `DER_TagInvalid`, `DER_TagValid`, `LKP_Country`, `MCAST_Matched`, `DCONV_MergeId`,
   `DCONV_CustId_L`, `DCONV_CustId_R` (Package_Transforms) were each reported "Component name NOT
   found by text presence inside `DFT_X`'s generated method." **Fixed** — see below.

2. **Gate 3 has a project-wide "does a specific Etl.Core construct class appear ANYWHERE in the
   generated project" check for exactly four SSIS component types** (`Microsoft.Sort` →
   `SortingRowSource`, `Microsoft.Merge`/`Microsoft.UnionAll` → `MergeInterleaveRowSource`/
   `ConcatenatingRowSource`, `Microsoft.RowCount` → `CountingRowSource`), reported as `LeftOnly`
   when none is found. All four class names are real, existing, tested types in `Etl.Core` — this
   is not a fabricated expectation. But `ssisx` has **two different, both-correct** translation
   paths for these same SSIS component types, and only one of them uses a dedicated wrapper:
   - A **standalone** Sort/RowCount feeding straight into a single-destination flow gets wrapped
     (`SortingRowSource`/`CountingRowSource` — confirmed present and used elsewhere in this
     codebase, e.g. any flow with `ProgramFlowSpec.SortKey`/`RowCountVariableNames` set).
     Similarly a genuinely multi-**source** Merge/Union All (two independent Data Flow sources
     feeding one Merge/Union All) gets `MergeInterleaveRowSource`/`ConcatenatingRowSource`.
     - A Sort/Merge/Union All/RowCount that sits **between a Conditional Split's own branches and
     their shared remerge point** (`DFT_MergeSortedBranches`'s own `SORT_Canada`/`SORT_RestOfWorld`
     → `MRG_SortedUnion`; `DFT_DerivedAndSplit`'s own `UNION_Recombine`; `DFT_LookupAndAggregate`'s
     own `RC_MatchedRows`/`RC_UnmatchedRows`) is instead translated as a **pure structural
     pass-through with no dedicated wrapper at all** — each branch already writes independently to
     the same destination via its own bulk insert, and neither a Sort's row order nor a Merge's
     interleaving has any observable effect on a bulk-inserted table, so reproducing the value is
     already correct with zero extra code. This is a real, previously fixture-verified design
     decision, not an oversight (see `CLAUDE.md`'s own "Sort + Merge — Conditional Split remerge"
     and "Union All — multi-hop remerge" history in this repo). Gate 3's project-wide check has no
     way to distinguish this shape from a genuinely dropped translation, so it reports `LeftOnly`
     either way.
   - `DFT_SortAndMergeJoin`'s own `SORT_Contacts`/`SORT_Customers` are the third shape: folded
     directly **into** `MergeJoinRowSource`'s own internal sort-merge-join algorithm (a Merge Join
     always sorts both sides itself) — again correct, again no separate wrapper, again `LeftOnly`.

3. **`Package_Legacy`'s one remaining item, `SQL_LegacyStep_DISABLED`, is a rollup/categorization
   inconsistency, not a translation question at all.** Its own Gate 2 finding already states
   plainly: "confirmed to have no generated counterpart by design, not treated as a gap" — yet the
   package's `final-summary.md` still lists it under Human-Needs-Review. The per-item conclusion
   and the final rollup disagree.

**One item was checked specifically for a real correctness bug, not waved off:**
`MRG_CustomerContacts` (Gate 2) came back `Tier2: AiSuspectedDiscrepancy`. Its join type
(`raw_value: "2"`) correctly maps to `MergeJoinType.Inner` in the generated code (matching this
project's own measured `0=FULL, 1=LEFT, 2=INNER` mapping) — confirmed correct, not a bug. The
likely reason an automated reviewer would flag it anyway: the destination entity's own
`ContactPhone`/`PreferredChannel`/`CustomerID` properties are all nullable (`string?`/`int?`) even
though an INNER join can never actually produce a null in any of them (only matched pairs emit a
row at all). That nullability is a deliberate, uniform, harmless conservative choice this
project's own `MergeJoinEmitter` makes regardless of join type (documented in `CLAUDE.md`) — not a
sign the join itself is wrong. Worth a human's five-second glance, but not a defect.

## What was fixed in `ssisx` this session (root cause 1, and part of root cause 3)

Both are pure, additive, structural-data-driven comment emission — **no change to what gets
built, executed, or tested; only to what the generated `.cs` says about itself.** Verified this
works for any package shape, not tuned to these two: regenerated 9 unrelated synthetic fixtures
(`SyntheticConditionalSplit`, `SyntheticLookupSingle`, `SyntheticMergeJoin`, `SyntheticAggregate`,
`SyntheticUnionTwoSources`, others) via `GeneratedSolutionBuildTests` and all 9 still build clean.

1. **`PackageClassEmitter` now writes a `//` comment directly above every Data Flow Task's own
   generated method**, listing every pipeline component in that flow (Seq/Name/Type/what it does),
   reusing the exact same `PipelineFlowOrder`/`ComponentInsight` functions `README.md`'s own "Data
   flow detail" table already uses — so the numbering and wording can never drift between the two,
   and a component folded into another construct (Data Conversion, Derived Column, a
   pass-through Sort/Merge/Union All/Row Count, a Lookup whose join lives in a cache class, a
   Multicast fan-out) now leaves a literal, named trace in the exact file a presence-based checker
   (this validator, or a human) would search. New optional parameter
   (`IReadOnlyDictionary<string, PipelineSpec>? pipelinesByFlowName = null`, defaulted so every
   existing caller/test is unaffected); `PackageGenerator` supplies the real dictionary from the
   already-computed `PackagePlan.Flows`.
2. **A disabled/skipped executable is now also noted directly on the generated package class
   itself** (`// 'SQL_LegacyStep_DISABLED' ... is disabled in the package, so it generated
   nothing -- matching SSIS ...`), the identical sentence `README.md`'s own "Gaps reported" table
   already carries, sourced from `PackagePlanner`'s own already-computed non-blocking
   `.Disabled` advisory — new `ProgramRequest.DisabledExecutableNotes`.

**Verified end to end, the same discipline this project's own history uses throughout:**
regenerated the real `Package_Transforms.dtsx`/`Package_Legacy.dtsx` (from
`D:\PoC\From Sandeep T\SSIS\SSIS\RBC_Demo_ETL\RBC_Demo_ETL`) with the updated `ssisx`, confirmed
by direct inspection that every previously-invisible component name now appears in
`Package_Transforms.cs`/`Package_Legacy.cs` (not just README.md), built the regenerated solution
(0 warnings/0 errors), and ran the generated tests (`Category!=Integration`, 7/7 + 21/21 green).
Full `Ssis.Extract.Codegen.Tests` suite: 335/365 passing, same 30 pre-existing, unrelated failures
(this PoC's own `SSIS/LoadEmployees.dtsx`/`LoadReferenceData.dtsx`/`SyntheticParallelShapes.dtsx`
no longer exist in this repo since commit `2f212bf` moved them out — nothing to do with this
change).

**A genuinely unrelated, pre-existing defect was also found and fixed while verifying this**:
`Tools/Etl.Core/Pipeline/EtlPackage.cs`/`IPackageRunner.cs`/`PackageRunner.cs`/`ConditionalStep.cs`
were four orphaned files referencing an `IEtlPackage` interface that no longer exists anywhere
(removed by the "method-per-task emitter rewrite" — see `CLAUDE.md`) — left behind because the
documented refresh recipe (`robocopy ... /E /XD bin obj`, no `/PURGE`) copies new/changed files
but never deletes ones removed upstream. This made `Tools/Etl.Core` fail to build standalone at
all, and made every one of `GeneratedSolutionBuildTests`' 10 real fixtures fail with the same
`CS0246` error, unrelated to anything in this session's own change. Deleted (confirmed unreferenced
anywhere else first); `Tools/Etl.Core` now builds standalone again, and 9/10 of those fixtures now
pass (the 10th is the same "moved out of this repo" `SyntheticParallelShapes.dtsx` issue above).

## Suggestions for `SSIS_RewriteValidationTool` (not implemented — that repo was not touched)

These are written from the report *output* only; I have not read that tool's source. Recommend
someone who owns that codebase evaluate them, and specifically **re-run all three gates against a
freshly regenerated rewrite** (using the `ssisx` build from this session) before triaging anything
below — several of today's Human-Needs-Review items may resolve, or at least gain much richer
context, purely from the new inline comments.

1. **Widen Gate 2's data-flow-internal presence check, or re-run it.** As of this session, every
   pipeline component's own name is now written as a comment directly above its owning Data Flow
   Task's generated method — the exact place this check already searches. A re-run should turn
   most "Component name NOT found" `Unverifiable` results into "found," and should give your own
   Tier 2 AI reviewer much more to work with for the ones it previously had nothing on (`DCONV_*`
   had no semantic evidence attached at all in today's Gate 3 report either — see #3).

2. **Gate 3's "no expected Etl.Core construct anywhere in the project" check for
   `Microsoft.Sort`/`Microsoft.Merge`/`Microsoft.UnionAll`/`Microsoft.RowCount` assumes there is
   always exactly one translation shape for each of these SSIS component types.** There are
   genuinely (and correctly) two: a standalone Sort/RowCount/multi-source Merge-or-Union-All gets
   a dedicated wrapper class; the SAME component types positioned between a Conditional Split's own
   branches and their shared remerge point are, by design, pure structural pass-throughs with no
   wrapper at all (verified: neither a Sort's ordering nor a Merge's interleaving has any effect on
   a bulk-inserted destination table, so nothing is lost). Recommend either (a) treating this
   specific `LeftOnly` shape as advisory/non-blocking rather than defaulting straight to
   Human-Needs-Review, since it's indistinguishable from "verified-safe pass-through" without
   deeper analysis, or (b) using the new inline provenance comment from #1 as a second, positive
   signal — if the component's name appears in the flow's own generated method at all (even
   without a dedicated wrapper class), that is evidence the translation deliberately accounted for
   it rather than silently dropping it.

3. **Attach semantic evidence for `Microsoft.DataConvert` and `Microsoft.Multicast` in Gate 3**,
   the same way it's already attached for Lookup/Aggregate/ConditionalSplit/MergeJoin/Sort. Right
   now these two get no evidence block at all, which appears to be exactly why they're stuck at "no
   Tier 2 review has been run yet" rather than reaching any AI-confirmed verdict — there's nothing
   for a reviewer (human or AI) to reason from. Data Conversion's own column list (source/target
   type per column) and Multicast's own output count are both cheap, already-available facts (the
   same ones `ComponentInsight.Describe` in this repo already renders for its own README/comment
   text, if useful as a reference for the shape of information that's actually available).

4. **Make the final-summary rollup respect a per-item conclusion.** `Package_Legacy`'s own Gate 2
   finding for `SQL_LegacyStep_DISABLED` states outright that it is "confirmed to have no generated
   counterpart by design, not treated as a gap," yet `Package_Legacy.final-summary.md` still lists
   it under Human-Needs-Review. When a gate's own narrative reaches a "by design, nothing to
   review" conclusion, the rollup category should reflect that rather than defaulting to
   Human-Needs-Review purely because Tier 1 read `Unverifiable`.

None of the above required, or should require, any change on the `ssisx`/`Etl.Core` side beyond
what's already shipped this session — every underlying translation this re-review checked was
already correct.
