# Final validation summary — Package_Transforms

## Tool-Verified (35)

- [Gate1] **OLEDST_CustomerEnriched** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate1] **LKP_Country** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate1] **OLEDST_RegionSummary** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate1] **OLEDST_CustomerMerged** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate1] **OLEDST_CustomerWithContact** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate1] **SQL_TruncateEnriched** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate1] **SQL_TruncateMerged** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate1] **SQL_TruncateRegionSummary** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate1] **SQL_TruncateWithContact** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `ControlFlow` **SQL_TruncateEnriched** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `ControlFlow` **DFT_DerivedAndSplit** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `ControlFlow` **SQL_TruncateRegionSummary** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `ControlFlow` **DFT_LookupAndAggregate** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `ControlFlow` **SQL_TruncateWithContact** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `ControlFlow` **DFT_SortAndMergeJoin** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `ControlFlow` **SQL_TruncateMerged** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `ControlFlow` **DFT_MergeSortedBranches** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_DerivedAndSplit` **FF_SRC_Customers** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_DerivedAndSplit` **OLEDST_CustomerEnriched** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_LookupAndAggregate` **FF_SRC_Customers2** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_LookupAndAggregate` **OLEDST_RegionSummary** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_MergeSortedBranches` **FF_SRC_Customers4** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_MergeSortedBranches` **OLEDST_CustomerMerged** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_SortAndMergeJoin` **FF_SRC_Contacts** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_SortAndMergeJoin` **FF_SRC_Customers3** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_SortAndMergeJoin` **OLEDST_CustomerWithContact** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `SourceOutput` **FF_SRC_Customers** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `DestinationInput` **OLEDST_CustomerEnriched** — Tier1: NormalizedMatch — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `SourceOutput` **FF_SRC_Customers2** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `DestinationInput` **OLEDST_RegionSummary** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `SourceOutput` **FF_SRC_Customers4** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `DestinationInput` **OLEDST_CustomerMerged** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `SourceOutput` **FF_SRC_Contacts** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `SourceOutput` **FF_SRC_Customers3** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `DestinationInput` **OLEDST_CustomerWithContact** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.

## Exception (0)

(none)

## AI-Verified (12)

- [Gate2] `DataFlow:DFT_DerivedAndSplit` **CSPLIT_Validity** — Tier1: Unverifiable — Tier2: AiConfirmedMatch — Tier 1 Unverifiable, Tier 2 resolved it as AiConfirmedMatch.
- [Gate2] `DataFlow:DFT_DerivedAndSplit` **DER_TagInvalid** — Tier1: Unverifiable — Tier2: AiConfirmedMatch — Tier 1 Unverifiable, Tier 2 resolved it as AiConfirmedMatch.
- [Gate2] `DataFlow:DFT_DerivedAndSplit` **DER_TagValid** — Tier1: Unverifiable — Tier2: AiConfirmedMatch — Tier 1 Unverifiable, Tier 2 resolved it as AiConfirmedMatch.
- [Gate2] `DataFlow:DFT_LookupAndAggregate` **AGG_ByRegion** — Tier1: Unverifiable — Tier2: AiConfirmedMatch — Tier 1 Unverifiable, Tier 2 resolved it as AiConfirmedMatch.
- [Gate2] `DataFlow:DFT_MergeSortedBranches` **CSPLIT_ByCountry** — Tier1: Unverifiable — Tier2: AiConfirmedMatch — Tier 1 Unverifiable, Tier 2 resolved it as AiConfirmedMatch.
- [Gate2] `DataFlow:DFT_SortAndMergeJoin` **MRG_CustomerContacts** — Tier1: Unverifiable — Tier2: AiConfirmedMatch — Tier 1 Unverifiable, Tier 2 resolved it as AiConfirmedMatch.
- [Gate3] `InternalTransform` **CSPLIT_Validity** — Tier1: Unverifiable — Tier2: AiConfirmedMatch — Tier 1 Unverifiable, Tier 2 resolved it as AiConfirmedMatch.
- [Gate3] `InternalTransform` **DER_TagInvalid** — Tier1: Unverifiable — Tier2: AiConfirmedMatch — Tier 1 Unverifiable, Tier 2 resolved it as AiConfirmedMatch.
- [Gate3] `InternalTransform` **DER_TagValid** — Tier1: Unverifiable — Tier2: AiConfirmedMatch — Tier 1 Unverifiable, Tier 2 resolved it as AiConfirmedMatch.
- [Gate3] `InternalTransform` **AGG_ByRegion** — Tier1: Unverifiable — Tier2: AiConfirmedMatch — Tier 1 Unverifiable, Tier 2 resolved it as AiConfirmedMatch.
- [Gate3] `InternalTransform` **CSPLIT_ByCountry** — Tier1: Unverifiable — Tier2: AiConfirmedMatch — Tier 1 Unverifiable, Tier 2 resolved it as AiConfirmedMatch.
- [Gate3] `InternalTransform` **MRG_CustomerContacts** — Tier1: Unverifiable — Tier2: AiConfirmedMatch — Tier 1 Unverifiable, Tier 2 resolved it as AiConfirmedMatch.

## Human-Needs-Review (30)

- [Gate2] `DataFlow:DFT_DerivedAndSplit` **DCONV_Types** — Tier1: Unverifiable — Tier 1 Unverifiable, no Tier 2 review has been run yet.
- [Gate2] `DataFlow:DFT_DerivedAndSplit` **DER_Enrich** — Tier1: Unverifiable — Tier2: AiSuspectedDiscrepancy — Tier 2 returned AiSuspectedDiscrepancy — needs human judgment, not a clean resolution.
- [Gate2] `DataFlow:DFT_DerivedAndSplit` **UNION_Recombine** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate2] `DataFlow:DFT_LookupAndAggregate` **LKP_Country** — Tier1: Unverifiable — Tier2: AiInconclusive — Tier 2 returned AiInconclusive — needs human judgment, not a clean resolution.
- [Gate2] `DataFlow:DFT_LookupAndAggregate` **MCAST_Matched** — Tier1: Unverifiable — Tier 1 Unverifiable, no Tier 2 review has been run yet.
- [Gate2] `DataFlow:DFT_LookupAndAggregate` **RC_MatchedRows** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate2] `DataFlow:DFT_LookupAndAggregate` **RC_UnmatchedRows** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate2] `DataFlow:DFT_MergeSortedBranches` **DCONV_MergeId** — Tier1: Unverifiable — Tier 1 Unverifiable, no Tier 2 review has been run yet.
- [Gate2] `DataFlow:DFT_MergeSortedBranches` **MRG_SortedUnion** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate2] `DataFlow:DFT_MergeSortedBranches` **SORT_Canada** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate2] `DataFlow:DFT_MergeSortedBranches` **SORT_RestOfWorld** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate2] `DataFlow:DFT_SortAndMergeJoin` **DCONV_CustId_L** — Tier1: Unverifiable — Tier 1 Unverifiable, no Tier 2 review has been run yet.
- [Gate2] `DataFlow:DFT_SortAndMergeJoin` **DCONV_CustId_R** — Tier1: Unverifiable — Tier 1 Unverifiable, no Tier 2 review has been run yet.
- [Gate2] `DataFlow:DFT_SortAndMergeJoin` **SORT_Contacts** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate2] `DataFlow:DFT_SortAndMergeJoin` **SORT_Customers** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate3] `InternalTransform` **DCONV_Types** — Tier1: Unverifiable — Tier 1 Unverifiable, no Tier 2 review has been run yet.
- [Gate3] `InternalTransform` **DER_Enrich** — Tier1: Unverifiable — Tier2: AiSuspectedDiscrepancy — Tier 2 returned AiSuspectedDiscrepancy — needs human judgment, not a clean resolution.
- [Gate3] `InternalTransform` **UNION_Recombine** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate3] `InternalTransform` **LKP_Country** — Tier1: Unverifiable — Tier2: AiInconclusive — Tier 2 returned AiInconclusive — needs human judgment, not a clean resolution.
- [Gate3] `InternalTransform` **MCAST_Matched** — Tier1: Unverifiable — Tier 1 Unverifiable, no Tier 2 review has been run yet.
- [Gate3] `InternalTransform` **RC_MatchedRows** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate3] `InternalTransform` **RC_UnmatchedRows** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate3] `InternalTransform` **DCONV_MergeId** — Tier1: Unverifiable — Tier 1 Unverifiable, no Tier 2 review has been run yet.
- [Gate3] `InternalTransform` **MRG_SortedUnion** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate3] `InternalTransform` **SORT_Canada** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate3] `InternalTransform` **SORT_RestOfWorld** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate3] `InternalTransform` **DCONV_CustId_L** — Tier1: Unverifiable — Tier 1 Unverifiable, no Tier 2 review has been run yet.
- [Gate3] `InternalTransform` **DCONV_CustId_R** — Tier1: Unverifiable — Tier 1 Unverifiable, no Tier 2 review has been run yet.
- [Gate3] `InternalTransform` **SORT_Contacts** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
- [Gate3] `InternalTransform` **SORT_Customers** — Tier1: LeftOnly — Tier2: AiConfirmedGap — Tier 2 returned AiConfirmedGap — needs human judgment, not a clean resolution.
