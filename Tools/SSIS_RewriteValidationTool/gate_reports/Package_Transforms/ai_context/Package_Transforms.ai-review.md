# Tier 2 AI-assisted adjudication — Package_Transforms

Component: CSPLIT_Validity (Package_Transforms)
Verdict: AiConfirmedMatch
Rationale: The SSIS "Valid" output expression is `!ISNULL(CustomerID_i4) && FINDSTRING(TRIM(Email),"@",1) > 0` with "Invalid" as the default output. The generated `CSPLIT_ValidityRouter.SelectBranch` reproduces exactly this: `if ((!((SsisFn.ToNullableI4(row.CustomerID) is null)) && (SsisFn.FindString(SsisFn.Trim(row.Email), "@", 1) > 0))) return 0;` (branch 0 = "Valid" in the `ConditionalSplitStep`'s branch array) `return 1; // Invalid` as the fallthrough default. The null-check, the `FindString`/`Trim` calls, and the `> 0` comparison all line up term-for-term with the SSIS expression.

Component: DER_Enrich (Package_Transforms)
Verdict: AiSuspectedDiscrepancy
Rationale: `FullName` and `EmailDomain` match exactly: the SSIS expressions `(DT_WSTR,200)(TRIM(FirstName) + " " + TRIM(LastName))` and `(DT_WSTR,200)(FINDSTRING(TRIM(Email),"@",1) > 0 ? SUBSTRING(TRIM(Email),FINDSTRING(TRIM(Email),"@",1) + 1,100) : "(none)")` are reproduced term-for-term in `DER_Enrich.cs` (including the `WidthGuard.Wstr(..., 200, ...)` enforcing the `DT_WSTR,200` cast) and the ternary/`FindString`/`Substring`/literal `"(none)"` fallback. `TenureDays`'s null-check and `DATEDIFF("dd",...)` structure also match (`-1` when null, else day-diff). The discrepancy is the second `DATEDIFF` argument: SSIS uses `GETDATE()` (the current wall-clock time evaluated live, per row, as the data flow runs), while the generated code substitutes `ctx.LoadedAtUtc` (UTC, and named as a load-level timestamp rather than "now"). Since `TenureDays` is computed as a day-count difference, a timezone offset or an evaluation-time gap near a day boundary could shift the result by a day for rows near midnight — a citable, not merely cosmetic, difference in this specific column.

Component: DER_TagInvalid (Package_Transforms)
Verdict: AiConfirmedMatch
Rationale: The SSIS expression is the literal `"Invalid"` for the `RowSource` output column. `DER_TagInvalid.RowSource` returns the literal string `"Invalid"` unconditionally. Exact match.

Component: DER_TagValid (Package_Transforms)
Verdict: AiConfirmedMatch
Rationale: The SSIS expression is the literal `"Valid"` for the `RowSource` output column. `DER_TagValid.RowSource` returns the literal string `"Valid"` unconditionally. Exact match.

Component: AGG_ByRegion (Package_Transforms)
Verdict: AiConfirmedMatch
Rationale: The SSIS Aggregate declares `Region` grouped via `AggregationType raw_value "0"` (Group by) sourced from `LKP_Country.Region`, and `CustomerCount` via `AggregationType raw_value "1"` (Count) sourced from `FF_SRC_Customers2.CustomerID`. The generated `AGG_ByRegion()` method filters rows to only those whose country resolved in the lookup cache (`row => lKP_CountryCache.ContainsKey(row.Country)` — implementing the join-then-group precondition), groups by `lKP_CountryCache[row.Country].Region` (the same Region-from-lookup source), and computes `CustomerCount = rows.Count(r => r.CustomerID != null)` — a count of non-null CustomerID, matching a Count aggregation on that column. `RegionSummaryTransform` then passes `Region` straight through and narrows `CustomerCount` (`SsisFn.NarrowI8ToI4`) into the destination shape, consistent with the declared output columns.

Component: LKP_Country (Package_Transforms)
Verdict: AiInconclusive
Rationale: The packet's right-side excerpt for this component is identical to `AGG_ByRegion`'s excerpt — the top-level `DFT_LookupAndAggregate` wiring (`source = AGG_ByRegion(); transform = new RegionSummaryTransform(); sink = OLEDST_RegionSummary();`) — and does not show where or how `lKP_CountryCache` itself is built. The declared evidence says the lookup runs `SELECT CountryName, CountryCode, Region FROM dbo.CountryLookup`, joins `Country` to `CountryName`, and outputs both `CountryCode` and `Region` with no-match rows routed to `RC_UnmatchedRows`. The excerpts shown only exercise `lKP_CountryCache[row.Country].Region` (Region) and a `.ContainsKey` no-match filter; there is no visible evidence in this packet of the cache's own SQL source, nor of `CountryCode` being consumed anywhere, nor of what happens to non-matching rows beyond being filtered out of the aggregate (the two `RC_*` RowCount components that would confirm row-level accounting for matched/unmatched are themselves flagged as left-only gaps below). To judge this component I would need to see the actual construction of `lKP_CountryCache` (its source query and both mapped columns) and confirm `CountryCode` is used somewhere in the pipeline or legitimately unused.

Component: CSPLIT_ByCountry (Package_Transforms)
Verdict: AiConfirmedMatch
Rationale: The SSIS "Canada" output expression is `TRIM(Country) == "Canada"` with "RestOfWorld" as the default. The generated `CSPLIT_ByCountryRouter.SelectBranch` reproduces this: `if (string.Equals(SsisFn.Trim(row.Country), "Canada", System.StringComparison.Ordinal)) return 0;` (branch 0 = "Canada") `return 1; // RestOfWorld` as the fallthrough default. `StringComparison.Ordinal` matches SSIS's case-sensitive `==` string comparison semantics.

Component: MRG_CustomerContacts (Package_Transforms)
Verdict: AiConfirmedMatch
Rationale: The SSIS MergeJoin declares `join_type raw_value "2"` (Inner), one key column (`CustomerID_i4` on both sides), `treat_nulls_as_equal: true`, and outputs `Email, CustomerID_i4, ContactPhone, PreferredChannel`. The generated `MergeJoinRowSource<..., int?, ...>` is constructed with `MergeJoinType.Inner` and `Comparer<int?>.Default` — the default nullable-int comparer treats two nulls as equal (returns 0), consistent with `treat_nulls_as_equal: true`. `CustomerWithContactTransform.Map` then projects `CustomerID = row.CustomerID_i4, Email, ContactPhone, PreferredChannel` — exactly the four declared output columns, with no extras or omissions.

Component: UNION_Recombine (Package_Transforms)
Verdict: AiConfirmedGap
Rationale: Tier 1 confirmed no `ConcatenatingRowSource`-shaped construct exists anywhere in the generated project, and the packet's bounded name search for "UNION_Recombine" returned zero matches anywhere in the generated project. With no candidate to evaluate — under a different name or otherwise — there is nothing to weigh against `AiFoundUnmappedImplementation`; Tier 1's gap stands.

Component: RC_MatchedRows (Package_Transforms)
Verdict: AiConfirmedGap
Rationale: Tier 1 confirmed no `CountingRowSource`-shaped construct exists anywhere in the generated project, and the bounded name search for "RC_MatchedRows" returned zero matches. No candidate is present to judge as an unmapped implementation, so this is a confirmed gap — worth noting this is one of the two RowCount tasks that would otherwise provide auditable evidence of how many rows matched in `LKP_Country`, which the `LKP_Country` adjudication above already flagged as unverifiable without them.

Component: RC_UnmatchedRows (Package_Transforms)
Verdict: AiConfirmedGap
Rationale: Tier 1 confirmed no `CountingRowSource`-shaped construct exists anywhere in the generated project, and the bounded name search for "RC_UnmatchedRows" returned zero matches. No candidate is present to judge, so the gap stands — this is the no-match counter for `LKP_Country`'s `no_match_output_destination`, and its absence means unmatched-lookup rows are silently dropped by the `ContainsKey` filter in `AGG_ByRegion()` with no generated audit trail of how many were dropped, unlike the original package which counted them explicitly.

Component: MRG_SortedUnion (Package_Transforms)
Verdict: AiConfirmedGap
Rationale: Tier 1 confirmed no `MergeInterleaveRowSource`-shaped construct exists anywhere in the generated project, and the bounded name search for "MRG_SortedUnion" returned zero matches. No candidate is present to judge, so the gap stands.

Component: SORT_Canada (Package_Transforms)
Verdict: AiConfirmedGap
Rationale: Tier 1 confirmed no `SortingRowSource`-shaped construct exists anywhere in the generated project, and the bounded name search for "SORT_Canada" returned zero matches. No candidate is present to judge, so the gap stands. This is one of four missing Sort components feeding into either `MRG_SortedUnion` or `MRG_CustomerContacts`'s merge join; the `MRG_CustomerContacts` excerpt shown elsewhere in this packet builds its `MergeJoinRowSource` directly from unsorted `CsvRowSource`s with no visible sort step in between, so if the generated merge-join implementation assumes sorted input (as the class name `MergeJoinRowSource` suggests), this missing sort could be a genuine correctness gap rather than just an unmapped rename — worth a human check of whether `MergeJoinRowSource` sorts internally.

Component: SORT_RestOfWorld (Package_Transforms)
Verdict: AiConfirmedGap
Rationale: Tier 1 confirmed no `SortingRowSource`-shaped construct exists anywhere in the generated project, and the bounded name search for "SORT_RestOfWorld" returned zero matches. No candidate is present to judge, so the gap stands — same missing-sort concern noted for `SORT_Canada` applies here.

Component: SORT_Contacts (Package_Transforms)
Verdict: AiConfirmedGap
Rationale: Tier 1 confirmed no `SortingRowSource`-shaped construct exists anywhere in the generated project, and the bounded name search for "SORT_Contacts" returned zero matches. No candidate is present to judge, so the gap stands. This sort fed the right side of `MRG_CustomerContacts`'s merge join on `CustomerID_i4`; the generated `MergeJoinRowSource` is built directly from `FF_SRC_Contacts`'s raw CSV source with no visible sort in between, reinforcing the same correctness concern raised for `SORT_Canada`/`SORT_RestOfWorld`.

Component: SORT_Customers (Package_Transforms)
Verdict: AiConfirmedGap
Rationale: Tier 1 confirmed no `SortingRowSource`-shaped construct exists anywhere in the generated project, and the bounded name search for "SORT_Customers" returned zero matches. No candidate is present to judge, so the gap stands — same missing-sort concern as the other three Sort components, this one feeding the left side of `MRG_CustomerContacts`'s merge join.
