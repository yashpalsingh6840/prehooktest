# Gate 2 report — Package_Transforms

Control-flow / data-flow graph parity. Every node/edge/concurrency-group item from both sides is listed below with a verdict and evidence — never a bare pass/fail. Data-flow-internal component checks are scoped to `Unverifiable` presence-only evidence; full per-component structural parity is not yet implemented (see SSIS-Rewrite-Validation-Plan.md and docs/dtsx-graph-notes.md §6).

| Verdict | Count |
|---|---|
| Unverifiable | 30 |
| Match | 8 |

## Unverifiable (30)

### [DataFlow:DFT_DerivedAndSplit] `CSPLIT_Validity` — **Unverifiable**

Component name found by text presence inside DFT_DerivedAndSplit's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_DerivedAndSplit] `DCONV_Types` — **Unverifiable**

Component name NOT found by text presence inside DFT_DerivedAndSplit's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_DerivedAndSplit] `DER_Enrich` — **Unverifiable**

Component name NOT found by text presence inside DFT_DerivedAndSplit's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_DerivedAndSplit] `DER_TagInvalid` — **Unverifiable**

Component name NOT found by text presence inside DFT_DerivedAndSplit's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_DerivedAndSplit] `DER_TagValid` — **Unverifiable**

Component name NOT found by text presence inside DFT_DerivedAndSplit's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_DerivedAndSplit] `FF_SRC_Customers` — **Unverifiable**

Component name found by text presence inside DFT_DerivedAndSplit's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_DerivedAndSplit] `OLEDST_CustomerEnriched` — **Unverifiable**

Component name NOT found by text presence inside DFT_DerivedAndSplit's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_DerivedAndSplit] `UNION_Recombine` — **Unverifiable**

Component name NOT found by text presence inside DFT_DerivedAndSplit's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_LookupAndAggregate] `AGG_ByRegion` — **Unverifiable**

Component name found by text presence inside DFT_LookupAndAggregate's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_LookupAndAggregate] `FF_SRC_Customers2` — **Unverifiable**

Component name NOT found by text presence inside DFT_LookupAndAggregate's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_LookupAndAggregate] `LKP_Country` — **Unverifiable**

Component name NOT found by text presence inside DFT_LookupAndAggregate's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_LookupAndAggregate] `MCAST_Matched` — **Unverifiable**

Component name NOT found by text presence inside DFT_LookupAndAggregate's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_LookupAndAggregate] `OLEDST_RegionSummary` — **Unverifiable**

Component name found by text presence inside DFT_LookupAndAggregate's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_LookupAndAggregate] `RC_MatchedRows` — **Unverifiable**

Component name NOT found by text presence inside DFT_LookupAndAggregate's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_LookupAndAggregate] `RC_UnmatchedRows` — **Unverifiable**

Component name NOT found by text presence inside DFT_LookupAndAggregate's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_MergeSortedBranches] `CSPLIT_ByCountry` — **Unverifiable**

Component name found by text presence inside DFT_MergeSortedBranches's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_MergeSortedBranches] `DCONV_MergeId` — **Unverifiable**

Component name NOT found by text presence inside DFT_MergeSortedBranches's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_MergeSortedBranches] `FF_SRC_Customers4` — **Unverifiable**

Component name found by text presence inside DFT_MergeSortedBranches's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_MergeSortedBranches] `MRG_SortedUnion` — **Unverifiable**

Component name NOT found by text presence inside DFT_MergeSortedBranches's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_MergeSortedBranches] `OLEDST_CustomerMerged` — **Unverifiable**

Component name NOT found by text presence inside DFT_MergeSortedBranches's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_MergeSortedBranches] `SORT_Canada` — **Unverifiable**

Component name NOT found by text presence inside DFT_MergeSortedBranches's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_MergeSortedBranches] `SORT_RestOfWorld` — **Unverifiable**

Component name NOT found by text presence inside DFT_MergeSortedBranches's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_SortAndMergeJoin] `DCONV_CustId_L` — **Unverifiable**

Component name NOT found by text presence inside DFT_SortAndMergeJoin's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_SortAndMergeJoin] `DCONV_CustId_R` — **Unverifiable**

Component name NOT found by text presence inside DFT_SortAndMergeJoin's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_SortAndMergeJoin] `FF_SRC_Contacts` — **Unverifiable**

Component name NOT found by text presence inside DFT_SortAndMergeJoin's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_SortAndMergeJoin] `FF_SRC_Customers3` — **Unverifiable**

Component name NOT found by text presence inside DFT_SortAndMergeJoin's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_SortAndMergeJoin] `MRG_CustomerContacts` — **Unverifiable**

Component name found by text presence inside DFT_SortAndMergeJoin's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_SortAndMergeJoin] `OLEDST_CustomerWithContact` — **Unverifiable**

Component name found by text presence inside DFT_SortAndMergeJoin's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_SortAndMergeJoin] `SORT_Contacts` — **Unverifiable**

Component name NOT found by text presence inside DFT_SortAndMergeJoin's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_SortAndMergeJoin] `SORT_Customers` — **Unverifiable**

Component name NOT found by text presence inside DFT_SortAndMergeJoin's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

## Match (8)

### [ControlFlow] `SQL_TruncateEnriched` — **Match**

Sequential call found in expected position in RunAsync.

### [ControlFlow] `DFT_DerivedAndSplit` — **Match**

Sequential call found in expected position in RunAsync.

### [ControlFlow] `SQL_TruncateRegionSummary` — **Match**

Sequential call found in expected position in RunAsync.

### [ControlFlow] `DFT_LookupAndAggregate` — **Match**

Sequential call found in expected position in RunAsync.

### [ControlFlow] `SQL_TruncateWithContact` — **Match**

Sequential call found in expected position in RunAsync.

### [ControlFlow] `DFT_SortAndMergeJoin` — **Match**

Sequential call found in expected position in RunAsync.

### [ControlFlow] `SQL_TruncateMerged` — **Match**

Sequential call found in expected position in RunAsync.

### [ControlFlow] `DFT_MergeSortedBranches` — **Match**

Sequential call found in expected position in RunAsync.
