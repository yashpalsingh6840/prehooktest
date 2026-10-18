"""Left-side extraction: readable semantic evidence for pipeline-internal transforms that Gate 3
can only report as `Unverifiable` (no declared external column shape to compare) — see
compare/gate3_schema.py. This module never produces a verdict; it only resolves what's already
present in the .dtsx as human-readable text/structure, so a surviving Unverifiable finding carries
a self-contained evidence packet instead of forcing a re-read of the raw XML.

Confirmed by direct inspection of Package_Transforms.dtsx and Package.dtsx (2026-09-12):

- DerivedColumn/ConditionalSplit already carry a `FriendlyExpression` property (column-name form)
  alongside the tokenized `Expression` (lineage-ID form) — no expression parsing needed, it's
  already the human-readable text.
- Every lineageId / `#{...}` reference / `InputColumnID` / `AggregationColumnId` property embeds
  its own owning component and column name in its path (path-separated:
  Component.Outputs[Port].Columns[Column], or .Inputs[...] on the input side) — a single regex
  parse resolves it, no separate cross-reference index needed.
- Lookup's cache mode / connection type / no-match behavior are `System.Int32` properties with a
  `typeConverter` attribute naming a UI-only enum whose exact integer mapping Microsoft does not
  publish (confirmed by direct search of Microsoft Learn's Lookup Transformation page and the
  MS-DTSX2 open specification on 2026-09-12 — both describe the *options* in prose, never the
  persisted integer each maps to). Rather than guess a mapping (this project's "never assume"
  rule), these are surfaced as `RawEnumProperty` — the raw integer plus the property's own
  `description` attribute, which is authoritative because SSIS itself writes that exact text into
  every real .dtsx file. The same applies to Aggregate's `AggregationType` and MergeJoin's
  `JoinType`.
- The Sort transformation is the one property in this module where Microsoft's public
  documentation IS explicit about the encoding (Sort Transformation page, confirmed 2026-09-12):
  "A positive number denotes that the sort is ascending, and a negative number denotes that the
  sort is descending. Columns that are not sorted have a sort order of 0." `NewSortKeyPosition`
  is decoded on that documented basis, not a guess.
- A Script Component's (`Microsoft.ManagedComponentHost` / `Microsoft.ScriptComponentHost`)
  `SourceCode` property is an array of (filename, encoding, content) CDATA triplets. Confirmed
  across every sample: the one file carrying real business logic is always named exactly
  `main.cs` or `main.vb` (VSTA's fixed project template); every other file
  (ComponentWrapper.cs, BufferWrapper.cs, files under Properties/, the *.csproj/*.vbproj, etc.)
  is generated wrapper/designer boilerplate. A Script Task's `<ScriptProject>` uses a different,
  equally fixed convention: its real entry point is always named exactly `ScriptMain.cs` or
  `ScriptMain.vb`; every other `<ProjectItem>` (files under My Project/ or Properties/, the
  project file itself) is boilerplate. Both extractors use an inclusion list (the one confirmed-real filename
  pattern), not an exclusion list, so an unrecognized shape degrades to "no source file found"
  rather than silently keeping the wrong file.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path

from ssis_validator.dtsx.sql_extraction import (
    _direct_children,
    _dts_attr,
    _find_properties,
    _local_name,
    _property_text,
    parse_dtsx,
)

_LINEAGE_RE = re.compile(
    r"\\(?P<component>[^\\]+)\.(?:Inputs|Outputs)\[[^\]]+\]\.Columns\[(?P<column>[^\]]+)\]$"
)

_SCRIPT_COMPONENT_REAL_FILES = {"main.cs", "main.vb"}
_SCRIPT_TASK_REAL_FILES = {"scriptmain.cs", "scriptmain.vb"}


def resolve_lineage(ref: str | None) -> tuple[str, str] | None:
    """"...\\Comp.Outputs[Port].Columns[Col]" -> ("Comp", "Col"), whether `ref` is a bare lineageId
    or a `#{...}`-wrapped reference (AggregationColumnId/InputColumnID's own form). Returns None
    for an empty/unresolvable reference -- callers must treat that as "source not determinable",
    never fabricate a name."""
    if not ref:
        return None
    ref = ref.strip()
    if ref.startswith("#{") and ref.endswith("}"):
        ref = ref[2:-1]
    m = _LINEAGE_RE.search(ref)
    if not m:
        return None
    return m.group("component"), m.group("column")


@dataclass
class RawEnumProperty:
    """A `typeConverter`-annotated int property whose exact integer->label mapping Microsoft does
    not publish (see module docstring) -- the raw value and the property's own authoritative
    `description` text, undecoded."""
    property_name: str
    raw_value: str
    description: str


@dataclass
class ExpressionColumn:
    output_name: str
    friendly_expression: str


@dataclass
class DerivedColumnSemantics:
    columns: list[ExpressionColumn] = field(default_factory=list)


@dataclass
class ConditionalSplitOutput:
    output_name: str
    is_default: bool
    friendly_expression: str | None = None
    evaluation_order: int | None = None


@dataclass
class ConditionalSplitSemantics:
    outputs: list[ConditionalSplitOutput] = field(default_factory=list)


@dataclass
class LookupJoinColumn:
    input_column: str
    join_to_reference_column: str | None


@dataclass
class LookupOutputColumn:
    name: str
    copy_from_reference_column: str | None


@dataclass
class LookupSemantics:
    sql_command: str | None
    connection_type: RawEnumProperty | None
    cache_type: RawEnumProperty | None
    no_match_behavior: RawEnumProperty | None
    join_columns: list[LookupJoinColumn] = field(default_factory=list)
    output_columns: list[LookupOutputColumn] = field(default_factory=list)
    match_output_destination: str | None = None
    no_match_output_destination: str | None = None


@dataclass
class AggregateOutputColumn:
    name: str
    aggregation_type: RawEnumProperty
    source_component: str | None
    source_column: str | None


@dataclass
class AggregateSemantics:
    columns: list[AggregateOutputColumn] = field(default_factory=list)


@dataclass
class MergeJoinInputColumn:
    name: str
    side: str  # "left" | "right"
    sort_key_position: int | None


@dataclass
class MergeJoinOutputColumn:
    name: str
    source_column: str | None


@dataclass
class MergeJoinSemantics:
    join_type: RawEnumProperty
    num_key_columns: int | None
    treat_nulls_as_equal: bool | None
    input_columns: list[MergeJoinInputColumn] = field(default_factory=list)
    output_columns: list[MergeJoinOutputColumn] = field(default_factory=list)


@dataclass
class SortColumn:
    name: str
    sort_key_position: int
    direction: str  # "ascending" | "descending" | "not-sorted" -- decoded per Microsoft's
                     # documented sign convention (Sort Transformation page, confirmed 2026-09-12)


@dataclass
class SortSemantics:
    columns: list[SortColumn] = field(default_factory=list)


@dataclass
class ScriptSourceFile:
    file_name: str
    source_code: str


@dataclass
class ScriptSemantics:
    files: list[ScriptSourceFile] = field(default_factory=list)


def _raw_enum_property(properties_elem, name: str) -> RawEnumProperty | None:
    if properties_elem is None:
        return None
    for prop in _direct_children(properties_elem, "property"):
        if prop.attrib.get("name") == name:
            return RawEnumProperty(
                property_name=name,
                raw_value=(prop.text or "").strip(),
                description=prop.attrib.get("description", ""),
            )
    return None


def _first_input(component_elem):
    inputs_container = next(iter(_direct_children(component_elem, "inputs")), None)
    if inputs_container is None:
        return None
    return next(iter(_direct_children(inputs_container, "input")), None)


def _outputs(component_elem):
    outputs_container = next(iter(_direct_children(component_elem, "outputs")), None)
    if outputs_container is None:
        return []
    return _direct_children(outputs_container, "output")


def extract_derived_column(component_elem) -> DerivedColumnSemantics:
    result = DerivedColumnSemantics()
    for output in _outputs(component_elem):
        if output.attrib.get("isErrorOut") == "true":
            continue
        output_columns = next(iter(_direct_children(output, "outputColumns")), None)
        if output_columns is None:
            continue
        for col in _direct_children(output_columns, "outputColumn"):
            props = _find_properties(col)
            friendly = _property_text(props, "FriendlyExpression")
            if friendly is not None:
                result.columns.append(ExpressionColumn(
                    output_name=col.attrib.get("name", ""), friendly_expression=friendly,
                ))
    return result


def extract_conditional_split(component_elem) -> ConditionalSplitSemantics:
    result = ConditionalSplitSemantics()
    for output in _outputs(component_elem):
        if output.attrib.get("isErrorOut") == "true":
            continue
        props = _find_properties(output)
        is_default = (_property_text(props, "IsDefaultOut") or "").strip().lower() == "true"
        friendly = _property_text(props, "FriendlyExpression")
        order_text = _property_text(props, "EvaluationOrder")
        result.outputs.append(ConditionalSplitOutput(
            output_name=output.attrib.get("name", ""),
            is_default=is_default,
            friendly_expression=friendly,
            evaluation_order=int(order_text) if order_text and order_text.strip("-").isdigit() else None,
        ))
    return result


def extract_lookup(component_elem, paths=()) -> LookupSemantics:
    """`paths` is the owning pipeline's list of dtsx.data_flow.DataFlowPath (or any object with
    matching start_component/start_port/end_component attributes) -- used to resolve where the
    match/no-match outputs actually go, so the "no-match rows silently dropped" question is
    checkable evidence instead of an assumption."""
    props = _find_properties(component_elem)
    sql_command = (_property_text(props, "SqlCommand") or "").strip() or None
    comp_name = component_elem.attrib.get("name", "")

    result = LookupSemantics(
        sql_command=sql_command,
        connection_type=_raw_enum_property(props, "ConnectionType"),
        cache_type=_raw_enum_property(props, "CacheType"),
        no_match_behavior=_raw_enum_property(props, "NoMatchBehavior"),
    )

    input_elem = _first_input(component_elem)
    if input_elem is not None:
        input_columns = next(iter(_direct_children(input_elem, "inputColumns")), None)
        if input_columns is not None:
            for col in _direct_children(input_columns, "inputColumn"):
                col_props = _find_properties(col)
                result.join_columns.append(LookupJoinColumn(
                    input_column=col.attrib.get("cachedName", ""),
                    join_to_reference_column=_property_text(col_props, "JoinToReferenceColumn"),
                ))

    for output in _outputs(component_elem):
        if output.attrib.get("isErrorOut") == "true":
            continue
        output_name = output.attrib.get("name", "")
        output_columns = next(iter(_direct_children(output, "outputColumns")), None)
        if output_columns is not None:
            for col in _direct_children(output_columns, "outputColumn"):
                col_props = _find_properties(col)
                result.output_columns.append(LookupOutputColumn(
                    name=col.attrib.get("name", ""),
                    copy_from_reference_column=_property_text(col_props, "CopyFromReferenceColumn"),
                ))
        destination = next(
            (p.end_component for p in paths
             if p.start_component == comp_name and p.start_port == output_name),
            None,
        )
        if output_name == "Lookup Match Output":
            result.match_output_destination = destination
        elif output_name == "Lookup No Match Output":
            result.no_match_output_destination = destination

    return result


def extract_aggregate(component_elem) -> AggregateSemantics:
    result = AggregateSemantics()
    for output in _outputs(component_elem):
        output_columns = next(iter(_direct_children(output, "outputColumns")), None)
        if output_columns is None:
            continue
        for col in _direct_children(output_columns, "outputColumn"):
            col_props = _find_properties(col)
            agg_type = _raw_enum_property(col_props, "AggregationType")
            if agg_type is None:
                continue
            source = resolve_lineage(_property_text(col_props, "AggregationColumnId"))
            result.columns.append(AggregateOutputColumn(
                name=col.attrib.get("name", ""),
                aggregation_type=agg_type,
                source_component=source[0] if source else None,
                source_column=source[1] if source else None,
            ))
    return result


def extract_merge_join(component_elem) -> MergeJoinSemantics:
    props = _find_properties(component_elem)
    num_key_text = _property_text(props, "NumKeyColumns")
    treat_nulls_text = _property_text(props, "TreatNullsAsEqual")

    result = MergeJoinSemantics(
        join_type=_raw_enum_property(props, "JoinType"),
        num_key_columns=int(num_key_text) if num_key_text and num_key_text.isdigit() else None,
        treat_nulls_as_equal=(
            (treat_nulls_text or "").strip().lower() == "true" if treat_nulls_text is not None else None
        ),
    )

    inputs_container = next(iter(_direct_children(component_elem, "inputs")), None)
    if inputs_container is not None:
        for input_elem in _direct_children(inputs_container, "input"):
            input_name = input_elem.attrib.get("name", "")
            side = "left" if "Left" in input_name else "right" if "Right" in input_name else input_name
            input_columns = next(iter(_direct_children(input_elem, "inputColumns")), None)
            if input_columns is None:
                continue
            for col in _direct_children(input_columns, "inputColumn"):
                pos_text = col.attrib.get("cachedSortKeyPosition")
                result.input_columns.append(MergeJoinInputColumn(
                    name=col.attrib.get("cachedName", ""),
                    side=side,
                    sort_key_position=int(pos_text) if pos_text and pos_text.lstrip("-").isdigit() else None,
                ))

    for output in _outputs(component_elem):
        output_columns = next(iter(_direct_children(output, "outputColumns")), None)
        if output_columns is None:
            continue
        for col in _direct_children(output_columns, "outputColumn"):
            col_props = _find_properties(col)
            source = resolve_lineage(_property_text(col_props, "InputColumnID"))
            result.output_columns.append(MergeJoinOutputColumn(
                name=col.attrib.get("name", ""),
                source_column=source[1] if source else None,
            ))

    return result


def _sort_direction(position: int) -> str:
    if position > 0:
        return "ascending"
    if position < 0:
        return "descending"
    return "not-sorted"


def extract_sort(component_elem) -> SortSemantics:
    result = SortSemantics()
    input_elem = _first_input(component_elem)
    if input_elem is None:
        return result
    input_columns = next(iter(_direct_children(input_elem, "inputColumns")), None)
    if input_columns is None:
        return result
    for col in _direct_children(input_columns, "inputColumn"):
        col_props = _find_properties(col)
        pos_text = (_property_text(col_props, "NewSortKeyPosition") or "0").strip()
        position = int(pos_text) if pos_text.lstrip("-").isdigit() else 0
        result.columns.append(SortColumn(
            name=col.attrib.get("cachedName", ""),
            sort_key_position=position,
            direction=_sort_direction(position),
        ))
    return result


def _script_source_triplets(source_code_prop) -> list[tuple[str, str]]:
    """A `SourceCode` property with `isArray="true"` stores (filename, encoding, content)
    triplets as sibling <arrayElement> children -- confirmed structurally across every sample."""
    array_elements = next(iter(_direct_children(source_code_prop, "arrayElements")), None)
    if array_elements is None:
        return []
    elements = _direct_children(array_elements, "arrayElement")
    return [
        (elements[i].text or "", elements[i + 2].text or "")
        for i in range(0, len(elements) - 2, 3)
    ]


def extract_script_component_source(component_elem) -> ScriptSemantics:
    props = _find_properties(component_elem)
    result = ScriptSemantics()
    if props is None:
        return result
    for prop in _direct_children(props, "property"):
        if prop.attrib.get("name") != "SourceCode":
            continue
        for file_name, content in _script_source_triplets(prop):
            if file_name.strip().lower() in _SCRIPT_COMPONENT_REAL_FILES:
                result.files.append(ScriptSourceFile(file_name=file_name, source_code=content))
    return result


def extract_script_task_source(script_project_elem) -> ScriptSemantics:
    """`script_project_elem` is the <ScriptProject> element under a Script Task's
    <DTS:ObjectData> -- its real entry point is a <ProjectItem Name="ScriptMain.cs|.vb">."""
    result = ScriptSemantics()
    for item in _direct_children(script_project_elem, "ProjectItem"):
        name = item.attrib.get("Name", "")
        if name.strip().lower() in _SCRIPT_TASK_REAL_FILES:
            result.files.append(ScriptSourceFile(file_name=name, source_code=item.text or ""))
    return result


def extract_all_script_task_sources(dtsx_path: str | Path) -> dict[str, ScriptSemantics]:
    """Returns {task_object_name: ScriptSemantics} for every Microsoft.ScriptTask in the package,
    at any nesting depth. A Script Task is a control-flow node, not a data-flow component, so it
    never appears in dtsx/component_schema.py's per-pipeline walk -- this is package-wide, mirroring
    dtsx/data_flow.py's own top-to-bottom Executables walk."""
    root = parse_dtsx(dtsx_path)
    results: dict[str, ScriptSemantics] = {}

    def walk(owner_elem) -> None:
        for executables_wrapper in _direct_children(owner_elem, "Executables"):
            for child in _direct_children(executables_wrapper, "Executable"):
                if _dts_attr(child, "Disabled") == "True":
                    continue
                object_name = _dts_attr(child, "ObjectName") or "?"
                if _dts_attr(child, "CreationName") == "Microsoft.ScriptTask":
                    object_data = next(iter(_direct_children(child, "ObjectData")), None)
                    if object_data is not None:
                        script_project = next(
                            (c for c in object_data if _local_name(c.tag) == "ScriptProject"), None
                        )
                        if script_project is not None:
                            semantics = extract_script_task_source(script_project)
                            if semantics.files:
                                results[object_name] = semantics
                else:
                    walk(child)

    walk(root)
    return results
