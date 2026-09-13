# AI review — Package_Advanced

Produced by hand-running `docs/ai-validation-prompt.md` against this package's evidence packet,
using only the packet's own content. Advisory only.

---

Component: OLECMD_SetFlag (Package_Advanced)
Verdict: AiConfirmedMatch
Rationale: Part 1 (is `known_correlate` genuinely this component's generated code?) — yes: Gate 1
already independently matched OLECMD_SetFlag's declared SQL text
("EXEC dbo.usp_SetCustomerFlag {0}, N'flagged'") to the `OleDbCommandStep<DFT_FlagCustomersSqlRow>`
construction site in `Package_Advanced.cs` by SQL content, not name — an established correlate,
not a guess. Part 2 (does it correctly implement `declared_columns`?) — yes: OLECMD_SetFlag
declares exactly one parameter, `@CustomerID` (i4), and the generated code's parameter-binding
lambda is `row => new object?[] { row.CustomerID }` — one positional parameter, matching both in
count and in identity (the declared parameter name and the bound row property agree), fed into
the same SQL text Gate 1 already confirmed matches. No discrepancy found.
