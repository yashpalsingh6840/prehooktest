# Gate 3 report — Package_Transforms

Per-component input/output schema parity. Declared column name + SSIS-type-to-.NET-type shape only — never actual data or execution. Every pipeline component is accounted for: a component with no populated externalMetadataColumns on either side (a pure pipeline-internal transform) is `Unverifiable`, not a silent pass.

| Verdict | Count |
|---|---|
| Mismatch | 4 |
| Unverifiable | 21 |
| Match | 5 |

## Mismatch (4)

### [DestinationInput] `OLEDST_CustomerEnriched` — **Mismatch**

```
vs. generated class 'CustomerEnriched' (Model\CustomerEnriched.cs):
  = EmailDomain: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
  = RowSource: SSIS 'wstr' -> 'string'.
  = TenureDays: SSIS 'i4' -> 'int'.
  x CustomerID_i4: declared on the left, no matching generated property.
  x SignupDate_dt: declared on the left, no matching generated property.
  i CustomerID: generated property has no matching left-side column. Not blocking — a destination may legitimately model an unmapped column.
  i SignupDate: generated property has no matching left-side column. Not blocking — a destination may legitimately model an unmapped column.
```

### [DestinationInput] `OLEDST_RegionSummary` — **Mismatch**

```
vs. generated class 'RegionSummary' (Model\RegionSummary.cs):
  x CustomerCount: SSIS 'ui8' expects one of ('UInt64',), generated type is 'int'.
  = Region: SSIS 'wstr' -> 'string'.
```

### [DestinationInput] `OLEDST_CustomerMerged` — **Mismatch**

```
vs. generated class 'CustomerMerged' (Model\CustomerMerged.cs):
  = Email: SSIS 'wstr' -> 'string'.
  x CustomerID_i4: declared on the left, no matching generated property.
  i CustomerID: generated property has no matching left-side column. Not blocking — a destination may legitimately model an unmapped column.
```

### [DestinationInput] `OLEDST_CustomerWithContact` — **Mismatch**

```
vs. generated class 'CustomerWithContact' (Model\CustomerWithContact.cs):
  = ContactPhone: SSIS 'wstr' -> 'string?'.
  = Email: SSIS 'wstr' -> 'string'.
  = PreferredChannel: SSIS 'wstr' -> 'string?'.
  x CustomerID_i4: declared on the left, no matching generated property.
  i CustomerID: generated property has no matching left-side column. Not blocking — a destination may legitimately model an unmapped column.
```

## Unverifiable (21)

### [InternalTransform] `CSPLIT_Validity` — **Unverifiable**

```
'CSPLIT_Validity' (Microsoft.ConditionalSplit) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `DCONV_Types` — **Unverifiable**

```
'DCONV_Types' (Microsoft.DataConvert) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `DER_Enrich` — **Unverifiable**

```
'DER_Enrich' (Microsoft.DerivedColumn) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `DER_TagInvalid` — **Unverifiable**

```
'DER_TagInvalid' (Microsoft.DerivedColumn) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `DER_TagValid` — **Unverifiable**

```
'DER_TagValid' (Microsoft.DerivedColumn) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `UNION_Recombine` — **Unverifiable**

```
'UNION_Recombine' (Microsoft.UnionAll) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `AGG_ByRegion` — **Unverifiable**

```
'AGG_ByRegion' (Microsoft.Aggregate) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `LKP_Country` — **Unverifiable**

```
'LKP_Country' (Microsoft.Lookup) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `MCAST_Matched` — **Unverifiable**

```
'MCAST_Matched' (Microsoft.Multicast) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `RC_MatchedRows` — **Unverifiable**

```
'RC_MatchedRows' (Microsoft.RowCount) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `RC_UnmatchedRows` — **Unverifiable**

```
'RC_UnmatchedRows' (Microsoft.RowCount) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `CSPLIT_ByCountry` — **Unverifiable**

```
'CSPLIT_ByCountry' (Microsoft.ConditionalSplit) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `DCONV_MergeId` — **Unverifiable**

```
'DCONV_MergeId' (Microsoft.DataConvert) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `MRG_SortedUnion` — **Unverifiable**

```
'MRG_SortedUnion' (Microsoft.Merge) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `SORT_Canada` — **Unverifiable**

```
'SORT_Canada' (Microsoft.Sort) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `SORT_RestOfWorld` — **Unverifiable**

```
'SORT_RestOfWorld' (Microsoft.Sort) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `DCONV_CustId_L` — **Unverifiable**

```
'DCONV_CustId_L' (Microsoft.DataConvert) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `DCONV_CustId_R` — **Unverifiable**

```
'DCONV_CustId_R' (Microsoft.DataConvert) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `MRG_CustomerContacts` — **Unverifiable**

```
'MRG_CustomerContacts' (Microsoft.MergeJoin) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `SORT_Contacts` — **Unverifiable**

```
'SORT_Contacts' (Microsoft.Sort) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `SORT_Customers` — **Unverifiable**

```
'SORT_Customers' (Microsoft.Sort) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

## Match (5)

### [SourceOutput] `FF_SRC_Customers` — **Match**

```
vs. generated class 'DFT_DerivedAndSplitCsvRow' (Csv\DFT_DerivedAndSplitCsvRow.cs):
  = Country: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = FirstName: SSIS 'wstr' -> 'string'.
  = LastName: SSIS 'wstr' -> 'string'.
  = Phone: SSIS 'wstr' -> 'string'.
  = SignupDate: SSIS 'wstr' -> 'string'.
```

### [SourceOutput] `FF_SRC_Customers2` — **Match**

```
vs. generated class 'AGG_ByRegionSourceCsvRow' (Csv\AGG_ByRegionSourceCsvRow.cs):
  = Country: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = FirstName: SSIS 'wstr' -> 'string'.
  = LastName: SSIS 'wstr' -> 'string'.
  = Phone: SSIS 'wstr' -> 'string'.
  = SignupDate: SSIS 'wstr' -> 'string'.
```

### [SourceOutput] `FF_SRC_Customers4` — **Match**

```
vs. generated class 'DFT_MergeSortedBranchesCsvRow' (Csv\DFT_MergeSortedBranchesCsvRow.cs):
  = Country: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = FirstName: SSIS 'wstr' -> 'string'.
  = LastName: SSIS 'wstr' -> 'string'.
  = Phone: SSIS 'wstr' -> 'string'.
  = SignupDate: SSIS 'wstr' -> 'string'.
```

### [SourceOutput] `FF_SRC_Contacts` — **Match**

```
vs. generated class 'MRG_CustomerContactsRightCsvRow' (Csv\MRG_CustomerContactsRightCsvRow.cs):
  = ContactPhone: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = PreferredChannel: SSIS 'wstr' -> 'string'.
```

### [SourceOutput] `FF_SRC_Customers3` — **Match**

```
vs. generated class 'MRG_CustomerContactsLeftCsvRow' (Csv\MRG_CustomerContactsLeftCsvRow.cs):
  = Country: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = FirstName: SSIS 'wstr' -> 'string'.
  = LastName: SSIS 'wstr' -> 'string'.
  = Phone: SSIS 'wstr' -> 'string'.
  = SignupDate: SSIS 'wstr' -> 'string'.
```
