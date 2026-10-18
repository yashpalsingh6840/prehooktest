# Final validation summary — Package

## Tool-Verified (22)

- [Gate1] **OLEDST_SalesSummary** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate1] **OLESRC_OrdersAndDetails** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate1] **OLEDST_StagingCustomers** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate1] **OLEDST_StagingErrors** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate1] **SQL_LogLoadFailure** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate1] **SQL_TruncateTargets** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `ControlFlow` **SQL_TruncateTargets** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `ControlFlow` **SCR_ValidateAndLogStart** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `ControlFlow` **DFT_LoadCustomers** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `ControlFlow` **SCR_NotifyAndLogProgress** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `ControlFlow` **DFT_BuildSalesSummary** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `FailureHandler` **SQL_LogLoadFailure** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_BuildSalesSummary` **OLEDST_SalesSummary** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_BuildSalesSummary` **OLESRC_OrdersAndDetails** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_LoadCustomers` **FF_SRC_Customers** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_LoadCustomers` **OLEDST_StagingCustomers** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate2] `DataFlow:DFT_LoadCustomers` **OLEDST_StagingErrors** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `DestinationInput` **OLEDST_SalesSummary** — Tier1: NormalizedMatch — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `SourceOutput` **OLESRC_OrdersAndDetails** — Tier1: NormalizedMatch — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `SourceOutput` **FF_SRC_Customers** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `DestinationInput` **OLEDST_StagingCustomers** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.
- [Gate3] `DestinationInput` **OLEDST_StagingErrors** — Tier1: Match — Tier 1 deterministic match — no AI involvement needed.

## Exception (0)

(none)

## AI-Verified (0)

(none)

## Human-Needs-Review (6)

- [Gate1] **SCR_ComputeCustomerTier** — Tier1: Unverifiable — Tier2: AiSuspectedDiscrepancy — Tier 2 returned AiSuspectedDiscrepancy — needs human judgment, not a clean resolution.
- [Gate1] **SCR_CleanseCustomerRow** — Tier1: Unverifiable — Tier2: AiSuspectedDiscrepancy — Tier 2 returned AiSuspectedDiscrepancy — needs human judgment, not a clean resolution.
- [Gate2] `DataFlow:DFT_BuildSalesSummary` **SCR_ComputeCustomerTier** — Tier1: Unverifiable — Tier2: AiSuspectedDiscrepancy — Tier 2 returned AiSuspectedDiscrepancy — needs human judgment, not a clean resolution.
- [Gate2] `DataFlow:DFT_LoadCustomers` **SCR_CleanseCustomerRow** — Tier1: Unverifiable — Tier2: AiSuspectedDiscrepancy — Tier 2 returned AiSuspectedDiscrepancy — needs human judgment, not a clean resolution.
- [Gate3] `InternalTransform` **SCR_ComputeCustomerTier** — Tier1: Unverifiable — Tier2: AiSuspectedDiscrepancy — Tier 2 returned AiSuspectedDiscrepancy — needs human judgment, not a clean resolution.
- [Gate3] `InternalTransform` **SCR_CleanseCustomerRow** — Tier1: Unverifiable — Tier2: AiSuspectedDiscrepancy — Tier 2 returned AiSuspectedDiscrepancy — needs human judgment, not a clean resolution.
