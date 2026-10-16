#!/usr/bin/env python3
"""
Stage 3: deterministic merge of Copilot-produced fact JSON into the
DI_Repository_Functional_Baseline Word template.

Pure Python standard library only (zipfile + xml.etree.ElementTree).
No python-docx / lxml dependency on purpose: corporate / locked-down dev
machines frequently cannot reach PyPI, and even when they can, lxml ships
no prebuilt wheel for newer/beta CPython builds, so `pip install python-docx`
is not a reliable step to depend on in this pipeline. This script runs with
nothing beyond a standard Python 3 install.

Usage:
    python merge_to_docx.py --doc baseline.docx --facts facts_EP-1.json --schemas schemas.json [--out baseline.docx]

facts JSON shape:
{
  "document_control": {"Repository Name": "...", "Migration Stream": "...", ...},
  "functional_overview": [{"Item": "...", "Current Understanding": "...", "Validation Status": "..."}],
  "sections": {
    "3": [ {"Function ID": "AUTO", "Functionality / Capability": "...", ...}, ... ],
    "4": [ {...} ],
    ...
  }
}

- "AUTO" in an id_field gets replaced with the next unused ID for that prefix,
  scanned from the rows already in the table.
- Missing columns are written as empty strings; extra keys are ignored.
- Re-run safely across entry points: each run only appends, never rewrites.
"""
import argparse
import copy
import io
import json
import re
import zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

W_NS = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"


def qn(tag):
    return f"{{{W_NS}}}{tag}"


P, TBL, TR, TC, T, PPR, PSTYLE, VAL = (
    qn("p"), qn("tbl"), qn("tr"), qn("tc"), qn("t"), qn("pPr"), qn("pStyle"), qn("val"),
)


def read_document_xml(docx_path):
    with zipfile.ZipFile(docx_path) as z:
        return z.read("word/document.xml")


def register_namespaces(xml_bytes):
    for _, (prefix, uri) in ET.iterparse(io.BytesIO(xml_bytes), events=["start-ns"]):
        ET.register_namespace(prefix if prefix else "", uri)


def paragraph_text(p_elem):
    return "".join(t.text or "" for t in p_elem.iter(T))


def get_section_tables(root):
    """Map each Heading-1 paragraph's visible text to the w:tbl element that follows it."""
    body = root.find(qn("body"))
    heading_to_table = {}
    current_heading = None
    for child in list(body):
        if child.tag == P:
            pPr = child.find(PPR)
            style = pPr.find(PSTYLE) if pPr is not None else None
            if style is not None and style.get(VAL) == "Heading1":
                text = paragraph_text(child).strip()
                if text:
                    current_heading = text
        elif child.tag == TBL:
            if current_heading:
                heading_to_table[current_heading] = child
                current_heading = None
    return heading_to_table


def cell_text(tc):
    return "".join(t.text or "" for t in tc.iter(T)).strip()


def set_cell_text(tc, text):
    text = "" if text is None else str(text)
    first_p = tc.find(P)
    if first_p is None:
        first_p = ET.SubElement(tc, P)
    r = first_p.find(qn("r"))
    if r is None:
        r = ET.SubElement(first_p, qn("r"))
    t = r.find(T)
    if t is None:
        t = ET.SubElement(r, T)
    t.set("{http://www.w3.org/XML/1998/namespace}space", "preserve")
    t.text = text
    # blank out every other w:t in this cell (other runs/paragraphs) so stale text can't linger
    for other_t in list(tc.iter(T)):
        if other_t is not t:
            other_t.text = ""


def get_rows(tbl):
    return tbl.findall(TR)


def get_cells(tr):
    return tr.findall(TC)


def clone_last_row(tbl):
    rows = get_rows(tbl)
    last = rows[-1]
    new_row = copy.deepcopy(last)
    children = list(tbl)
    idx = children.index(last)
    tbl.insert(idx + 1, new_row)
    return new_row


def next_id(tbl, id_field, columns, prefix):
    col_idx = columns.index(id_field)
    max_n = 0
    pattern = re.compile(rf"^{re.escape(prefix)}-?(\d+)$", re.I)
    for row in get_rows(tbl)[1:]:
        cells = get_cells(row)
        if col_idx < len(cells):
            m = pattern.match(cell_text(cells[col_idx]))
            if m:
                max_n = max(max_n, int(m.group(1)))
    return f"{prefix}-{max_n + 1:02d}"


def update_kv_table(tbl, kv_dict):
    for row in get_rows(tbl):
        cells = get_cells(row)
        for i in range(0, len(cells) - 1, 2):
            label = cell_text(cells[i])
            if label in kv_dict:
                set_cell_text(cells[i + 1], kv_dict[label])


def append_rows(tbl, columns, rows, id_field=None, prefix=None):
    for row_dict in rows:
        new_row = clone_last_row(tbl)
        if id_field and prefix and str(row_dict.get(id_field, "")).upper() == "AUTO":
            row_dict[id_field] = next_id(tbl, id_field, columns, prefix)
        cells = get_cells(new_row)
        for i, col in enumerate(columns):
            if i < len(cells):
                set_cell_text(cells[i], row_dict.get(col, ""))


def write_document_xml(docx_path, out_path, root, original_prolog):
    body_bytes = ET.tostring(root, encoding="unicode").encode("utf-8")
    new_xml = original_prolog + body_bytes

    with zipfile.ZipFile(docx_path) as zin:
        infos = zin.infolist()
        data = {i.filename: zin.read(i.filename) for i in infos}
    data["word/document.xml"] = new_xml

    out_path = str(out_path)
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as zout:
        for info in infos:
            zout.writestr(info, data[info.filename])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--doc", required=True)
    ap.add_argument("--facts", required=True)
    ap.add_argument("--schemas", default=str(Path(__file__).parent / "schemas.json"))
    ap.add_argument("--out", default=None, help="Defaults to overwriting --doc in place")
    args = ap.parse_args()

    schemas = json.loads(Path(args.schemas).read_text())
    facts = json.loads(Path(args.facts).read_text())

    xml_bytes = read_document_xml(args.doc)
    prolog_match = re.match(rb"^<\?xml[^>]*\?>\s*", xml_bytes)
    original_prolog = prolog_match.group(0) if prolog_match else b'<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\r\n'

    register_namespaces(xml_bytes)
    root = ET.fromstring(xml_bytes)
    heading_to_table = get_section_tables(root)

    if "document_control" in facts:
        spec = schemas["document_control"]
        table = heading_to_table.get(spec["heading"])
        if table is not None:
            update_kv_table(table, facts["document_control"])
        else:
            print(f"WARNING: heading '{spec['heading']}' not found, skipping document_control")

    if "functional_overview" in facts:
        spec = schemas["functional_overview"]
        table = heading_to_table.get(spec["heading"])
        if table is not None:
            append_rows(table, spec["columns"], facts["functional_overview"])
        else:
            print(f"WARNING: heading '{spec['heading']}' not found, skipping functional_overview")

    for section_id, rows in facts.get("sections", {}).items():
        spec = schemas.get(section_id)
        if not spec:
            print(f"WARNING: unknown section id '{section_id}' in facts, skipping")
            continue
        table = heading_to_table.get(spec["heading"])
        if table is None:
            print(f"WARNING: heading '{spec['heading']}' not found, skipping section {section_id}")
            continue
        append_rows(table, spec["columns"], rows, spec.get("id_field"), spec.get("id_prefix"))

    out_path = args.out or args.doc
    write_document_xml(args.doc, out_path, root, original_prolog)
    print(f"Merged '{args.facts}' into '{out_path}'")


if __name__ == "__main__":
    main()
