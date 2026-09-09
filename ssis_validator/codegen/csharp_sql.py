"""Right-side extraction: SQL-bearing items from generated C# source.

Structural conventions confirmed across multiple real generated files — see
docs/dtsx-xml-notes.md §2. We scan every .cs file's text (never trusting folder/file names as
content indicators, per the observed "Sql/ folder with zero SQL text" trap) and apply each known
generated-code shape's regex. Text/regex extraction only, per the plan's tech-stack decision —
escalate to tree-sitter only if this proves too irregular (not yet needed on these samples).
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path

from ssis_validator.dtsx.sql_extraction import SqlKind

_SKIP_DIR_NAMES = {"bin", "obj", "TestResults"}


@dataclass
class CodegenSqlItem:
    file_path: str
    object_name: str
    shape: str
    kind: SqlKind
    sql_text: str | None = None
    literal_segments: list[str] | None = None
    table_name: str | None = None
    schema_name: str | None = None
    evidence: str = ""


@dataclass
class CodegenExtractionResult:
    sql_items: list[CodegenSqlItem] = field(default_factory=list)


_CS_STRING_LITERAL = r'"(?:\\.|[^"\\])*"'


def _unescape_cs_string(literal: str) -> str:
    """literal includes surrounding quotes, e.g. '"a\\nb"'."""
    inner = literal[1:-1]
    return (
        inner.replace('\\r\\n', '\n').replace('\\n', '\n').replace('\\r', '\n')
        .replace('\\t', '\t').replace('\\"', '"').replace('\\\\', '\\')
    )


def _extract_literal_segments(expr_text: str) -> list[str]:
    """Pulls every double-quoted C# string literal out of an expression, in order."""
    return [_unescape_cs_string(m.group(0)) for m in re.finditer(_CS_STRING_LITERAL, expr_text)]


_STATEMENT_CLASS_RE = re.compile(
    r"public\s+static\s+class\s+(?P<cls>\w+)Statement\b.*?"
    r"public\s+static\s+string\s+BuildStatement\s*\([^)]*\)\s*=>\s*(?P<expr>.*?);\s*\}",
    re.DOTALL,
)


_WHOLE_LITERAL_RE = re.compile(r"^\s*" + _CS_STRING_LITERAL + r"\s*$")


def _scan_statement_classes(text: str, file_path: str) -> list[CodegenSqlItem]:
    items: list[CodegenSqlItem] = []
    for m in _STATEMENT_CLASS_RE.finditer(text):
        object_name = m.group("cls")
        expr = m.group("expr")
        segments = _extract_literal_segments(expr)
        if len(segments) == 1 and _WHOLE_LITERAL_RE.match(expr):
            items.append(CodegenSqlItem(
                file_path=file_path, object_name=object_name, shape="ExecuteSqlStatement",
                kind=SqlKind.STATEMENT, sql_text=segments[0],
            ))
        else:
            items.append(CodegenSqlItem(
                file_path=file_path, object_name=object_name, shape="ExecuteSqlStatement",
                kind=SqlKind.STATEMENT, literal_segments=segments,
                evidence="Expression-driven BuildStatement(); compared as an ordered literal-segment template.",
            ))
    return items


_SQL_ROW_SOURCE_RE = re.compile(
    r'new\s+SqlRowSource<[\w<>,\s]+>\(\s*"(?P<name>[^"]+)"\s*,\s*new\s+SqlSourceOptions\s*\{'
    r'.*?CommandText\s*=\s*(?P<sql>' + _CS_STRING_LITERAL + r')',
    re.DOTALL,
)


def _scan_inline_sql_row_source(text: str, file_path: str) -> list[CodegenSqlItem]:
    items = []
    for m in _SQL_ROW_SOURCE_RE.finditer(text):
        items.append(CodegenSqlItem(
            file_path=file_path, object_name=m.group("name"), shape="InlineSqlRowSource",
            kind=SqlKind.STATEMENT, sql_text=_unescape_cs_string(m.group("sql")),
        ))
    return items


_OLEDB_COMMAND_STEP_RE = re.compile(
    r'new\s+OleDbCommandStep<[\w<>,\s]+>\(\s*"(?P<name>[^"]+)"\s*,\s*\w+\s*,\s*'
    r'(?P<sql>' + _CS_STRING_LITERAL + r')',
    re.DOTALL,
)


def _scan_oledb_command_step(text: str, file_path: str) -> list[CodegenSqlItem]:
    items = []
    for m in _OLEDB_COMMAND_STEP_RE.finditer(text):
        sql = _unescape_cs_string(m.group("sql"))
        items.append(CodegenSqlItem(
            file_path=file_path, object_name=m.group("name"), shape="OleDbCommandStep",
            kind=SqlKind.STATEMENT, sql_text=sql,
            evidence=f"Composite-format string; {sql.count('{')} positional placeholder(s).",
        ))
    return items


_MODEL_BUILDER_ENTITY_RE = re.compile(
    r"modelBuilder\.Entity<(?P<entity>\w+)>\(entity\s*=>\s*\{(?P<body>.*?)\n\s*\}\);",
    re.DOTALL,
)
_TO_TABLE_RE = re.compile(r'entity\.ToTable\(\s*"(?P<table>[^"]+)"\s*(?:,\s*"(?P<schema>[^"]+)")?\s*\)')


def _scan_ef_core_tables(text: str, file_path: str) -> list[CodegenSqlItem]:
    items = []
    for entity_m in _MODEL_BUILDER_ENTITY_RE.finditer(text):
        to_table_m = _TO_TABLE_RE.search(entity_m.group("body"))
        if to_table_m is None:
            continue
        items.append(CodegenSqlItem(
            file_path=file_path, object_name=entity_m.group("entity"), shape="EfCoreTable",
            kind=SqlKind.TABLE_IDENTITY,
            table_name=to_table_m.group("table"), schema_name=to_table_m.group("schema"),
        ))
    return items


_LOOKUP_CACHE_RE = re.compile(
    r"public\s+static\s+class\s+(?P<cls>\w+Cache)\b.*?"
    r"command\.CommandText\s*=\s*(?P<sql>" + _CS_STRING_LITERAL + r")",
    re.DOTALL,
)


def _scan_lookup_cache(text: str, file_path: str) -> list[CodegenSqlItem]:
    items = []
    for m in _LOOKUP_CACHE_RE.finditer(text):
        cls = m.group("cls")
        object_name = cls[: -len("Cache")] if cls.endswith("Cache") else cls
        items.append(CodegenSqlItem(
            file_path=file_path, object_name=object_name, shape="LookupCache",
            kind=SqlKind.STATEMENT, sql_text=_unescape_cs_string(m.group("sql")),
            evidence=(
                f"Generated class name '{cls}' matched to SSIS component '{object_name}' by "
                "stripping the 'Cache' suffix the generator appended (not present in the .dtsx)."
            ),
        ))
    return items


_ERROR_HANDLER_SQL_RE = re.compile(
    r'ExecuteSqlWithoutTransactionAsync\(\s*(?P<sql>' + _CS_STRING_LITERAL + r')\s*,\s*'
    r'CancellationToken\.None\);\s*handlersRun\.Add\(\s*"(?P<name>[^"]+)"\s*\)',
    re.DOTALL,
)


def _scan_error_handler_sql(text: str, file_path: str) -> list[CodegenSqlItem]:
    items = []
    for m in _ERROR_HANDLER_SQL_RE.finditer(text):
        items.append(CodegenSqlItem(
            file_path=file_path, object_name=m.group("name"), shape="ErrorHandlerSql",
            kind=SqlKind.STATEMENT, sql_text=_unescape_cs_string(m.group("sql")),
            evidence="Failure-handler Execute SQL Task, inlined as an ExecuteSqlWithoutTransactionAsync call.",
        ))
    return items


_SCANNERS = (
    _scan_statement_classes,
    _scan_inline_sql_row_source,
    _scan_oledb_command_step,
    _scan_ef_core_tables,
    _scan_lookup_cache,
    _scan_error_handler_sql,
)


def extract(generated_package_dir: str | Path) -> CodegenExtractionResult:
    root = Path(generated_package_dir)
    result = CodegenExtractionResult()
    for cs_file in root.rglob("*.cs"):
        if any(part in _SKIP_DIR_NAMES for part in cs_file.parts):
            continue
        text = cs_file.read_text(encoding="utf-8-sig", errors="replace")
        rel_path = str(cs_file.relative_to(root))
        for scanner in _SCANNERS:
            result.sql_items.extend(scanner(text, rel_path))
    return result
