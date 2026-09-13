# AI evidence packet (combined) — Package_Advanced

- .dtsx file: `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package_Advanced.dtsx`
- Generated file: `D:\tmp\Sandeep_Packages\gen\generate\Package_Advanced\Package_Advanced.cs`
- 0 component(s), 1 distinct shared source file(s) (each shown once below)

Adjudicate every component below in ONE pass — see docs/ai-validation-prompt.md for the required output (one Component/Verdict/Rationale block per component). Each component references shared source files by path instead of repeating their content.

## Sources

### `D:\tmp\Sandeep_Packages\gen\generate\Package_Advanced\Package_Advanced.cs (method OLESRC_CustomerIds)`

```csharp

        return new SqlRowSource<DFT_FlagCustomersSqlRow>("OLESRC_CustomerIds", new SqlSourceOptions { ConnectionString = SqlConnectionStringFactory.Build(Db()), CommandText = "SELECT DISTINCT CustomerID FROM dbo.Orders" }, DFT_FlagCustomersSqlRowReader.Read, uow);
    
```

## Components

## Correlation gaps

Tier 1 knows what the SSIS side declares (`declared_columns`) for each of these but could not correlate ANY generated construct to compare it against. Your job here has two parts: (1) decide whether `known_correlate` (if present) or one of `unconfirmed_candidates` (if not) is genuinely the generated code for this component — cite why; (2) only if you're confident which one it is, judge whether it correctly implements `declared_columns`. If nothing plausible is present, that is `AiInconclusive`, stated as such — do not force a guess.

### OLECMD_SetFlag

- Component path: `Package\DFT_FlagCustomers\OLECMD_SetFlag`

**Tier 1 finding:** 'OLECMD_SetFlag' (Microsoft.OLEDBCommand) is not a database destination Gate 1 can identify by (schema, table), and no generated factory method is named after this component either — no schema-correlation mechanism matched (1 declared column(s) not compared).

**Declared columns (left/SSIS side):**

```json
[{"name":"@CustomerID","ssis_type_raw":"i4"}]
```

**Known correlate** (resolved via Gate 1's own SQL-content correlation):

- Generated object: `DFT_FlagCustomers` (OleDbCommandStep) in `Package_Advanced.cs`
- Related source files: `D:\tmp\Sandeep_Packages\gen\generate\Package_Advanced\Package_Advanced.cs (method OLESRC_CustomerIds)`

```csharp

        var source = OLESRC_CustomerIds(uow);
        var step = new OleDbCommandStep<DFT_FlagCustomersSqlRow>(
            "DFT_FlagCustomers",
            source,
            "EXEC dbo.usp_SetCustomerFlag {0}, N'flagged'",
            row => new object?[] { row.CustomerID },
            Log<OleDbCommandStep<DFT_FlagCustomersSqlRow>>());
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```
