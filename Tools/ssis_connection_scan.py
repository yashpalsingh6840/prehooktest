#!/usr/bin/env python3
"""
ssis_connection_scan.py -- scan a folder tree of .dtsx files and report what
each package actually CONNECTS TO: flat files, Excel, or a database -- and if
a database, which real vendor (SQL Server, Oracle, Sybase, DB2, MySQL,
PostgreSQL, or an unrecognized ODBC/OLEDB provider).

Why this exists: a package inventory built from PIPELINE COMPONENT TYPES
(Microsoft.OLEDBSource, Microsoft.ADONETDestination, ...) cannot answer "is
this all SQL Server, or is there Oracle/Sybase in here too" -- those
component classes are provider-agnostic. The real vendor only shows up on
each package's own <DTS:ConnectionManager> elements: its CreationName
(OLEDB / ADO.NET:<TypeName> / ODBC / FLATFILE / EXCEL / FILE / ...) and,
for OLEDB/ODBC, the Provider=/Driver= key inside its ConnectionString.

Usage:
    python ssis_connection_scan.py <folder> [--out connection_scan.csv] [--no-dedupe]

Stdlib only -- no pip install needed, so it runs anywhere Python 3 runs,
including a locked-down client machine.

What it does:
  1. Recursively finds every *.dtsx under <folder>.
  2. Dedupes by (filename, sha256) so N copies of the same package on disk
     are counted once, and flags "drift" -- same filename, different content
     -- as something a human should reconcile rather than silently pick one.
  3. Parses each distinct package's XML directly (no SSIS install/GAC needed)
     and reads every top-level connection manager's CreationName + the
     ConnectionString buried in its own <DTS:ObjectData>.
  4. Classifies each one into a vendor + kind (File / Database / Other),
     masking any Password=/Pwd= value before it's ever printed or written out.
  5. Prints a vendor breakdown to the console and writes one CSV row per
     (package, connection manager) for a full audit trail.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path

# Split so this never reads as one contiguous "www.something.com" substring --
# chat apps / Teams / Slack / browser viewers auto-linkify that shape, and the
# quote characters touching the link frequently get eaten on copy-paste,
# turning this into `DTS_NS = www.microsoft.com/SqlServer/Dts` (a NameError on
# `www`) on whatever machine the file gets pasted onto next.
DTS_NS = "www" + ".microsoft.com/SqlServer/Dts"
Q = "{" + DTS_NS + "}"  # tag/attribute prefix, e.g. Q + "ConnectionManager"


# --------------------------------------------------------------------------
# Discovery + dedupe
# --------------------------------------------------------------------------

def find_dtsx_files(root: Path):
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in filenames:
            if name.lower().endswith(".dtsx"):
                yield Path(dirpath) / name


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


@dataclass
class DedupeResult:
    unique_paths: list         # one Path per distinct (filename, content)
    files_on_disk: int
    duplicate_copies_skipped: int
    drifted_filenames: list    # filenames seen with >1 distinct content hash


def dedupe(files: list, no_dedupe: bool) -> DedupeResult:
    if no_dedupe:
        return DedupeResult(list(files), len(files), 0, [])

    by_name: dict[str, dict[str, Path]] = defaultdict(dict)
    duplicates_skipped = 0
    for path in files:
        try:
            h = sha256_of(path)
        except OSError:
            # unreadable file -- keep it, let the XML parse step report why
            by_name[path.name][f"unreadable:{path}"] = path
            continue
        key = path.name.lower()
        if h in by_name[key]:
            duplicates_skipped += 1
        else:
            by_name[key][h] = path

    unique_paths = [p for variants in by_name.values() for p in variants.values()]
    drifted = sorted(name for name, variants in by_name.items() if len(variants) > 1)
    return DedupeResult(unique_paths, len(files), duplicates_skipped, drifted)


# --------------------------------------------------------------------------
# Parsing
# --------------------------------------------------------------------------

@dataclass
class ConnectionManagerInfo:
    package_name: str
    package_path: str
    object_name: str
    creation_name: str
    connection_string_raw: str | None
    provider_or_driver: str | None
    server: str | None
    database: str | None
    vendor: str
    kind: str  # "File" | "Database" | "Other"


@dataclass
class PackageParseFailure:
    path: str
    reason: str


def _find_connection_string(cm_element: ET.Element) -> str | None:
    """The real ConnectionString attribute lives on a nested <DTS:ObjectData>
    child, not the outer <DTS:ConnectionManager> element itself -- walk the
    whole subtree rather than assuming a fixed depth."""
    for el in cm_element.iter():
        for key, value in el.attrib.items():
            if key.endswith("ConnectionString") and value:
                return value
    return None


def _extract_kv(conn_str: str | None, keys: list) -> str | None:
    if not conn_str:
        return None
    for key in keys:
        m = re.search(re.escape(key) + r"\s*=\s*([^;]*)", conn_str, re.IGNORECASE)
        if m:
            value = m.group(1).strip()
            if value:
                return value
    return None


def mask_connection_string(conn_str: str | None) -> str | None:
    if not conn_str:
        return conn_str
    return re.sub(
        r"(?i)(password|pwd)\s*=\s*[^;]*",
        lambda m: f"{m.group(1)}=****",
        conn_str,
    )


def classify_by_text(text: str | None) -> str | None:
    """Best-effort vendor guess from free text (a Driver= value, or a whole
    connection string when nothing more specific matched)."""
    if not text:
        return None
    t = text.upper()
    if "SQL SERVER" in t or "SQLNCLI" in t or "SQLOLEDB" in t or "MSOLEDBSQL" in t:
        return "SQL Server"
    if "ORACLE" in t:
        return "Oracle"
    if "SYBASE" in t or "ADAPTIVE SERVER" in t or "ASE" in t:
        return "Sybase"
    if "DB2" in t:
        return "DB2"
    if "MYSQL" in t:
        return "MySQL"
    if "POSTGRE" in t:
        return "PostgreSQL"
    if "TERADATA" in t:
        return "Teradata"
    if "SNOWFLAKE" in t:
        return "Snowflake"
    return None


def classify_connection_manager(creation_name: str, conn_str: str | None):
    """Returns (vendor, kind, provider_or_driver)."""
    cn = (creation_name or "").upper()
    provider = _extract_kv(conn_str, ["Provider"])
    driver = _extract_kv(conn_str, ["Driver"])

    if cn in ("FLATFILE", "MULTIFLATFILE"):
        return "Flat File", "File", None
    if cn == "EXCEL":
        return "Excel", "File", provider
    if cn == "FILE":
        return "File System (path/folder reference)", "File", None
    if cn == "FTP":
        return "FTP", "File", None
    if cn == "SMTP":
        return "SMTP / Email", "Other", None
    if cn.startswith("MSOLAP"):
        return "Analysis Services (OLAP)", "Database", None
    if cn.startswith("HTTP"):
        return "HTTP endpoint", "Other", None

    if cn.startswith("ADO.NET:"):
        adonet_type = creation_name.split(":", 1)[1]
        a = adonet_type.upper()
        if "SQLCLIENT" in a:
            vendor = "SQL Server"
        elif "ORACLE" in a:
            vendor = "Oracle"
        elif "SYBASE" in a or "ASECLIENT" in a or "IANYWHERE" in a:
            vendor = "Sybase"
        elif "DB2" in a:
            vendor = "DB2"
        elif "MYSQL" in a:
            vendor = "MySQL"
        elif "NPGSQL" in a or "POSTGRE" in a:
            vendor = "PostgreSQL"
        elif "ODBC" in a:
            vendor = classify_by_text(driver) or classify_by_text(conn_str) or "ODBC via ADO.NET (unrecognized driver)"
        else:
            vendor = f"Other ADO.NET provider ({adonet_type})"
        return vendor, "Database", adonet_type

    if cn == "OLEDB":
        p = (provider or "").upper()
        if any(k in p for k in ("SQLNCLI", "SQLOLEDB", "MSOLEDBSQL")):
            vendor = "SQL Server"
        elif "ORAOLEDB" in p or "MSDAORA" in p:
            vendor = "Oracle"
        elif "ASEOLEDB" in p or "SYBASE" in p:
            vendor = "Sybase"
        elif "DB2" in p:
            vendor = "DB2"
        elif "ACE.OLEDB" in p or "JET.OLEDB" in p:
            vendor = "Excel/Access (via OLEDB)"
        elif "MSDASQL" in p:
            vendor = classify_by_text(driver) or classify_by_text(conn_str) or "ODBC via MSDASQL (unrecognized driver)"
        elif provider:
            vendor = f"Other OLEDB provider ({provider})"
        else:
            vendor = "OLEDB (no Provider= found -- inspect manually)"
        return vendor, "Database", provider

    if cn == "ODBC":
        vendor = classify_by_text(driver) or classify_by_text(conn_str) or "ODBC (driver unrecognized -- inspect manually)"
        return vendor, "Database", driver

    return f"Unclassified (CreationName={creation_name})", "Other", provider or driver


def parse_package(path: Path):
    """Returns (package_name, [ConnectionManagerInfo]) or raises."""
    tree = ET.parse(str(path))
    root = tree.getroot()
    package_name = root.get(Q + "ObjectName") or path.stem

    cm_container = root.find(Q + "ConnectionManagers")
    cm_elements = list(cm_container) if cm_container is not None else []

    results = []
    for cm in cm_elements:
        if cm.tag != Q + "ConnectionManager":
            continue
        creation_name = cm.get(Q + "CreationName", "")
        object_name = cm.get(Q + "ObjectName", "(unnamed)")
        conn_str = _find_connection_string(cm)
        vendor, kind, provider_or_driver = classify_connection_manager(creation_name, conn_str)
        server = _extract_kv(conn_str, ["Data Source", "Server", "Server Name"])
        database = _extract_kv(conn_str, ["Initial Catalog", "Database"])
        results.append(
            ConnectionManagerInfo(
                package_name=package_name,
                package_path=str(path),
                object_name=object_name,
                creation_name=creation_name,
                connection_string_raw=mask_connection_string(conn_str),
                provider_or_driver=provider_or_driver,
                server=server,
                database=database,
                vendor=vendor,
                kind=kind,
            )
        )
    return package_name, results


# --------------------------------------------------------------------------
# Reporting
# --------------------------------------------------------------------------

def format_table(headers: list, rows: list) -> str:
    """Plain aligned text table -- no external dependency, works in any
    terminal. Column widths are computed from the actual data so nothing
    gets truncated (this is a "print everything" report, not a summary)."""
    if not rows:
        return "    (none)"
    widths = [len(h) for h in headers]
    str_rows = [[str(c) if c not in (None, "") else "-" for c in row] for row in rows]
    for row in str_rows:
        for i, cell in enumerate(row):
            widths[i] = max(widths[i], len(cell))
    def fmt_row(cells):
        return "  ".join(cell.ljust(widths[i]) for i, cell in enumerate(cells))
    lines = [fmt_row(headers), fmt_row(["-" * w for w in widths])]
    lines.extend(fmt_row(row) for row in str_rows)
    return "\n".join(f"    {line}" for line in lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("input", help="Root folder to scan recursively for .dtsx files")
    parser.add_argument("--out", default=None, help="Also write a CSV with the same detail (off by default -- everything already prints to the console)")
    parser.add_argument("--no-dedupe", action="store_true", help="Do not skip byte-identical duplicate copies of the same filename")
    args = parser.parse_args()

    root = Path(args.input)
    if not root.exists():
        print(f"error: input path does not exist: {root}", file=sys.stderr)
        return 2

    all_files = list(find_dtsx_files(root))
    dedupe_result = dedupe(all_files, args.no_dedupe)

    print("=== DISCOVERY ===")
    print(f".dtsx files on disk      : {dedupe_result.files_on_disk}")
    print(f"distinct packages        : {len(dedupe_result.unique_paths)}")
    print(f"duplicate copies skipped : {dedupe_result.duplicate_copies_skipped}")
    if dedupe_result.drifted_filenames:
        print(f"PACKAGE DRIFT (same filename, different content): {len(dedupe_result.drifted_filenames)}  <-- reconcile these")
        for name in dedupe_result.drifted_filenames:
            print(f"    {name}")
    print()

    all_conn_managers: list = []
    failures: list = []
    for path in sorted(dedupe_result.unique_paths, key=lambda p: str(p).lower()):
        try:
            _name, conn_managers = parse_package(path)
            all_conn_managers.extend(conn_managers)
        except ET.ParseError as e:
            failures.append(PackageParseFailure(str(path), f"XML parse error: {e}"))
        except OSError as e:
            failures.append(PackageParseFailure(str(path), f"read error: {e}"))

    if failures:
        print(f"=== PARSE FAILURES ({len(failures)}) ===")
        for f in failures:
            print(f"    {f.path} -- {f.reason}")
        print()

    # ---- vendor breakdown (counts only -- the crisp headline) ----
    vendor_counts = Counter(cm.vendor for cm in all_conn_managers)
    vendor_kind = {cm.vendor: cm.kind for cm in all_conn_managers}
    packages_by_vendor: dict[str, set] = defaultdict(set)
    rows_by_vendor: dict[str, list] = defaultdict(list)
    for cm in all_conn_managers:
        packages_by_vendor[cm.vendor].add(cm.package_name)
        rows_by_vendor[cm.vendor].append(cm)

    print("=== SUMMARY ===")
    print(f"total connection managers found: {len(all_conn_managers)}")
    database_vendors = sorted(
        (v for v in vendor_counts if vendor_kind[v] == "Database"),
        key=lambda v: -vendor_counts[v],
    )
    file_vendors = sorted(
        (v for v in vendor_counts if vendor_kind[v] == "File"),
        key=lambda v: -vendor_counts[v],
    )
    other_vendors = sorted(
        (v for v in vendor_counts if vendor_kind[v] not in ("Database", "File")),
        key=lambda v: -vendor_counts[v],
    )
    all_vendors_in_order = database_vendors + file_vendors + other_vendors
    for vendor in all_vendors_in_order:
        n = vendor_counts[vendor]
        pkgs = len(packages_by_vendor[vendor])
        flag = "  <-- REVIEW MANUALLY" if ("unrecognized" in vendor.lower() or "unclassified" in vendor.lower() or "inspect manually" in vendor.lower()) else ""
        print(f"    [{vendor_kind[vendor]:<8}] {vendor:<45} connections={n:<4} packages={pkgs}{flag}")
    print()

    # ---- full detail, every row, every field -- grouped by vendor ----
    print("=== FULL DETAIL (every connection manager, every field) ===")
    for section_title, vendors in (
        ("-- DATABASE --", database_vendors),
        ("-- FILE --", file_vendors),
        ("-- OTHER --", other_vendors),
    ):
        if not vendors:
            continue
        print(section_title)
        for vendor in vendors:
            print(f"  {vendor}  ({vendor_counts[vendor]} connection manager(s), {len(packages_by_vendor[vendor])} package(s))")
            rows = sorted(rows_by_vendor[vendor], key=lambda c: (c.package_name.lower(), c.object_name.lower()))
            table_rows = [
                [
                    cm.package_name, cm.object_name, cm.creation_name,
                    cm.provider_or_driver, cm.server, cm.database,
                    cm.connection_string_raw,
                ]
                for cm in rows
            ]
            print(format_table(
                ["Package", "ConnMgr", "CreationName", "Provider/Driver", "Server", "Database", "ConnectionString (masked)"],
                table_rows,
            ))
            print()

    non_sql_server_db_vendors = [
        v for v in database_vendors if v != "SQL Server"
    ]
    print("=== ANSWER: is everything SQL Server (plus Flat File / Excel)? ===")
    if not non_sql_server_db_vendors:
        print("Yes -- every DATABASE connection manager found resolves to SQL Server.")
        print("No Oracle, Sybase, DB2, MySQL, or PostgreSQL connection managers were found.")
    else:
        print("No -- at least one non-SQL-Server database vendor was found:")
        for v in non_sql_server_db_vendors:
            print(f"    {v}  ({vendor_counts[v]} connection manager(s) across {len(packages_by_vendor[v])} package(s))")
    print()

    # ---- CSV (optional -- everything above already covers it) ----
    if args.out:
        out_path = Path(args.out)
        with open(out_path, "w", newline="", encoding="utf-8") as f:
            writer = csv.writer(f)
            writer.writerow(
                [
                    "PackageName", "PackagePath", "ConnectionManagerName", "CreationName",
                    "Vendor", "Kind", "ProviderOrDriver", "Server", "Database",
                    "ConnectionStringMasked",
                ]
            )
            for cm in sorted(all_conn_managers, key=lambda c: (c.package_name.lower(), c.object_name.lower())):
                writer.writerow(
                    [
                        cm.package_name, cm.package_path, cm.object_name, cm.creation_name,
                        cm.vendor, cm.kind, cm.provider_or_driver or "", cm.server or "",
                        cm.database or "", cm.connection_string_raw or "",
                    ]
                )
        print(f"Also written to: {out_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
