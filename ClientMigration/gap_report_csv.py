#!/usr/bin/env python3
"""
gap_report_csv.py -- CSV export of gap_report.py's findings: every package instance
that uses a component/task type NOT in known_client_side_component_types.txt.

Columns: Component/Task Name, Is Third Party, Package Name, Package Full Path

"Component/Task Name" uses the same canonical-id resolution as ssis_scan.py (legacy
ProgID folding, CLSID->friendly-name via contactInfo, ManagedComponentHost->
UserComponentTypeName unwrapping) so it matches what gap_report.py already printed.

Excludes "Flat File Destination" (the pre-fold friendly-name spelling of the already-
supported Microsoft.FlatFileDestination CLSID) -- not a real unsupported gap, per the
known-list's own dedupe note.
"""
import csv
import os
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

sys.path.insert(0, os.path.dirname(__file__))
import ssis_scan as sc

ROOT = sys.argv[1] if len(sys.argv) > 1 else r"D:\PoC\SSIS_Packages_From_GitHub"
KNOWN_FILE = sys.argv[2] if len(sys.argv) > 2 else r"D:\PoC\SSIS\ClientMigration\known_client_side_component_types.txt"
OUT_CSV = sys.argv[3] if len(sys.argv) > 3 else r"D:\PoC\SSIS\ClientMigration\unsupported_components_report.csv"

# Not a real gap -- pre-fold friendly-name spelling of an already-supported CLSID.
NOT_A_REAL_GAP = {"Flat File Destination"}

THIRD_PARTY_PREFIXES = ("Attunity.", "ZappySys.")

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

# (canonical id) -> set of (package_name, full_path)
comp_full = defaultdict(set)
task_full = defaultdict(set)

for path in sorted(canon):
    pkg_name = os.path.splitext(os.path.basename(path))[0]
    full_path = os.path.abspath(path)
    try:
        root = ET.parse(path).getroot()
    except Exception:
        continue

    for ex in root.iter():
        if sc.ln(ex.tag) != "Executable":
            continue
        et = sc.at(ex, "ExecutableType")
        if et:
            task_full[et].add((pkg_name, full_path))

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
            comp_full[sc.SCRIPT].add((pkg_name, full_path))
            continue
        canonid = sc.LEGACY.get(cid, cid)
        ci = sc.at(c, "contactInfo") or ""
        seg = [s.strip() for s in ci.split(";")]
        if canonid.startswith("{"):
            nm = seg[0] if seg and seg[0] else canonid
            if nm != canonid:
                canonid = nm
        elif canonid in sc.HOSTS:
            canonid = (P.get("UserComponentTypeName") or canonid).split(",")[0].strip() or canonid
        comp_full[canonid].add((pkg_name, full_path))


def is_third_party(name):
    if name.startswith(THIRD_PARTY_PREFIXES):
        return "Yes"
    if name.startswith("{") and name.endswith("}"):
        return "Unknown (unresolved CLSID)"
    return "No"


rows = []
for name in sorted(comp_full):
    if name in known_ids or name in NOT_A_REAL_GAP:
        continue
    for pkg_name, full_path in sorted(comp_full[name], key=lambda x: x[1]):
        rows.append({
            "Component/Task Name": name,
            "Is Third Party": is_third_party(name),
            "Package Name": pkg_name,
            "Package Full Path": full_path,
        })

for name in sorted(task_full):
    if name in known_ids:
        continue
    for pkg_name, full_path in sorted(task_full[name], key=lambda x: x[1]):
        rows.append({
            "Component/Task Name": name,
            "Is Third Party": is_third_party(name),
            "Package Name": pkg_name,
            "Package Full Path": full_path,
        })

cols = ["Component/Task Name", "Is Third Party", "Package Name", "Package Full Path"]
with open(OUT_CSV, "w", newline="", encoding="utf-8-sig") as fh:
    w = csv.DictWriter(fh, fieldnames=cols)
    w.writeheader()
    for r in rows:
        w.writerow(r)

print("Wrote %d rows to %s" % (len(rows), OUT_CSV))
distinct = sorted(set(r["Component/Task Name"] for r in rows))
print("Distinct unsupported component/task types: %d" % len(distinct))
for d in distinct:
    print("  %s" % d)
