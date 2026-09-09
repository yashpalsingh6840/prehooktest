"""Left-side extraction: SQL-bearing items from raw .dtsx XML.

Structural markers only — see docs/dtsx-xml-notes.md for the empirical evidence behind every
shape handled here. No hardcoded sample-specific table/column/package names.
"""
from __future__ import annotations

import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path

DTS = "www.microsoft.com/SqlServer/Dts"
SQLTASK = "www.microsoft.com/sqlserver/dts/tasks/sqltask"


class SqlKind(str, Enum):
    STATEMENT = "Statement"
    TABLE_IDENTITY = "TableIdentity"
    UNVERIFIABLE = "Unverifiable"


@dataclass
class SqlItem:
    package: str
    executable_path: str
    object_name: str
    component_type: str
    source_property: str
    kind: SqlKind
    sql_text: str | None = None
    table_name: str | None = None
    schema_name: str | None = None
    connection_manager: str | None = None
    from_property_expression: bool = False
    evidence: str = ""


@dataclass
class ExtractionResult:
    sql_items: list[SqlItem] = field(default_factory=list)
    disabled_executables: list[str] = field(default_factory=list)
    unrecognized_component_types: list[tuple[str, str]] = field(default_factory=list)


# Pipeline component types confirmed (docs/dtsx-xml-notes.md §1.2) to never carry SQL text.
NO_SQL_COMPONENT_TYPES = {
    "Microsoft.FlatFileSource",
    "Microsoft.FlatFileDestination",
    "Microsoft.Multicast",
    "Microsoft.DerivedColumn",
    "Microsoft.ConditionalSplit",
    "Microsoft.DataConvert",
    "Microsoft.Sort",
    "Microsoft.Merge",
    "Microsoft.MergeJoin",
    "Microsoft.UnionAll",
    "Microsoft.RowCount",
    "Microsoft.Aggregate",
}


def _dts(tag: str) -> str:
    return f"{{{DTS}}}{tag}"


def _local_name(tag: str) -> str:
    return tag.split("}")[-1] if "}" in tag else tag


def _direct_children(elem: ET.Element, local_tag: str) -> list[ET.Element]:
    return [c for c in elem if _local_name(c.tag) == local_tag]


def _dts_attr(elem: ET.Element, name: str) -> str | None:
    return elem.attrib.get(f"{{{DTS}}}{name}")


def parse_dtsx(path: str | Path) -> ET.Element:
    tree = ET.parse(path)
    return tree.getroot()


def _build_connection_manager_index(root: ET.Element) -> dict[str, str]:
    """Maps both DTSID (with braces) and refId to the connection manager's ObjectName."""
    index: dict[str, str] = {}
    for cm_container in _direct_children(root, "ConnectionManagers"):
        for cm in _direct_children(cm_container, "ConnectionManager"):
            name = _dts_attr(cm, "ObjectName") or ""
            dtsid = _dts_attr(cm, "DTSID")
            ref_id = _dts_attr(cm, "refId") or cm.attrib.get("refId")
            if dtsid:
                index[dtsid] = name
            if ref_id:
                index[ref_id] = name
    return index


def _property_text(properties_elem: ET.Element | None, name: str) -> str | None:
    """Reads <property name="...">text</property> element text; None if property absent."""
    if properties_elem is None:
        return None
    for prop in _direct_children(properties_elem, "property"):
        if prop.attrib.get("name") == name:
            return prop.text or ""
    return None


def _find_properties(component: ET.Element) -> ET.Element | None:
    for child in _direct_children(component, "properties"):
        return child
    return None


def _parse_bracket_qualified(name: str) -> tuple[str | None, str | None]:
    """"[schema].[table]" -> (schema, table). Falls back gracefully on other shapes."""
    parts = [p.strip("[]") for p in name.split(".") if p.strip()]
    if len(parts) >= 2:
        return parts[-2], parts[-1]
    if len(parts) == 1:
        return None, parts[0]
    return None, None


def _parse_quote_qualified(name: str) -> tuple[str | None, str | None]:
    """'"schema"."table"' -> (schema, table)."""
    parts = [p.strip('"') for p in name.split(".") if p.strip()]
    if len(parts) >= 2:
        return parts[-2], parts[-1]
    if len(parts) == 1:
        return None, parts[0]
    return None, None


def _extract_execute_sql_task(
    executable: ET.Element, package: str, path: str, cm_index: dict[str, str]
) -> SqlItem | None:
    object_name = _dts_attr(executable, "ObjectName") or ""
    prop_expr_override = None
    for pe in _direct_children(executable, "PropertyExpression"):
        if _dts_attr(pe, "Name") == "SqlStatementSource":
            prop_expr_override = pe.text or ""
            break

    object_data = next(iter(_direct_children(executable, "ObjectData")), None)
    if object_data is None:
        return None
    sql_task_data = None
    for child in object_data:
        if _local_name(child.tag) == "SqlTaskData":
            sql_task_data = child
            break
    if sql_task_data is None:
        return None

    static_sql = sql_task_data.attrib.get(f"{{{SQLTASK}}}SqlStatementSource")
    connection_ref = sql_task_data.attrib.get(f"{{{SQLTASK}}}Connection")
    connection_name = cm_index.get(connection_ref, connection_ref) if connection_ref else None

    if prop_expr_override is not None:
        return SqlItem(
            package=package,
            executable_path=path,
            object_name=object_name,
            component_type="Microsoft.ExecuteSQLTask",
            source_property="DTS:PropertyExpression[SqlStatementSource]",
            kind=SqlKind.STATEMENT,
            sql_text=prop_expr_override,
            connection_manager=connection_name,
            from_property_expression=True,
            evidence=(
                f"PropertyExpression overrides static SqlStatementSource "
                f"({static_sql!r} at rest) with a runtime expression."
            ),
        )
    if static_sql is not None:
        return SqlItem(
            package=package,
            executable_path=path,
            object_name=object_name,
            component_type="Microsoft.ExecuteSQLTask",
            source_property="SQLTask:SqlStatementSource",
            kind=SqlKind.STATEMENT,
            sql_text=static_sql,
            connection_manager=connection_name,
        )
    return None


def _resolve_component_type(component: ET.Element) -> str:
    class_id = component.attrib.get("componentClassID", "")
    if class_id == "Microsoft.ManagedComponentHost":
        real_type = _property_text(_find_properties(component), "UserComponentTypeName")
        if real_type:
            return real_type
    return class_id


def _extract_oledb_source(component: ET.Element, ctx: dict) -> SqlItem | None:
    props = _find_properties(component)
    sql_command = (_property_text(props, "SqlCommand") or "").strip()
    open_rowset = (_property_text(props, "OpenRowset") or "").strip()
    open_rowset_var = (_property_text(props, "OpenRowsetVariable") or "").strip()
    if sql_command:
        return _statement_item(ctx, "property:SqlCommand", sql_command)
    if open_rowset:
        schema, table = _parse_bracket_qualified(open_rowset)
        return _table_item(ctx, "property:OpenRowset", schema, table)
    if open_rowset_var:
        return _unverifiable_item(
            ctx, "property:OpenRowsetVariable",
            f"Table name comes from variable {open_rowset_var!r}; not statically resolvable.",
        )
    return None


def _extract_oledb_destination(component: ET.Element, ctx: dict) -> SqlItem | None:
    # Same property shape as source (docs/dtsx-xml-notes.md §1.2).
    return _extract_oledb_source(component, ctx)


def _extract_ado_net_destination(component: ET.Element, ctx: dict) -> SqlItem | None:
    props = _find_properties(component)
    table_or_view = (_property_text(props, "TableOrViewName") or "").strip()
    if table_or_view:
        schema, table = _parse_quote_qualified(table_or_view)
        return _table_item(ctx, "property:TableOrViewName", schema, table)
    return _unverifiable_item(ctx, "property:TableOrViewName", "TableOrViewName is empty.")


def _extract_ado_net_source(component: ET.Element, ctx: dict) -> SqlItem | None:
    props = _find_properties(component)
    sql_command = (_property_text(props, "SqlCommand") or "").strip()
    if sql_command:
        return _statement_item(ctx, "property:SqlCommand", sql_command)
    table_or_view = (_property_text(props, "TableOrViewName") or "").strip()
    if table_or_view:
        schema, table = _parse_quote_qualified(table_or_view)
        return _table_item(ctx, "property:TableOrViewName", schema, table)
    return None


def _extract_oledb_command(component: ET.Element, ctx: dict) -> SqlItem | None:
    props = _find_properties(component)
    sql_command = (_property_text(props, "SqlCommand") or "").strip()
    if sql_command:
        item = _statement_item(ctx, "property:SqlCommand", sql_command)
        item.evidence = f"Parameterized per-row SQL; {sql_command.count('?')} positional '?' marker(s)."
        return item
    return None


def _extract_lookup(component: ET.Element, ctx: dict) -> SqlItem | None:
    props = _find_properties(component)
    sql_command = (_property_text(props, "SqlCommand") or "").strip()
    if sql_command:
        item = _statement_item(ctx, "property:SqlCommand", sql_command)
        item.evidence = (
            "Lookup reference query. Join key is declared separately via "
            "inputColumn/properties/JoinToReferenceColumn, not in this SQL text."
        )
        return item
    return None


def _extract_excel_source(component: ET.Element, ctx: dict) -> SqlItem | None:
    props = _find_properties(component)
    sql_command = (_property_text(props, "SqlCommand") or "").strip()
    if sql_command:
        return _statement_item(ctx, "property:SqlCommand", sql_command)
    # A bare worksheet name (OpenRowset, e.g. "Sheet1$") is not a SQL statement or a database
    # table identity — it's out of Gate 1's scope (SQL statement & schema parity). Worksheet
    # column-shape parity belongs to Gate 3 (per-component input/output schema).
    return None


def _extract_script_component(component: ET.Element, ctx: dict) -> SqlItem | None:
    return _unverifiable_item(
        ctx,
        "ScriptComponentHost:SourceCode/BinaryCode",
        "Script Component may contain SQL embedded in its source/binary blob; not extractable "
        "as text from the outer .dtsx XML (see docs/dtsx-xml-notes.md §1.3).",
    )


COMPONENT_SQL_HANDLERS = {
    "Microsoft.OLEDBSource": _extract_oledb_source,
    "Microsoft.OLEDBDestination": _extract_oledb_destination,
    "Microsoft.ADONETDestination": _extract_ado_net_destination,
    "Microsoft.DataReaderSourceAdapter": _extract_ado_net_source,
    "Microsoft.OLEDBCommand": _extract_oledb_command,
    "Microsoft.Lookup": _extract_lookup,
    "Microsoft.ExcelSource": _extract_excel_source,
    "Microsoft.ScriptComponentHost": _extract_script_component,
}


def _statement_item(ctx: dict, source_property: str, sql_text: str) -> SqlItem:
    return SqlItem(
        package=ctx["package"], executable_path=ctx["path"], object_name=ctx["object_name"],
        component_type=ctx["component_type"], source_property=source_property,
        kind=SqlKind.STATEMENT, sql_text=sql_text, connection_manager=ctx.get("connection_manager"),
    )


def _table_item(ctx: dict, source_property: str, schema: str | None, table: str | None) -> SqlItem:
    return SqlItem(
        package=ctx["package"], executable_path=ctx["path"], object_name=ctx["object_name"],
        component_type=ctx["component_type"], source_property=source_property,
        kind=SqlKind.TABLE_IDENTITY, schema_name=schema, table_name=table,
        connection_manager=ctx.get("connection_manager"),
    )


def _unverifiable_item(ctx: dict, source_property: str, evidence: str) -> SqlItem:
    return SqlItem(
        package=ctx["package"], executable_path=ctx["path"], object_name=ctx["object_name"],
        component_type=ctx["component_type"], source_property=source_property,
        kind=SqlKind.UNVERIFIABLE, connection_manager=ctx.get("connection_manager"), evidence=evidence,
    )


def _extract_pipeline(
    pipeline_executable: ET.Element, package: str, path: str, cm_index: dict[str, str], result: ExtractionResult
) -> None:
    object_data = next(iter(_direct_children(pipeline_executable, "ObjectData")), None)
    if object_data is None:
        return
    pipeline_elem = next((c for c in object_data if _local_name(c.tag) == "pipeline"), None)
    if pipeline_elem is None:
        return
    components_container = next(
        (c for c in pipeline_elem if _local_name(c.tag) == "components"), None
    )
    if components_container is None:
        return
    for component in components_container:
        if _local_name(component.tag) != "component":
            continue
        component_type = _resolve_component_type(component)
        component_name = component.attrib.get("name", "")
        component_path = f"{path}\\{component_name}"

        connection_name = None
        connections = next((c for c in component if _local_name(c.tag) == "connections"), None)
        if connections is not None:
            conn = next((c for c in connections if _local_name(c.tag) == "connection"), None)
            if conn is not None:
                ref = conn.attrib.get("connectionManagerRefId") or conn.attrib.get("connectionManagerID")
                connection_name = cm_index.get(ref, ref) if ref else None

        ctx = {
            "package": package,
            "path": component_path,
            "object_name": component_name,
            "component_type": component_type,
            "connection_manager": connection_name,
        }

        handler = COMPONENT_SQL_HANDLERS.get(component_type)
        if handler is not None:
            item = handler(component, ctx)
            if item is not None:
                result.sql_items.append(item)
        elif component_type not in NO_SQL_COMPONENT_TYPES:
            result.unrecognized_component_types.append((component_type, component_path))


def _process_executable(
    executable: ET.Element,
    package: str,
    path: str,
    inherited_disabled: bool,
    cm_index: dict[str, str],
    result: ExtractionResult,
) -> None:
    """`executable`: a <DTS:Executable> element. `path` is its own already-computed path."""
    disabled = inherited_disabled or (_dts_attr(executable, "Disabled") == "True")
    creation_name = _dts_attr(executable, "CreationName") or ""

    if disabled:
        result.disabled_executables.append(path)
        return  # per docs/dtsx-xml-notes.md: disabled executables generate no C# at all

    if creation_name == "Microsoft.ExecuteSQLTask":
        item = _extract_execute_sql_task(executable, package, path, cm_index)
        if item is not None:
            result.sql_items.append(item)
    elif creation_name == "Microsoft.Pipeline":
        _extract_pipeline(executable, package, path, cm_index, result)

    _process_children(executable, package, path, disabled, cm_index, result)


def _process_children(
    owner: ET.Element,
    package: str,
    owner_path: str,
    inherited_disabled: bool,
    cm_index: dict[str, str],
    result: ExtractionResult,
) -> None:
    """Recurses into `owner`'s <DTS:Executables> and <DTS:EventHandlers> wrapper children.

    `owner` may be the package root itself or any task/container DTS:Executable.
    """
    for executables_wrapper in _direct_children(owner, "Executables"):
        for child_executable in _direct_children(executables_wrapper, "Executable"):
            child_name = _dts_attr(child_executable, "ObjectName") or "?"
            child_path = f"{owner_path}\\{child_name}"
            _process_executable(
                child_executable, package, child_path, inherited_disabled, cm_index, result
            )

    for handlers_wrapper in _direct_children(owner, "EventHandlers"):
        for handler in _direct_children(handlers_wrapper, "EventHandler"):
            handler_name = _dts_attr(handler, "EventName") or _dts_attr(handler, "ObjectName") or "?"
            handler_path = f"{owner_path}.EventHandlers[{handler_name}]"
            for executables_wrapper in _direct_children(handler, "Executables"):
                for child_executable in _direct_children(executables_wrapper, "Executable"):
                    child_name = _dts_attr(child_executable, "ObjectName") or "?"
                    child_path = f"{handler_path}\\{child_name}"
                    _process_executable(
                        child_executable, package, child_path, inherited_disabled, cm_index, result
                    )


def extract(dtsx_path: str | Path) -> ExtractionResult:
    root = parse_dtsx(dtsx_path)
    package = _dts_attr(root, "ObjectName") or Path(dtsx_path).stem
    # The root's DTS:refId is always the literal "Package" regardless of DTS:ObjectName — real
    # SSIS convention (confirmed in docs/dtsx-graph-notes.md §1.1). Executable paths in evidence
    # should reflect this refId, not the package's own name.
    root_ref_id = root.attrib.get(f"{{{DTS}}}refId") or package
    cm_index = _build_connection_manager_index(root)
    result = ExtractionResult()

    _process_children(root, package, root_ref_id, False, cm_index, result)

    return result
