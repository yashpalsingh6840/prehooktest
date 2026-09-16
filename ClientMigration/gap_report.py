#!/usr/bin/env python3
"""
gap_report.py -- find component/task types in a scanned SSIS portfolio that are NOT
listed in known_client_side_component_types.txt, with full package paths (not just
5 truncated examples like ssis_scan.py's console report).

Reuses ssis_scan.py's own XML-parsing/dedupe helpers so results are consistent with
that script's canonical component-id resolution (legacy ProgID folding, CLSID->name
resolution via contactInfo, ManagedComponentHost->UserComponentTypeName unwrapping).
"""
import os
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

sys.path.insert(0, os.path.dirname(__file__))
import ssis_scan as sc

ROOT = sys.argv[1] if len(sys.argv) > 1 else r"D:\PoC\SSIS_Packages_From_GitHub"
KNOWN_FILE = sys.argv[2] if len(sys.argv) > 2 else r"D:\PoC\SSIS\ClientMigration\known_client_side_component_types.txt"

# Pull every "Microsoft.X" / "STOCK:X" / bare-name token that appears in the known file
# as a left-hand identifier (start of a line in the two tables), so we don't need to
# hand-maintain a duplicate list here.
known_ids = set()
with open(KNOWN_FILE, encoding="utf-8") as f:
    for line in f:
        line = line.rstrip("\n")
        if not line or line[0] in " \t=-":
            continue
        token = line.split()[0] if line.split() else ""
        if token:
            known_ids.add(token)

paths, by_hash, canon, drift, folders, unread = sc.discover(ROOT)

comp_full = defaultdict(set)   # canonical component id -> set of (relpath)
task_full = defaultdict(set)   # ExecutableType -> set of (relpath)

for path in sorted(canon):
    rel = os.path.relpath(path, ROOT)
    try:
        root = ET.parse(path).getroot()
    except Exception:
        continue

    for ex in root.iter():
        if sc.ln(ex.tag) != "Executable":
            continue
        et = sc.at(ex, "ExecutableType")
        if et:
            task_full[et].add(rel)

    for c in root.iter():
        if sc.ln(c.tag) != "component":
            continue
        cid = sc.at(c, "componentClassID")
        if not cid:
            continue
        P = sc.dprops(c)
        srcprop = next((p for p in c.iter()
                        if sc.ln(p.tag) == "property" and sc.at(p, "name") == "SourceCode"), None)
        if srcprop is not None:
            comp_full[sc.SCRIPT].add(rel)
            continue
        canonid = sc.LEGACY.get(cid, cid)
        ci = sc.at(c, "contactInfo") or ""
        seg = [s.strip() for s in ci.split(";")]
        vendor = seg[1] if len(seg) > 1 else ""
        if canonid.startswith("{"):
            nm = seg[0] if seg and seg[0] else canonid
            if nm != canonid:
                canonid = nm
        elif canonid in sc.HOSTS:
            canonid = (P.get("UserComponentTypeName") or canonid).split(",")[0].strip() or canonid
        comp_full[canonid].add(rel)

print("=" * 100)
print("COMPONENT TYPES NOT IN known_client_side_component_types.txt")
print("=" * 100)
gap_components = {k: v for k, v in comp_full.items() if k not in known_ids}
for k in sorted(gap_components):
    print("\n--- %s  (%d package instance(s)) ---" % (k, len(gap_components[k])))
    for rel in sorted(gap_components[k]):
        print("    %s" % rel)

print()
print("=" * 100)
print("TASK TYPES NOT IN known_client_side_component_types.txt")
print("=" * 100)
gap_tasks = {k: v for k, v in task_full.items() if k not in known_ids}
for k in sorted(gap_tasks):
    print("\n--- %s  (%d package instance(s)) ---" % (k, len(gap_tasks[k])))
    for rel in sorted(gap_tasks[k]):
        print("    %s" % rel)

print()
print("=" * 100)
print("SUMMARY")
print("=" * 100)
print("Distinct component types found : %d" % len(comp_full))
print("  -> not in known list         : %d" % len(gap_components))
print("Distinct task types found      : %d" % len(task_full))
print("  -> not in known list         : %d" % len(gap_tasks))
