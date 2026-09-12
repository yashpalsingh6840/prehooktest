# AI review — Package_Transforms

Produced by hand-running `docs/ai-validation-prompt.md` against every evidence packet in this
directory, using **only** each packet's own content (no other file in the generated project or
its .dtsx was consulted beyond what each packet already resolved — the independence rule the
prompt itself restates). Advisory only — see the prompt document for what this means and does
not mean.

---

Component: DER_Enrich (Package_Transforms)
Verdict: AiSuspectedDiscrepancy
Rationale: `FullName` and `EmailDomain` are exact, faithful ports — `Mapping/DER_Enrich.cs`
carries the original FriendlyExpression as a `//` comment directly above each method, and the
implementation (`$"{SsisFn.Trim(row.FirstName)} {SsisFn.Trim(row.LastName)}"`; the
FINDSTRING/SUBSTRING ternary) matches both expressions token-for-token in structure. `TenureDays`
substitutes `ctx.LoadedAtUtc` for the original expression's `GETDATE()`
(`ISNULL(SignupDate_dt) ? -1 : DATEDIFF("dd",SignupDate_dt,GETDATE())`). `ctx.LoadedAtUtc` appears
to be one fixed timestamp for the whole batch load, not the moment each row is evaluated — for a
column that is itself a *computed numeric value* (days of tenure), this could shift the result
for different rows if the batch takes non-trivial wall-clock time to run, which is a more material
difference than a bare audit-timestamp substitution. Worth confirming with a human whether a
single fixed batch time is the intended replacement for per-row `GETDATE()` here.

Component: CSPLIT_Validity (Package_Transforms)
Verdict: AiConfirmedMatch
Rationale: FriendlyExpression `!ISNULL(CustomerID_i4) && FINDSTRING(TRIM(Email),"@",1) > 0` for
the "Valid" branch matches `Mapping/CSPLIT_ValidityRouter.cs`'s `SelectBranch`:
`!(SsisFn.ToNullableI4(row.CustomerID) is null) && SsisFn.FindString(SsisFn.Trim(row.Email), "@", 1) > 0`
→ returns 0 (the "Valid" branch, first in the branch array); every other row falls through to
return 1 ("Invalid", the declared default output) — structurally identical to the SSIS shape
(one explicit condition plus a default catch-all).

Component: CSPLIT_ByCountry (Package_Transforms)
Verdict: AiConfirmedMatch
Rationale: FriendlyExpression `TRIM(Country) == "Canada"` matches
`Mapping/CSPLIT_ByCountryRouter.cs`: `string.Equals(SsisFn.Trim(row.Country), "Canada", StringComparison.Ordinal)`
→ 0 ("Canada"), else 1 ("RestOfWorld", the declared default) — exact match, including the
ordinal (case-sensitive) comparison SSIS's `==` string operator uses by default.

Component: DER_TagInvalid (Package_Transforms)
Verdict: AiConfirmedMatch
Rationale: A constant-literal FriendlyExpression (`"Invalid"`) — `Mapping/DER_TagInvalid.cs`'s
`RowSource()` returns the literal string `"Invalid"`. Trivial, exact match.

Component: DER_TagValid (Package_Transforms)
Verdict: AiConfirmedMatch
Rationale: Same as DER_TagInvalid — FriendlyExpression `"Valid"` matches
`Mapping/DER_TagValid.cs`'s `RowSource()` returning the literal `"Valid"` exactly.

Component: AGG_ByRegion (Package_Transforms)
Verdict: AiConfirmedMatch
Rationale: `Region` (aggregation_type raw=0, source LKP_Country.Region) resolves via
`lKP_CountryCache[row.Country].Region` in the `AGG_ByRegion()` factory method — matches the
declared group-by source exactly. `CustomerCount` (aggregation_type raw=1, source
FF_SRC_Customers2.CustomerID) is computed as `rows.Count(r => r.CustomerID != null)` — a count of
non-null values in the named column, which is precisely Microsoft's documented "Count" operation
semantics ("Returns the number of items in a group," ignoring nulls in the counted column) as
opposed to `CountDistinct`/`Sum`/`GroupBy`. This is also a useful, independently-observed data
point (not asserted as a general rule): in this real generated pair, `AggregationType` raw value
`1` behaves exactly like "Count." Scoped narrowly to AGG_ByRegion's own aggregation computation —
see LKP_Country's own verdict for the separate question of which rows even reach this aggregation.

Component: LKP_Country (Package_Transforms)
Verdict: AiSuspectedDiscrepancy
Rationale: The join/cache/output-column mapping is a confirmed match: `RunAsync`'s
`lKP_CountryCache = await LKP_CountryCache.LoadAsync<string>(..., r => r.CountryName, ct)` loads
the entire reference dataset once before the transaction begins, consistent with the declared
`CacheType` raw=0 and SSIS's documented Full Cache behavior; the join key (`Country` →
`CountryName`) and copied output columns (`CountryCode`, `Region`) all match the declared shape
exactly. However, the declared `no_match_output_destination` is `RC_UnmatchedRows` — a dedicated
row counter, implying the original design tracks how many rows failed to match — but the
generated `AGG_ByRegion()` factory implements the join as a plain filter,
`row => lKP_CountryCache.ContainsKey(row.Country)`, with no reference anywhere to a matched/
unmatched row count. Combined with Gate 3's own Phase A finding that `CountingRowSource` (the
Etl.Core construct that would represent a Row Count transform) has zero references anywhere in
this generated project, this is a real, specific, semantically-confirmed discrepancy: non-
matching rows are silently excluded from the aggregate, and the original's explicit "count and
expose how many didn't match" signal (`User::UnmatchedRows`) has no counterpart in the rewrite at
all, not merely a differently-named implementation of it.

Component: MRG_CustomerContacts (Package_Transforms)
Verdict: AiInconclusive
Rationale: `MergeJoinRowSource<...>(..., MergeJoinType.Inner, ...)` directly confirms the declared
`JoinType` raw value `2` means Inner Join (an empirical data point from this real pair, not
assumed). The output shape is an exact 1:1 field copy in `Mapping/CustomerWithContactTransform.cs`
(Email, CustomerID_i4, ContactPhone, PreferredChannel all pass straight through, matching the
declared `source_column` for each). However, the actual join key selectors
(`MRG_CustomerContactsMapper.LeftKey` / `.RightKey`) are referenced from inside the
`MRG_CustomerContacts()` factory method but their own bodies were not included in this packet (a
second level of indirection this run's evidence collection does not follow) — so while an `int?`
key type and the presence of only one common integer-typed column (`CustomerID_i4`, at
`sort_key_position` 1 on both sides) make it very likely the join key is correct, this packet does
not let me actually confirm the join predicate itself, which is the part of a Merge Join most
likely to silently produce wrong results if it doesn't match. Needed to resolve: the body of
`MRG_CustomerContactsMapper.LeftKey`/`RightKey`.

---

## Cross-cutting observation (not a per-component verdict)

Three separate components — `DER_Enrich` (TenureDays), `SCR_ComputeCustomerTier`
(ProcessedDateTime), and `SCR_CleanseCustomerRow` (LoadDateTime, in the companion `Package`
review) — all substitute a single `ctx.LoadedAtUtc` value for what the original computed as
`GETDATE()` / `DateTime.Now` at the moment each row was processed. This looks like a deliberate,
systematic design choice (one consistent, replayable timestamp per load, rather than many
slightly-different wall-clock reads) rather than three independent bugs, but it is a real,
consistently-applied behavioral difference from every original script/expression — worth
confirming once, as a design decision, rather than three separate times.
