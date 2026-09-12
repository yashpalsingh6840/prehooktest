# AI evidence packet — Package_Transforms / MRG_CustomerContacts

- Scope: `DataFlowComponent`
- Component path: `Package\DFT_SortAndMergeJoin\MRG_CustomerContacts`
- .dtsx file: `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package_Transforms.dtsx`
- Generated file: `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs`
- Generated method: `DFT_SortAndMergeJoin`

## Tier 1 finding

'MRG_CustomerContacts' (Microsoft.MergeJoin) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

## Left-side (SSIS) semantic evidence

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

## Right-side (generated C#) method excerpt

```csharp

        var source = MRG_CustomerContacts();
        var transform = new CustomerWithContactTransform();
        var sink = OLEDST_CustomerWithContact();

        var step = new DataFlowStep<MRG_CustomerContactsRow, CustomerWithContact>("DFT_SortAndMergeJoin", source, transform, sink, Log<DataFlowStep<MRG_CustomerContactsRow, CustomerWithContact>>());
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```

## Related generated source

The excerpt above only wires together factory calls; each referenced identifier's own definition (where found in this package's own generated files) follows, since that is typically where the actual per-row/per-script logic lives.

### `MRG_CustomerContacts` — `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method MRG_CustomerContacts)`

```csharp

        IRowSource<MRG_CustomerContactsLeftCsvRow> BuildLeft()
        {
            return new CsvRowSource<MRG_CustomerContactsLeftCsvRow>("FF_SRC_Customers3", new CsvSourceOptions { FilePath = File("MRG_CustomerContactsLeft") }, new MRG_CustomerContactsLeftCsvRowMap());
        }

        IRowSource<MRG_CustomerContactsRightCsvRow> BuildRight()
        {
            return new CsvRowSource<MRG_CustomerContactsRightCsvRow>("FF_SRC_Contacts", new CsvSourceOptions { FilePath = File("MRG_CustomerContactsRight") }, new MRG_CustomerContactsRightCsvRowMap());
        }

        return new MergeJoinRowSource<MRG_CustomerContactsLeftCsvRow, MRG_CustomerContactsRightCsvRow, int?, MRG_CustomerContactsRow>(
            "MRG_CustomerContacts", BuildLeft(), BuildRight(),
            MRG_CustomerContactsMapper.LeftKey, MRG_CustomerContactsMapper.RightKey,
            Comparer<int?>.Default,
            MergeJoinType.Inner,
            MRG_CustomerContactsMapper.Map);
    
```

### `CustomerWithContactTransform` — `Mapping\CustomerWithContactTransform.cs`

```csharp
using Etl.Core.Abstractions;
using Package_Transforms.Sql;
using Package_Transforms.Model;

namespace Package_Transforms.Mapping;

public sealed class CustomerWithContactTransform : IRowTransform<MRG_CustomerContactsRow, CustomerWithContact>
{
    public CustomerWithContact Map(MRG_CustomerContactsRow row, in RowContext ctx) => new()
    {
        CustomerID = row.CustomerID_i4,
        Email = row.Email,
        ContactPhone = row.ContactPhone,
        PreferredChannel = row.PreferredChannel,
    };
}

```

### `OLEDST_CustomerWithContact` — `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method OLEDST_CustomerWithContact)`

```csharp

        return new SqlBulkSink<CustomerWithContact>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<CustomerWithContact>>());
    
```
