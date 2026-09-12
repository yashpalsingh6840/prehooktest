# AI evidence packet — Package / SCR_CleanseCustomerRow

- Scope: `DataFlowComponent`
- Component path: `Package\DFT_LoadCustomers\SCR_CleanseCustomerRow`
- .dtsx file: `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package.dtsx`
- Generated file: `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs`
- Generated method: `DFT_LoadCustomers`

## Tier 1 finding

'SCR_CleanseCustomerRow' (Microsoft.ScriptComponentHost) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

## Left-side (SSIS) semantic evidence

```json
{
  "files": [
    {
      "file_name": "main.cs",
      "source_code": "using System;\nusing System.Text.RegularExpressions;\nusing Microsoft.SqlServer.Dts.Pipeline;\n\nnamespace SC_CleanseCustomerRow\n{\n    /// <summary>\n    /// Row-level cleansing performed inside the data flow (requirement 4).\n    /// Trims and normalises the incoming CSV values, derives FullName, validates the\n    /// e-mail address and the customer id, and stamps a load timestamp.\n    /// </summary>\n    [SSISScriptComponentEntryPoint]\n    public class ScriptMain : UserComponent\n    {\n        // Deliberately simple - enough to reject \"not-an-email\" while accepting the rest.\n        private static readonly Regex EmailPattern =\n            new Regex(@\"^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$\", RegexOptions.Compiled);\n\n        public override void Input0_ProcessInputRow(Input0Buffer Row)\n        {\n            string first = (Row.FirstName ?? string.Empty).Trim();\n            string last = (Row.LastName ?? string.Empty).Trim();\n            string email = (Row.Email ?? string.Empty).Trim();\n            string customerId = (Row.CustomerID ?? string.Empty).Trim();\n\n            Row.FullName = (first + \" \" + last).Trim();\n\n            Row.CleanEmail = email.ToLowerInvariant();\n\n            // A row is valid only when the customer id parses as an integer AND the\n            // e-mail looks like an address. int.TryParse(\"\") is false - which is why the\n            // T-SQL harness has to use NULLIF(...,'') to match this behaviour.\n            int parsedId;\n            bool validId = int.TryParse(customerId, out parsedId);\n            bool validEmail = email.Length > 0 && EmailPattern.IsMatch(email);\n\n            Row.IsValidRow = validId && validEmail;\n\n            Row.LoadDateTime = DateTime.Now;\n        }\n    }\n}\n"
    }
  ]
}
```

## Right-side (generated C#) method excerpt

```csharp

        var source = FF_SRC_Customers();
        var transform = new StagingCustomersTransform();
        var sink = OLEDST_StagingCustomers();

        var step = new DataFlowStep<StagingCustomersCsvRow, StagingCustomers>("DFT_LoadCustomers", source, transform, sink, Log<DataFlowStep<StagingCustomersCsvRow, StagingCustomers>>());
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```

## Related generated source

The excerpt above only wires together factory calls; each referenced identifier's own definition (where found in this package's own generated files) follows, since that is typically where the actual per-row/per-script logic lives.

### `FF_SRC_Customers` — `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs (method FF_SRC_Customers)`

```csharp

        return new CsvRowSource<StagingCustomersCsvRow>("FF_SRC_Customers", new CsvSourceOptions { FilePath = File("StagingCustomers") }, new StagingCustomersCsvRowMap());
    
```

### `StagingCustomersTransform` — `Fills\StagingCustomersTransform.Fills.cs`

```csharp
using System.Text.RegularExpressions;
using Etl.Core.Abstractions;
using Package.Csv;

namespace Package.Mapping;

public sealed partial class StagingCustomersTransform
{
    private static readonly Regex EmailPattern =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    // ssisx-fill: GapId=SCRIPT-COLUMN:Package:StagingCustomers.SCR_CleanseCustomerRow Author=GitHub Copilot Date=2026-09-09 EvidenceSha256=65ea553cecb73be073ab978403716018f50f5590a3f6965e28f9dc8cfad61102
    private partial SCR_CleanseCustomerRowResult SCR_CleanseCustomerRow(StagingCustomersCsvRow row, in RowContext ctx)
    {
        var fullName = (row.FirstName.Trim() + " " + row.LastName.Trim()).Trim();
        var cleanEmail = row.Email.Trim().ToLowerInvariant();

        var email = row.Email.Trim();
        var validId = int.TryParse(row.CustomerID.Trim(), out _);
        var validEmail = email.Length > 0 && EmailPattern.IsMatch(email);
        var isValidRow = validId && validEmail;

        var loadDateTime = ctx.LoadedAtUtc;

        return new(FullName: fullName, CleanEmail: cleanEmail, IsValidRow: isValidRow, LoadDateTime: loadDateTime);
    }
}

```

### `StagingCustomersTransform` — `Mapping\StagingCustomersTransform.cs`

```csharp
using Etl.Core.Abstractions;
using Package.Csv;
using Package.Model;

namespace Package.Mapping;

internal readonly record struct SCR_CleanseCustomerRowResult(string FullName, string CleanEmail, bool IsValidRow, DateTime LoadDateTime);

/// <summary>Has 1 unimplemented Tier-2 seam(s): each is a Script Component, computing
/// one or more columns together, whose logic this tool does not translate. Implement each
/// one in a SECOND PART of this partial class -- a class part, not just a method body, so
/// the port can declare its own fields (a compiled Regex, a lookup table). Until then this
/// project deliberately does NOT compile: CS8795, one error per unfilled seam. Put the
/// part in fills/&lt;Package&gt;/StagingCustomersTransform.Fills.cs and run `ssisx apply-fills`.</summary>
public sealed partial class StagingCustomersTransform : IRowTransform<StagingCustomersCsvRow, StagingCustomers>
{
    public StagingCustomers Map(StagingCustomersCsvRow row, in RowContext ctx)
    {
        var sCR_CleanseCustomerRowResult = SCR_CleanseCustomerRow(row, ctx);
        return new()
        {
            CustomerID = row.CustomerID,
            FirstName = row.FirstName,
            LastName = row.LastName,
            Email = row.Email,
            Phone = row.Phone,
            SignupDate = row.SignupDate,
            Country = row.Country,
            FullName = sCR_CleanseCustomerRowResult.FullName,
            CleanEmail = sCR_CleanseCustomerRowResult.CleanEmail,
            IsValidRow = sCR_CleanseCustomerRowResult.IsValidRow,
            LoadDateTime = sCR_CleanseCustomerRowResult.LoadDateTime,
        };
    }

    /// <summary>Script Component 'SCR_CleanseCustomerRow', producing 4 column(s)
    /// together: FullName, CleanEmail, IsValidRow, LoadDateTime. Work packet: SCRIPT-COLUMN StagingCustomers.SCR_CleanseCustomerRow.</summary>
    private partial SCR_CleanseCustomerRowResult SCR_CleanseCustomerRow(StagingCustomersCsvRow row, in RowContext ctx);
}

```

### `OLEDST_StagingCustomers` — `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs (method OLEDST_StagingCustomers)`

```csharp

        return new RedirectingSqlSink<StagingCustomers, StagingCustomersError>(
            OLEDST_StagingErrors(), StagingCustomersErrorMap.Map, Log<RedirectingSqlSink<StagingCustomers, StagingCustomersError>>());
    
```
