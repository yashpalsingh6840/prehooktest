# Gate 3 report — Package_Transforms

Per-component input/output schema parity. Declared column name + SSIS-type-to-.NET-type shape only — never actual data or execution. Every pipeline component is accounted for: a component with no populated externalMetadataColumns on either side (a pure pipeline-internal transform) is `Unverifiable`, not a silent pass.

| Verdict | Count |
|---|---|
| LeftOnly | 8 |
| Unverifiable | 13 |
| NormalizedMatch | 1 |
| Match | 8 |

## LeftOnly (8)

### [InternalTransform] `UNION_Recombine` — **LeftOnly**

```
'UNION_Recombine' (Microsoft.UnionAll) has no generated counterpart: none of ('ConcatenatingRowSource',) — the Etl.Core construct(s) that would represent this SSIS component type if translated — are referenced anywhere in the generated project. A project-wide presence check, not a per-instance match: a real, tool-detected gap, not a naming ambiguity.
```

### [InternalTransform] `RC_MatchedRows` — **LeftOnly**

```
'RC_MatchedRows' (Microsoft.RowCount) has no generated counterpart: none of ('CountingRowSource',) — the Etl.Core construct(s) that would represent this SSIS component type if translated — are referenced anywhere in the generated project. A project-wide presence check, not a per-instance match: a real, tool-detected gap, not a naming ambiguity.
```

### [InternalTransform] `RC_UnmatchedRows` — **LeftOnly**

```
'RC_UnmatchedRows' (Microsoft.RowCount) has no generated counterpart: none of ('CountingRowSource',) — the Etl.Core construct(s) that would represent this SSIS component type if translated — are referenced anywhere in the generated project. A project-wide presence check, not a per-instance match: a real, tool-detected gap, not a naming ambiguity.
```

### [InternalTransform] `MRG_SortedUnion` — **LeftOnly**

```
'MRG_SortedUnion' (Microsoft.Merge) has no generated counterpart: none of ('MergeInterleaveRowSource',) — the Etl.Core construct(s) that would represent this SSIS component type if translated — are referenced anywhere in the generated project. A project-wide presence check, not a per-instance match: a real, tool-detected gap, not a naming ambiguity.
```

### [InternalTransform] `SORT_Canada` — **LeftOnly**

```
'SORT_Canada' (Microsoft.Sort) has no generated counterpart: none of ('SortingRowSource',) — the Etl.Core construct(s) that would represent this SSIS component type if translated — are referenced anywhere in the generated project. A project-wide presence check, not a per-instance match: a real, tool-detected gap, not a naming ambiguity.
```

### [InternalTransform] `SORT_RestOfWorld` — **LeftOnly**

```
'SORT_RestOfWorld' (Microsoft.Sort) has no generated counterpart: none of ('SortingRowSource',) — the Etl.Core construct(s) that would represent this SSIS component type if translated — are referenced anywhere in the generated project. A project-wide presence check, not a per-instance match: a real, tool-detected gap, not a naming ambiguity.
```

### [InternalTransform] `SORT_Contacts` — **LeftOnly**

```
'SORT_Contacts' (Microsoft.Sort) has no generated counterpart: none of ('SortingRowSource',) — the Etl.Core construct(s) that would represent this SSIS component type if translated — are referenced anywhere in the generated project. A project-wide presence check, not a per-instance match: a real, tool-detected gap, not a naming ambiguity.
```

### [InternalTransform] `SORT_Customers` — **LeftOnly**

```
'SORT_Customers' (Microsoft.Sort) has no generated counterpart: none of ('SortingRowSource',) — the Etl.Core construct(s) that would represent this SSIS component type if translated — are referenced anywhere in the generated project. A project-wide presence check, not a per-instance match: a real, tool-detected gap, not a naming ambiguity.
```

## Unverifiable (13)

### [InternalTransform] `CSPLIT_Validity` — **Unverifiable**

```
'CSPLIT_Validity' (Microsoft.ConditionalSplit) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.
```

<details><summary>Semantic evidence</summary>

```json
{
  "outputs": [
    {
      "output_name": "Valid",
      "is_default": false,
      "friendly_expression": "!ISNULL(CustomerID_i4) && FINDSTRING(TRIM(Email),\"@\",1) > 0",
      "evaluation_order": 0
    },
    {
      "output_name": "Invalid",
      "is_default": true,
      "friendly_expression": null,
      "evaluation_order": null
    }
  ]
}
```

</details>

### [InternalTransform] `DCONV_Types` — **Unverifiable**

```
'DCONV_Types' (Microsoft.DataConvert) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `DER_Enrich` — **Unverifiable**

```
'DER_Enrich' (Microsoft.DerivedColumn) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.
```

<details><summary>Semantic evidence</summary>

```json
{
  "columns": [
    {
      "output_name": "FullName",
      "friendly_expression": "(DT_WSTR,200)(TRIM(FirstName) + \" \" + TRIM(LastName))"
    },
    {
      "output_name": "EmailDomain",
      "friendly_expression": "(DT_WSTR,200)(FINDSTRING(TRIM(Email),\"@\",1) > 0 ? SUBSTRING(TRIM(Email),FINDSTRING(TRIM(Email),\"@\",1) + 1,100) : \"(none)\")"
    },
    {
      "output_name": "TenureDays",
      "friendly_expression": "ISNULL(SignupDate_dt) ? -1 : DATEDIFF(\"dd\",SignupDate_dt,GETDATE())"
    }
  ]
}
```

</details>

### [InternalTransform] `DER_TagInvalid` — **Unverifiable**

```
'DER_TagInvalid' (Microsoft.DerivedColumn) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.
```

<details><summary>Semantic evidence</summary>

```json
{
  "columns": [
    {
      "output_name": "RowSource",
      "friendly_expression": "\"Invalid\""
    }
  ]
}
```

</details>

### [InternalTransform] `DER_TagValid` — **Unverifiable**

```
'DER_TagValid' (Microsoft.DerivedColumn) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.
```

<details><summary>Semantic evidence</summary>

```json
{
  "columns": [
    {
      "output_name": "RowSource",
      "friendly_expression": "\"Valid\""
    }
  ]
}
```

</details>

### [InternalTransform] `AGG_ByRegion` — **Unverifiable**

```
'AGG_ByRegion' (Microsoft.Aggregate) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.
```

<details><summary>Semantic evidence</summary>

```json
{
  "columns": [
    {
      "name": "Region",
      "aggregation_type": {
        "property_name": "AggregationType",
        "raw_value": "0",
        "description": "Specifies the type of aggregation."
      },
      "source_component": "LKP_Country",
      "source_column": "Region"
    },
    {
      "name": "CustomerCount",
      "aggregation_type": {
        "property_name": "AggregationType",
        "raw_value": "1",
        "description": "Specifies the type of aggregation."
      },
      "source_component": "FF_SRC_Customers2",
      "source_column": "CustomerID"
    }
  ]
}
```

</details>

### [InternalTransform] `LKP_Country` — **Unverifiable**

```
'LKP_Country' (Microsoft.Lookup) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.
```

<details><summary>Semantic evidence</summary>

```json
{
  "sql_command": "SELECT CountryName, CountryCode, Region FROM dbo.CountryLookup",
  "connection_type": {
    "property_name": "ConnectionType",
    "raw_value": "0",
    "description": "Specifies the type of connection used to access the reference dataset."
  },
  "cache_type": {
    "property_name": "CacheType",
    "raw_value": "0",
    "description": "Specifies the cache type of the lookup table."
  },
  "no_match_behavior": {
    "property_name": "NoMatchBehavior",
    "raw_value": "1",
    "description": "Specifies how the Lookup transformation handles rows without matching entries in the reference data set."
  },
  "join_columns": [
    {
      "input_column": "Country",
      "join_to_reference_column": "CountryName"
    }
  ],
  "output_columns": [
    {
      "name": "CountryCode",
      "copy_from_reference_column": "CountryCode"
    },
    {
      "name": "Region",
      "copy_from_reference_column": "Region"
    }
  ],
  "match_output_destination": "MCAST_Matched",
  "no_match_output_destination": "RC_UnmatchedRows"
}
```

</details>

### [InternalTransform] `MCAST_Matched` — **Unverifiable**

```
'MCAST_Matched' (Microsoft.Multicast) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `CSPLIT_ByCountry` — **Unverifiable**

```
'CSPLIT_ByCountry' (Microsoft.ConditionalSplit) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.
```

<details><summary>Semantic evidence</summary>

```json
{
  "outputs": [
    {
      "output_name": "Canada",
      "is_default": false,
      "friendly_expression": "TRIM(Country) == \"Canada\"",
      "evaluation_order": 0
    },
    {
      "output_name": "RestOfWorld",
      "is_default": true,
      "friendly_expression": null,
      "evaluation_order": null
    }
  ]
}
```

</details>

### [InternalTransform] `DCONV_MergeId` — **Unverifiable**

```
'DCONV_MergeId' (Microsoft.DataConvert) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
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
'MRG_CustomerContacts' (Microsoft.MergeJoin) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.
```

<details><summary>Semantic evidence</summary>

```json
{
  "join_type": {
    "property_name": "JoinType",
    "raw_value": "2",
    "description": "Specifies the type of join to perform."
  },
  "num_key_columns": 1,
  "treat_nulls_as_equal": true,
  "input_columns": [
    {
      "name": "CustomerID_i4",
      "side": "left",
      "sort_key_position": 1
    },
    {
      "name": "Email",
      "side": "left",
      "sort_key_position": null
    },
    {
      "name": "CustomerID_i4",
      "side": "right",
      "sort_key_position": 1
    },
    {
      "name": "ContactPhone",
      "side": "right",
      "sort_key_position": null
    },
    {
      "name": "PreferredChannel",
      "side": "right",
      "sort_key_position": null
    }
  ],
  "output_columns": [
    {
      "name": "Email",
      "source_column": "Email"
    },
    {
      "name": "CustomerID_i4",
      "source_column": "CustomerID_i4"
    },
    {
      "name": "ContactPhone",
      "source_column": "ContactPhone"
    },
    {
      "name": "PreferredChannel",
      "source_column": "PreferredChannel"
    }
  ]
}
```

</details>

## NormalizedMatch (1)

### [DestinationInput] `OLEDST_CustomerEnriched` — **NormalizedMatch**

```
vs. generated class 'CustomerEnriched' (Model\CustomerEnriched.cs):
  = CustomerID: SSIS 'i4' -> 'int?'.
  = EmailDomain: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
  = RowSource: SSIS 'wstr' -> 'string'.
  ~ SignupDate: SSIS 'dbDate' -> 'DateOnly?' (accepted modern equivalent of 'DateTime').
  = TenureDays: SSIS 'i4' -> 'int'.
```

## Match (8)

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

### [DestinationInput] `OLEDST_RegionSummary` — **Match**

```
vs. generated class 'RegionSummary' (Model\RegionSummary.cs):
  = CustomerCount: SSIS 'i4' -> 'int'.
  = Region: SSIS 'wstr' -> 'string'.
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

### [DestinationInput] `OLEDST_CustomerMerged` — **Match**

```
vs. generated class 'CustomerMerged' (Model\CustomerMerged.cs):
  = CustomerID: SSIS 'i4' -> 'int?'.
  = Email: SSIS 'wstr' -> 'string'.
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

### [DestinationInput] `OLEDST_CustomerWithContact` — **Match**

```
vs. generated class 'CustomerWithContact' (Model\CustomerWithContact.cs):
  = ContactPhone: SSIS 'wstr' -> 'string?'.
  = CustomerID: SSIS 'i4' -> 'int?'.
  = Email: SSIS 'wstr' -> 'string'.
  = PreferredChannel: SSIS 'wstr' -> 'string?'.
```
