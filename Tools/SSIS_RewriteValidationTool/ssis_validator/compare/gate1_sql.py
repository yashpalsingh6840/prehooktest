"""Gate 1 — SQL statement & schema parity.

Matches every left-side (.dtsx) SQL-bearing item to a right-side (generated C#) item by object
name, then compares them structurally:
- STATEMENT vs STATEMENT: parsed/normalized via sqlglot (falls back to whitespace-normalized text
  if either side fails to parse, never silently).
- Expression-driven statements (literal segment templates) are compared segment-by-segment.
- TABLE_IDENTITY vs TABLE_IDENTITY: schema+table name comparison (case-insensitive, since SQL
  Server identifiers are case-insensitive by default collation).
- Anything left unmatched, on either side, is a finding — never a silent drop.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum

import sqlglot

from ssis_validator.codegen.csharp_sql import CodegenSqlItem
from ssis_validator.dtsx.sql_extraction import SqlItem, SqlKind

SQL_DIALECT = "tsql"


class Verdict(str, Enum):
    MATCH = "Match"
    NORMALIZED_MATCH = "NormalizedMatch"
    MISMATCH = "Mismatch"
    LEFT_ONLY = "LeftOnly"
    RIGHT_ONLY = "RightOnly"
    UNRECOGNIZED = "Unrecognized"
    UNVERIFIABLE = "Unverifiable"


@dataclass
class Finding:
    object_name: str
    verdict: Verdict
    left: SqlItem | None
    right: CodegenSqlItem | None
    detail: str


@dataclass
class Gate1Result:
    findings: list[Finding] = field(default_factory=list)
    disabled_left_only_excluded: list[str] = field(default_factory=list)
    unrecognized_left_components: list[tuple[str, str]] = field(default_factory=list)


def _normalize_positional_params(text: str) -> str:
    """SSIS's positional '?' parameter marker becomes .NET composite-format '{0}', '{1}', ...
    in generated OLEDBCommand code (docs/dtsx-xml-notes.md §2). Rewrites '?' -> sequential '{n}'
    so both sides can be compared after this specific, confirmed translation."""
    out, i = [], 0
    for ch in text:
        if ch == "?":
            out.append(f"{{{i}}}")
            i += 1
        else:
            out.append(ch)
    return "".join(out)


def _normalize_sql_text(text: str) -> str:
    try:
        parsed = sqlglot.parse_one(text, read=SQL_DIALECT)
        return parsed.sql(dialect=SQL_DIALECT, normalize=True, pretty=False).strip().lower()
    except Exception:
        return " ".join(text.split()).strip().lower()


def _compare_statements(left: SqlItem, right: CodegenSqlItem) -> tuple[Verdict, str]:
    if not left.from_property_expression and left.sql_text is not None and right.sql_text is not None:
        if left.sql_text.strip() == right.sql_text.strip():
            return Verdict.MATCH, "Exact text match."
        left_norm = _normalize_sql_text(left.sql_text)
        right_norm = _normalize_sql_text(right.sql_text)
        if left_norm == right_norm:
            return Verdict.NORMALIZED_MATCH, "Structurally equivalent after SQL normalization."
        if "?" in left.sql_text:
            left_placeholders = _normalize_positional_params(left.sql_text)
            if left_placeholders.strip() == right.sql_text.strip():
                return Verdict.NORMALIZED_MATCH, (
                    "Match after translating SSIS's positional '?' marker(s) to .NET composite-"
                    "format '{n}' placeholder(s), the confirmed OLEDBCommand generation pattern."
                )
        return Verdict.MISMATCH, (
            f"SQL differs.\nLEFT  ({left.source_property}): {left.sql_text!r}\n"
            f"RIGHT ({right.shape}): {right.sql_text!r}"
        )

    # Expression-driven statement (a PropertyExpression / parameterized BuildStatement): compare
    # as an ordered list of literal segments rather than one opaque string, since the
    # runtime-varying part (an SSIS variable reference on the left, a method parameter on the
    # right) can never textually match.
    left_segments = _left_segments(left) if left.from_property_expression else (
        [left.sql_text] if left.sql_text is not None else None
    )
    right_segments = right.literal_segments if right.literal_segments is not None else (
        [right.sql_text] if right.sql_text is not None else None
    )
    if left_segments is not None and right_segments is not None:
        left_norm = [" ".join(s.split()) for s in left_segments]
        right_norm = [" ".join(s.split()) for s in right_segments]
        if left_norm == right_norm:
            return Verdict.NORMALIZED_MATCH, "Literal-segment template match (expression-driven statement)."
        return Verdict.MISMATCH, (
            f"Literal segments differ.\nLEFT: {left_segments!r}\nRIGHT: {right_segments!r}"
        )

    return Verdict.UNVERIFIABLE, "Could not extract comparable SQL text from one or both sides."


def _left_segments(left: SqlItem) -> list[str] | None:
    """SSIS expression text (e.g. `"a" + @[User::X] + "b"`) split into its literal segments."""
    if left.sql_text is None:
        return None
    import re
    segments = re.findall(r'"((?:[^"\\]|\\.)*)"', left.sql_text)
    return segments if segments else None


def _compare_table_identity(left: SqlItem, right: CodegenSqlItem) -> tuple[Verdict, str]:
    left_table = (left.table_name or "").lower()
    right_table = (right.table_name or "").lower()
    left_schema = (left.schema_name or "").lower() or None
    right_schema = (right.schema_name or "").lower() or None

    if not left_table or not right_table:
        return Verdict.UNVERIFIABLE, "Table name missing on one side."
    if left_table == right_table and (left_schema is None or right_schema is None or left_schema == right_schema):
        return Verdict.MATCH, f"Table identity matches ({left.table_name}/{right.table_name})."
    return Verdict.MISMATCH, (
        f"Table identity differs.\nLEFT: schema={left.schema_name!r} table={left.table_name!r}\n"
        f"RIGHT: schema={right.schema_name!r} table={right.table_name!r}"
    )


def _find_table_identity_candidate(
    left: SqlItem, unmatched_right: list[CodegenSqlItem]
) -> CodegenSqlItem | None:
    """Table-identity items are correlated by (schema, table), not by name — the generator names
    its EF Core POCO/entity after the bare table, not the SSIS destination component (see
    docs/dtsx-xml-notes.md §2)."""
    left_table = (left.table_name or "").lower()
    for r in unmatched_right:
        if r.kind == SqlKind.TABLE_IDENTITY and (r.table_name or "").lower() == left_table:
            return r
    return None


def run_gate1(left_items: list[SqlItem], right_items: list[CodegenSqlItem]) -> Gate1Result:
    result = Gate1Result()
    unmatched_right = list(right_items)

    for left in left_items:
        if left.kind == SqlKind.UNVERIFIABLE:
            result.findings.append(Finding(
                object_name=left.object_name, verdict=Verdict.UNVERIFIABLE,
                left=left, right=None, detail=left.evidence,
            ))
            continue

        if left.kind == SqlKind.TABLE_IDENTITY:
            match = _find_table_identity_candidate(left, unmatched_right)
            candidates = [match] if match is not None else []
        else:
            candidates = [
                r for r in unmatched_right
                if r.object_name == left.object_name and r.kind != SqlKind.TABLE_IDENTITY
            ]
        if not candidates:
            result.findings.append(Finding(
                object_name=left.object_name, verdict=Verdict.LEFT_ONLY,
                left=left, right=None,
                detail=f"No generated artifact found matching object name {left.object_name!r}.",
            ))
            continue

        right = candidates[0]
        unmatched_right.remove(right)

        if left.kind == SqlKind.STATEMENT and right.kind == SqlKind.STATEMENT:
            verdict, detail = _compare_statements(left, right)
        elif left.kind == SqlKind.TABLE_IDENTITY and right.kind == SqlKind.TABLE_IDENTITY:
            verdict, detail = _compare_table_identity(left, right)
        else:
            verdict, detail = Verdict.MISMATCH, (
                f"Left is {left.kind.value} ({left.source_property}) but right is "
                f"{right.kind.value} ({right.shape}) — structurally different representations."
            )
        result.findings.append(Finding(
            object_name=left.object_name, verdict=verdict, left=left, right=right, detail=detail,
        ))

    _apply_sql_text_fallback(result, unmatched_right)

    for right in unmatched_right:
        result.findings.append(Finding(
            object_name=right.object_name, verdict=Verdict.RIGHT_ONLY,
            left=None, right=right,
            detail=f"Generated artifact {right.object_name!r} ({right.shape}) has no corresponding left-side SQL item.",
        ))

    return result


def _apply_sql_text_fallback(result: Gate1Result, unmatched_right: list[CodegenSqlItem]) -> None:
    """Some generated shapes name their artifact after a different SSIS object than the one that
    carries the SQL (observed: OLEDBCommand's OleDbCommandStep is keyed by the owning pipeline's
    name, not the component's — docs/dtsx-xml-notes.md §2). For any LeftOnly STATEMENT finding
    left over from name-based matching, retry by exact SQL text against the remaining unmatched
    right-side STATEMENT items — only when exactly one candidate matches, so an ambiguous case is
    still reported as Unmatched rather than guessed.
    """
    left_only_statement_findings = [
        f for f in result.findings
        if f.verdict == Verdict.LEFT_ONLY and f.left is not None and f.left.kind == SqlKind.STATEMENT
    ]
    for finding in left_only_statement_findings:
        left = finding.left
        if left.sql_text is None:
            continue
        left_variants = {left.sql_text.strip(), _normalize_positional_params(left.sql_text).strip()}
        candidates = [
            r for r in unmatched_right
            if r.kind == SqlKind.STATEMENT and r.sql_text is not None
            and r.sql_text.strip() in left_variants
        ]
        if len(candidates) != 1:
            continue
        right = candidates[0]
        unmatched_right.remove(right)
        finding.right = right
        finding.verdict = Verdict.NORMALIZED_MATCH
        finding.detail = (
            f"SQL text match, correlated by content rather than name: left object "
            f"{left.object_name!r} vs. generated artifact {right.object_name!r} ({right.shape})."
        )
