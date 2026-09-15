# Multicast → Lookup chain support — design plan (not yet built)

## Status

Investigated 2026-09-15 against a real client portfolio
(`D:\PoC\SSIS_Packages_From_GitHub\Datawarehouse_ETL_SSIS_end-to-end_Project`). Not built —
this is a design plan only, written up on explicit request instead of building blind.

## The real gap, precisely

`PackagePlanner.ResolveBranch` (the forward chain-walker a Conditional Split/Multicast branch
uses to find its eventual destination) already recognizes `Microsoft.DerivedColumn`,
`Microsoft.UnionAll`, `Microsoft.Sort`, `Microsoft.Merge` as pure pass-through hops, and (for
Multicast only) a dead-end `Microsoft.RowCount` and `Microsoft.Aggregate`. It does **not**
recognize `Microsoft.Lookup` — hitting one produces the generic fallback gap:

```
Multicast output 'Multicast Output 1' on 'MLT - prod cat name' passes through
'LKP - products X orderItems' (Microsoft.Lookup) before reaching a destination -- not supported
```

This is the single blocker for `DWH_DimProducts` and `DWH - FactOrders 3` in the real
portfolio (confirmed via `svk walkthrough`, not guessed).

## Why this is a bigger feature than the Aggregate/AiPacketEmitter fixes done today

A Lookup is not a pure pass-through the way Sort/Merge/UnionAll are — it **adds new columns**
(the Lookup's own `CopyFromReferenceColumn` outputs) and requires a preloaded reference-table
cache at runtime. The already-existing "Lookup feeds a Multicast/Aggregate" support
(`GenerateLookupThenAggregateFlow`) solves the mirror-image problem (Lookup *before* the
Multicast); this is Lookup *after* a Multicast branch, on the way to that branch's own
destination — a different composition, needing its own resolution path.

## Real evidence: this is a common, valuable, CHAINED pattern — not a single hop

Read both blocked packages' own `svk walkthrough` output in full before designing anything.
Neither package needs just one Lookup after a Multicast branch — both are built from a
**repeated Lookup → Multicast → {reject row, continue} pattern**, chained many times over:

**`DWH_DimProducts`** (one repetition):
```
OLE_SRC - ODS Products
  -> LKP - Product cat name english          (Match -> continue; No-Match -> DER (default text) -> UnionAll)
  -> MLT - prod cat name                     (Multicast, 2 outputs)
       -> Output 1: DER - Functional Rejects -> OLE_DST - ADM Functional rejects   (reject sink)
       -> Output 2: ALL - Cat name (UnionAll, already-supported pass-through)
            -> LKP - products X orderItems   (Lookup #1, NOT supported: this is the reported gap)
            -> LKP - DimSeller get seller_key (Lookup #2, chained: its own join key,
                                                seller_id, is LKP #1's own output column)
            -> OLE_DST - DWH DimProducts     (real destination)
```

**`DWH - FactOrders 3`** (the SAME pattern, repeated **nine times** in series: `LKP - With ODS
Orders`, `LKP - With ODS OrderPayments`, `LKP - DWH DimCustomer`, `LKP - DWH DimSellers`,
`LKP - products`, `LKP - Price plus feight_value`, `LKP - purchase date to dimDate`,
`LKP - approved date to dimDate`, `LKP - delivered date to dimDate`, `LKP - review_id` — each
followed by its own Multicast fanning to a reject sink plus the continuation into the next
Lookup) before finally reaching `OLE_DST - DWH FactOrders`.

**This means a single-hop implementation (one Lookup, no chaining) would not close either real
package** — both need N sequential Lookups resolved in one branch's own chain, where a later
Lookup's join key can itself be an earlier Lookup's own output column
(`LKP - DimSeller get seller_key`'s key, `seller_id`, only exists via
`LKP - products X orderItems`'s own copied column). Scope the implementation for chaining from
the start, or explicitly stage it (see "Staged rollout" below) knowing stage 1 alone proves
nothing against the real motivating packages.

## What this needs, concretely

### 1. `ResolveBranch` (`PackagePlanner.cs`) — recognize Lookup as a walkable hop

Gated to `componentKind == "Multicast"` only, matching the existing RowCount/Aggregate
carve-outs (unevidenced for Conditional Split, so left alone there per the existing
discipline). When the walk hits `next.ComponentClassId == "Microsoft.Lookup"`:

- Follow the Lookup's own **Match** output only (never the No-Match output — that already
  routes to its own separate downstream chain in both real packages, e.g. the "no category
  name in english" default-text branch in `DWH_DimProducts`; that chain is a SEPARATE gap,
  out of scope here, see "Not covered" below).
- Record the Lookup component in a new ordered list (parallel to the existing
  `derivedColumns` list), e.g. `lookupsInChain: List<PipelineComponentSpec>` on
  `ConditionalSplitBranchPlan`.
- Continue the walk from the Lookup's Match output's own downstream path, same as any other
  recognized pass-through.

### 2. `ConditionalSplitBranchPlan` — carry the resolved chain

Add `List<PipelineComponentSpec> LookupsInChain` (empty for every existing branch shape,
so this is additive). `PackageGenerator.GenerateMulticastFlow` reads it per branch.

### 3. Cache preloads — mostly already generic, needs multiplicity handled

`LookupCacheEmitter`/`LookupPreload`/the top-level `await {Cache}.LoadAsync<TKey>(...)`
bootstrap in `Program.cs` already work per-Lookup-component, not per-flow-shape — reuse
directly, once per DISTINCT Lookup component encountered anywhere in the package (a Lookup
appearing in more than one branch's chain, if that ever happens, must still get exactly one
cache/one preload — dedupe by the Lookup's own RefId, the same identity discipline used
everywhere else in this codebase).

### 4. Per-branch key/column resolution — the genuinely new work

This is where the real design effort is. For each branch with a non-empty `LookupsInChain`,
building that branch's own transform needs a resolution function conceptually like:

```
ResolveValue(columnName) =
    if columnName is copied from LookupsInChain[i]'s own reference table
        -> {cacheVar_i}[ResolveValue(LookupsInChain[i]'s own join input column)].{ReferenceColumn}
    else if columnName is a plain source-row property
        -> row.{columnName}
```

i.e. a RECURSIVE resolution, not a single-hop one — `LKP - DimSeller get seller_key`'s own key
selector must read `lkpProductsXOrderItemsCache[row.ProductId].SellerId`, not
`row.SellerId` (no such property exists on the raw source row at all). This is structurally the
same shape `TransformEmitter`'s existing `TranslateDataConversion`/Lookup-cross-reference
resolution already uses for ONE hop (Data Conversion → Conditional Split condition); it needs
generalizing to walk N hops through the `LookupsInChain` list, in order.

Concretely, likely a new small resolver (parallel to `TransformEmitter.LookupJoinSpec` but
usable per-column across an ordered list of Lookups) that, given a destination column name,
determines which Lookup (if any) in the chain produced it via `CopyFromReferenceColumn`, and
recursively resolves that Lookup's own join-input column the same way.

### 5. Miss handling across the WHOLE chain

`GenerateLookupThenAggregateFlow`'s existing `FilteringRowSource<TRow>` filters a row out
entirely when it misses the ONE Lookup in that flow. Generalize to: a row must be excluded
if it misses **any** Lookup in the chain — i.e. the filter predicate becomes a conjunction
across every `LookupsInChain` entry's own `.ContainsKey(...)` check, each keyed by that
Lookup's own (possibly Lookup-derived) join value, using the same recursive resolution as #4.

**Open question, not yet resolved by evidence**: does a genuine miss partway through this
real portfolio's own chains get silently dropped (as this design assumes, mirroring the
existing single-Lookup case), or does it matter that the row already went through the
Multicast's OTHER (reject) branch upstream of the second Lookup? Confirmed real: the reject
branch is a SIBLING Multicast output, not conditioned on a later Lookup's own outcome — so a
row can legitimately land in BOTH the reject sink AND (if it later misses a downstream Lookup)
be silently dropped from the main destination. This matches real SSIS behavior (a Lookup's own
No-Match output, when unrouted-elsewhere in a chain like this, simply drops those rows from
what reaches the final destination) but should be confirmed via a real `dtexec` probe before
shipping, per this project's own "measure, don't assume" discipline — the same rigor every
other Lookup-shape round in this codebase's history has used.

## Staged rollout

1. **Stage 1 — single Lookup in a Multicast branch's own chain, no further chaining.**
   Buildable and verifiable in isolation (a new synthetic fixture: Multicast → one Lookup →
   destination, alongside a reject branch). Does **not** close either real blocked package on
   its own — it's a genuine incremental step, not a false "done."
2. **Stage 2 — N chained Lookups**, generalizing #4/#5 above to walk the whole
   `LookupsInChain` list. This is what actually closes `DWH_DimProducts` (2-Lookup chain) and
   `DWH - FactOrders 3` (9-Lookup chain, and the only real portfolio-wide test that a
   genuinely long chain works, not just two).

## Verification plan

- A new dedicated fixture per stage, built via `Ssis.Extract.FixtureBuilder`, mirroring this
  project's own established discipline: real object-model construction, `dotnet build` against
  a copy of `Etl.Core` (0 warnings/0 errors), and an actual run against a real SQL Server,
  reading rows back rather than trusting the exit code.
- A real `dtexec` probe specifically for the miss-partway-through-a-chain question above,
  before assuming the drop-silently behavior is correct.
- Regenerate `DWH_DimProducts`/`DWH - FactOrders 3` from the real client portfolio and confirm:
  the Multicast→Lookup gap is gone, the whole regenerated solution builds 0 warnings/0 errors,
  and (data permitting) an actual run against seeded reference tables lands the expected
  dimension keys.
- Full existing test suite must stay green — this feature only ADDS a recognized branch shape
  and a new resolver; every existing single-Lookup/Aggregate/Multicast fixture must regenerate
  byte-identical.

## Not covered by this design (named, not silently dropped)

- A Lookup's own **No-Match** output routed to further downstream processing (rather than
  discarded or fed straight to a dead-end RowCount) — unevidenced in both real packages'
  actual No-Match routing (each just feeds a default-value Derived Column back into the SAME
  Union All convergence, which is itself already a recognized pass-through, so this may already
  fall out of the design above for FREE once Stage 1 is built — worth re-checking against real
  evidence once that stage exists, not assumed now).
- Conditional Split branches passing through a Lookup (unevidenced anywhere in this portfolio
  or prior ones; the Multicast-only gating mirrors the existing RowCount/Aggregate carve-outs'
  own reasoning).
- A Lookup whose own reference query itself depends on a value only available inside the
  chain (not evidenced; every real Lookup's own reference query is a plain, parameterless
  `SELECT` in both blocked packages).
