# Gate 3 report — Package

Per-component input/output schema parity. Declared column name + SSIS-type-to-.NET-type shape only — never actual data or execution. Every pipeline component is accounted for: a component with no populated externalMetadataColumns on either side (a pure pipeline-internal transform) is `Unverifiable`, not a silent pass.

| Verdict | Count |
|---|---|
| Unverifiable | 2 |
| NormalizedMatch | 2 |
| Match | 3 |

## Unverifiable (2)

### [InternalTransform] `SCR_ComputeCustomerTier` — **Unverifiable**

```
'SCR_ComputeCustomerTier' (Microsoft.ScriptComponentHost) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.
```

<details><summary>Semantic evidence</summary>

```json
{
  "files": [
    {
      "file_name": "main.cs",
      "source_code": "using System;\nusing Microsoft.SqlServer.Dts.Pipeline;\n\nnamespace SC_ComputeCustomerTier\n{\n    /// <summary>\n    /// Second row-level Script Component (requirement 4), this one in\n    /// DFT_BuildSalesSummary. Assigns a loyalty tier from the aggregated revenue and\n    /// stamps the processing time.\n    ///\n    /// Thresholds match sql\\04_verify_expected_output.sql exactly, so the package output\n    /// can be diffed against the pure T-SQL harness.\n    /// </summary>\n    [SSISScriptComponentEntryPoint]\n    public class ScriptMain : UserComponent\n    {\n        public override void Input0_ProcessInputRow(Input0Buffer Row)\n        {\n            double revenue = Row.TotalRevenue;\n\n            if (revenue >= 10000)     Row.CustomerTier = \"Platinum\";\n            else if (revenue >= 5000) Row.CustomerTier = \"Gold\";\n            else if (revenue >= 1000) Row.CustomerTier = \"Silver\";\n            else                      Row.CustomerTier = \"Bronze\";\n\n            Row.ProcessedDateTime = DateTime.Now;\n        }\n    }\n}\n"
    }
  ]
}
```

</details>

### [InternalTransform] `SCR_CleanseCustomerRow` — **Unverifiable**

```
'SCR_CleanseCustomerRow' (Microsoft.ScriptComponentHost) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.
```

<details><summary>Semantic evidence</summary>

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

</details>

## NormalizedMatch (2)

### [DestinationInput] `OLEDST_SalesSummary` — **NormalizedMatch**

```
vs. generated class 'SalesSummary' (Model\SalesSummary.cs):
  = CustomerID: SSIS 'i4' -> 'int'.
  = CustomerTier: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
  ~ LastOrderDate: SSIS 'dbDate' -> 'DateOnly' (accepted modern equivalent of 'DateTime').
  = ProcessedDateTime: SSIS 'dbTimeStamp2' -> 'DateTime'.
  = TotalOrders: SSIS 'i4' -> 'int'.
  = TotalRevenue: SSIS 'r8' -> 'double'.
```

### [SourceOutput] `OLESRC_OrdersAndDetails` — **NormalizedMatch**

```
vs. generated class 'SalesSummarySqlRow' (Sql\SalesSummarySqlRow.cs):
  = CustomerID: SSIS 'i4' -> 'int'.
  = Email: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
  ~ LastOrderDate: SSIS 'dbDate' -> 'DateOnly' (accepted modern equivalent of 'DateTime').
  = TotalOrders: SSIS 'i4' -> 'int'.
  = TotalRevenue: SSIS 'r8' -> 'double'.
```

## Match (3)

### [SourceOutput] `FF_SRC_Customers` — **Match**

```
vs. generated class 'StagingCustomersCsvRow' (Csv\StagingCustomersCsvRow.cs):
  = Country: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = FirstName: SSIS 'wstr' -> 'string'.
  = LastName: SSIS 'wstr' -> 'string'.
  = Phone: SSIS 'wstr' -> 'string'.
  = SignupDate: SSIS 'wstr' -> 'string'.
```

### [DestinationInput] `OLEDST_StagingCustomers` — **Match**

```
vs. generated class 'StagingCustomers' (Model\StagingCustomers.cs):
  = CleanEmail: SSIS 'wstr' -> 'string'.
  = Country: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = FirstName: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
  = IsValidRow: SSIS 'bool' -> 'bool'.
  = LastName: SSIS 'wstr' -> 'string'.
  = LoadDateTime: SSIS 'dbTimeStamp2' -> 'DateTime'.
  = Phone: SSIS 'wstr' -> 'string'.
  = SignupDate: SSIS 'wstr' -> 'string'.
```

### [DestinationInput] `OLEDST_StagingErrors` — **Match**

```
vs. generated class 'StagingCustomersError' (Model\StagingCustomersError.cs):
  = CustomerID: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = ErrorCode: SSIS 'i4' -> 'int'.
  = ErrorColumn: SSIS 'i4' -> 'int'.
  = FirstName: SSIS 'wstr' -> 'string'.
  = LastName: SSIS 'wstr' -> 'string'.
  i FailedAt: generated property has no matching left-side column. Not blocking — a destination may legitimately model an unmapped column.
```
