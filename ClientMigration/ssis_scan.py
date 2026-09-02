#!/usr/bin/env python3
"""
ssis_scan.py -- offline SSIS portfolio scan, dedupe-aware.

Reads .dtsx files as plain XML. Needs NO SSIS, no SQL Server, no Visual Studio.
Python 3.8+, standard library only.

Usage:  python ssis_scan.py <root-folder> [-c ssis_scan.csv] [-s scripts_out]
"""
import argparse, csv, hashlib, os, re, sys, xml.etree.ElementTree as ET
from collections import Counter, defaultdict

# ---------- namespace-agnostic xml helpers ----------
def ln(t):
    return t.rsplit("}", 1)[-1] if "}" in t else t

def at(el, n):
    for k, v in el.attrib.items():
        if ln(k) == n:
            return v
    return None

def kids(el, name):
    return [c for c in el if ln(c.tag) == name]

def dprops(el):
    """component-level custom properties only (not nested column properties)"""
    o = {}
    for pc in kids(el, "properties"):
        for p in kids(pc, "property"):
            n = at(p, "name")
            if n:
                o[n] = (p.text or "").strip()
    return o

def nprop(el, name):
    for p in el.iter():
        if ln(p.tag) == "property" and at(p, "name") == name:
            return (p.text or "").strip()
    return None

# ---------- lookup tables ----------
LEGACY = {   # old COM ProgID -> modern id. Stock Microsoft in legacy spelling, NOT third-party.
    "DTSAdapter.FlatFileSource.3": "Microsoft.FlatFileSource",
    "DTSAdapter.FlatFileDestination.3": "Microsoft.FlatFileDestination",
    "DTSAdapter.OLEDBSource.3": "Microsoft.OLEDBSource",
    "DTSAdapter.OLEDBDestination.3": "Microsoft.OLEDBDestination",
    "DTSAdapter.ExcelSource.3": "Microsoft.ExcelSource",
    "DTSAdapter.ExcelDestination.3": "Microsoft.ExcelDestination",
    "DTSAdapter.RawFileSource.3": "Microsoft.RawFileSource",
    "DTSTransform.DerivedColumn.3": "Microsoft.DerivedColumn",
    "DTSTransform.ConditionalSplit.3": "Microsoft.ConditionalSplit",
    "DTSTransform.DataConvert.3": "Microsoft.DataConvert",
    "DTSTransform.Lookup.3": "Microsoft.Lookup",
    "DTSTransform.Aggregate.3": "Microsoft.Aggregate",
    "DTSTransform.Sort.3": "Microsoft.Sort",
    "DTSTransform.Merge.3": "Microsoft.Merge",
    "DTSTransform.MergeJoin.3": "Microsoft.MergeJoin",
    "DTSTransform.UnionAll.3": "Microsoft.UnionAll",
    "DTSTransform.Multicast.3": "Microsoft.Multicast",
    "DTSTransform.RowCount.3": "Microsoft.RowCount",
    "DTSTransform.OLEDBCommand.3": "Microsoft.OLEDBCommand",
    "DTSTransform.Pivot.3": "Microsoft.Pivot",
    "DTSTransform.Unpivot.3": "Microsoft.Unpivot",
    "DTSTransform.SCD.3": "Microsoft.SCD",
    "DTS.ManagedComponentWrapper.3": "Microsoft.ManagedComponentHost",
}
HOSTS = {"Microsoft.ManagedComponentHost", "DTS.ManagedComponentWrapper.3"}
SCRIPT = "Microsoft.ScriptComponentHost"
AGG = {"0": "GroupBy", "1": "Count", "2": "CountDistinct", "3": "Sum",
       "4": "Avg", "5": "Min", "6": "Max", "7": "CountAll"}
CACHE = {"0": "FullCache", "1": "PartialCache", "2": "NoCache"}
EVALOP = {"1": "ExpressionOnly", "2": "ConstraintOnly",
          "3": "Success AND expression", "4": "Failure OR expression"}
OUTCOME = {"0": "Success", "1": "Failure", "2": "Completion"}
TXN = {"0": "NotSupported", "1": "Supported", "2": "Required"}
PROT = {"0": "DontSaveSensitive", "1": "EncryptSensitiveWithUserKey",
        "2": "EncryptSensitiveWithPassword", "3": "EncryptAllWithPassword",
        "4": "EncryptAllWithUserKey", "5": "ServerStorage"}
STOCK_CM = {"OLEDB", "FLATFILE", "ADO.NET", "ADO", "EXCEL", "FILE", "MULTIFILE",
            "MULTIFLATFILE", "ODBC", "OLAP", "MSOLAP100", "SMTP", "FTP", "HTTP",
            "MSMQ", "WMI", "SQLMOBILE", "CACHE", "SMOSERVER"}
PIPE = {"Microsoft.Pipeline", "Microsoft.Pipeline.2", "SSIS.Pipeline.3", "DTS.Pipeline.3"}
GEN = {"componentwrapper.cs", "componentwrapper.vb", "bufferwrapper.cs",
       "bufferwrapper.vb", "assemblyinfo.cs", "assemblyinfo.vb",
       "resources.resx", "settings.settings"}
COMMENT_STARTS = ("//", "*", "/*")

def is_authored(n):
    """
    Positive rule: authored logic is always a .cs/.vb file that is not VSTA boilerplate.
    A denylist is not enough -- every Script Task also carries an MSBuild descriptor
    stored under the extension-less name "Project", which is ~40 lines of pure
    boilerplate and would otherwise be counted as real code.
    """
    b = os.path.basename(n).lower()
    if not b.endswith((".cs", ".vb")):
        return False
    return b not in GEN and ".designer." not in b

def safe(s, maxlen=80):
    s = (s or "x").replace("\r", " ").replace("\n", " ")
    s = "".join(c if c.isalnum() or c in " ._-" else "_" for c in s).strip()
    return s[:maxlen] or "x"

# A Script Component stores its VSTA files in a flat SourceCode array. USUALLY that is a
# run of (name, encoding, content) triples -- but not always, and an element count that
# happens to divide by 3 is NOT proof. Validate the shape before trusting it, or a line
# of source code ends up being used as a filename.
ENC_HINTS = set(["utf8", "utf16", "utf16le", "utf16be", "unicodefffe", "ascii",
                 "windows1252", "utf7", "utf32", "iso88591", "default"])

def _norm_enc(s):
    return re.sub(r"[^a-z0-9]", "", (s or "").lower())

def looks_like_triples(blobs):
    if len(blobs) < 3 or len(blobs) % 3 != 0:
        return False
    for i in range(0, len(blobs), 3):
        name = (blobs[i] or "").strip()
        if not name or "\n" in name or len(name) > 120 or "." not in name:
            return False
        if _norm_enc(blobs[i + 1]) not in ENC_HINTS:
            return False
    return True

def looks_like_code(s):
    """for the non-triple fallback: is this element real source, or a name/encoding token?"""
    t = (s or "").strip()
    return len(t) >= 40 and ("\n" in t or ";" in t or "{" in t)

def is_comment(line):
    s = line.strip()
    if not s:
        return True
    if s.startswith(COMMENT_STARTS):
        return True
    return s[0] == "'"          # VB comment

def loc(t):
    return sum(1 for l in t.splitlines() if not is_comment(l))

def bodyhash(t):
    """content hash that ignores formatting and comments"""
    t = re.sub(r"/\*.*?\*/", "", t, flags=re.S)
    ls = [re.sub(r"\s+", " ", l.strip()) for l in t.splitlines() if not is_comment(l)]
    return hashlib.sha256("\n".join(ls).encode("utf-8", "replace")).hexdigest()

FUNC = re.compile(r"\b([A-Z][A-Z0-9_]{1,})\s*\(")
CAST = re.compile(r"\(\s*(DT_[A-Z0-9_]+)")

# ---------- discovery + dedupe ----------
def discover(root):
    paths = [os.path.join(d, f) for d, _, fs in os.walk(root)
             for f in fs if f.lower().endswith(".dtsx")]
    by_hash = defaultdict(list)
    unread = []
    for p in paths:
        try:
            with open(p, "rb") as fh:
                by_hash[hashlib.sha256(fh.read()).hexdigest()].append(p)
        except Exception as e:
            unread.append((p, str(e)[:120]))
    # one canonical copy per distinct content
    canon = [min(v, key=lambda x: (len(x), x)) for v in by_hash.values()]
    # same filename with different content = real drift, do NOT collapse
    by_name = defaultdict(set)
    for h, v in by_hash.items():
        for p in v:
            by_name[os.path.basename(p).lower()].add(h)
    drift = dict((n, hs) for n, hs in by_name.items() if len(hs) > 1)
    # which top-level folders are full copies of content found elsewhere?
    def top_of(p):
        rel = os.path.relpath(p, root)
        return rel.split(os.sep)[0] if os.sep in rel else "."
    hash_folders = defaultdict(set)
    for h, v in by_hash.items():
        for p in v:
            hash_folders[h].add(top_of(p))
    folders = defaultdict(lambda: [0, 0])
    for h, v in by_hash.items():
        for p in v:
            t = top_of(p)
            folders[t][0] += 1
            if len(hash_folders[h]) == 1:
                folders[t][1] += 1
    return paths, by_hash, sorted(canon), drift, folders, unread


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("root")
    ap.add_argument("-c", "--csv", default="ssis_scan.csv")
    ap.add_argument("-s", "--scripts", default="scripts_out")
    a = ap.parse_args()

    paths, by_hash, canon, drift, folders, unread = discover(a.root)
    if not paths:
        sys.exit("no .dtsx found under %s" % a.root)
    os.makedirs(a.scripts, exist_ok=True)

    rows = []
    comp_pkgs = defaultdict(set)
    task_pkgs = defaultdict(set)
    conn_pkgs = defaultdict(set)
    legacy = defaultdict(set)
    guids = defaultdict(lambda: {"name": set(), "vendor": set(), "pkgs": set()})
    thirdparty = defaultdict(set)
    scripts = []
    bodies = defaultdict(list)
    aggops, caches, lkkey, cons, funcs, casts = set(), set(), set(), set(), set(), set()
    st, langs, prot = Counter(), Counter(), Counter()
    flows, nosrc, odd = [], [], []

    for path in canon:
        pkg = os.path.splitext(os.path.basename(path))[0]
        try:
            root = ET.parse(path).getroot()
        except Exception as e:
            rows.append({"category": "parse_error", "item": pkg, "notes": str(e)[:200]})
            st["parse_fail"] += 1
            continue
        st["packages"] += 1
        fmt = "2012+" if at(root, "ExecutableType") else "pre-2012 (NEEDS DIFFERENT READER)"
        if fmt.startswith("pre"):
            st["pre2012"] += 1
        plv = at(root, "ProtectionLevel")
        plname = PROT.get(plv, "(absent - VERIFY)" if plv is None else plv)
        prot[plname] += 1
        counts = {"flow": 0, "scr": 0, "dis": 0}
        txn = set()

        def dump(unit, rid, fname, text, kind, lang_hint):
            if not is_authored(fname) or not text.strip():
                return
            n = loc(text)
            if not n:
                return
            lang = "VisualBasic" if fname.lower().endswith(".vb") else "CSharp"
            # short refId hash prevents same-named units overwriting each other
            tag = hashlib.sha1((rid or unit).encode("utf-8", "replace")).hexdigest()[:8]
            key = "%s__%s" % (safe(unit, 40), tag)
            d = os.path.join(a.scripts, safe(pkg, 60), key)
            try:
                os.makedirs(d, exist_ok=True)
                with open(os.path.join(d, safe(fname, 60)), "w",
                          encoding="utf-8", newline="") as fh:
                    fh.write(text)
            except OSError as e:
                # never let one awkward filename abort a whole-portfolio run
                odd.append("%s / %s: could not write %r (%s)" % (pkg, unit, fname[:40], e))
            langs[lang] += n
            bodies[bodyhash(text)].append((pkg, unit, fname, n, lang))
            scripts.append({"category": "script", "item": kind, "package": pkg,
                            "object_name": unit, "language": lang, "source_present": "yes",
                            "notes": "loc=%d; file=%s" % (n, fname)})

        # ---- control flow / tasks
        for ex in root.iter():
            if ln(ex.tag) != "Executable":
                continue
            et = at(ex, "ExecutableType")
            if not et:
                continue
            task_pkgs[et].add(pkg)
            if (at(ex, "Disabled") or "").lower() == "true":
                counts["dis"] += 1
            to = at(ex, "TransactionOption")
            if to and to != "1":
                txn.add(TXN.get(to, to))
            if et in PIPE:
                counts["flow"] += 1
                ncomp = sum(1 for c in ex.iter()
                            if ln(c.tag) == "component" and at(c, "componentClassID"))
                flows.append((ncomp, pkg, at(ex, "ObjectName") or ""))
            if et == "Microsoft.ScriptTask":
                counts["scr"] += 1
                sp = next((x for x in ex.iter() if ln(x.tag) == "ScriptProject"), None)
                items = [x for x in ex.iter() if ln(x.tag) == "ProjectItem"]
                lang = (at(sp, "Language") if sp is not None else None) or ""
                if not items:
                    nosrc.append("%s / %s (ScriptTask)" % (pkg, at(ex, "ObjectName")))
                for it in items:
                    dump(at(ex, "ObjectName") or "ScriptTask",
                         at(ex, "refId") or at(ex, "DTSID") or "",
                         at(it, "Name") or "item.txt", it.text or "", "ScriptTask", lang)

        # ---- pipeline components
        for c in root.iter():
            if ln(c.tag) != "component":
                continue
            cid = at(c, "componentClassID")
            if not cid:
                continue
            P = dprops(c)
            srcprop = next((p for p in c.iter()
                            if ln(p.tag) == "property" and at(p, "name") == "SourceCode"), None)
            if srcprop is not None:
                # Script Component -- identified by having source, so any spelling or CLSID
                counts["scr"] += 1
                comp_pkgs[SCRIPT].add(pkg)
                blobs = [(e.text or "") for e in srcprop.iter() if ln(e.tag) == "arrayElement"]
                unit = at(c, "name") or "ScriptComponent"
                rid = at(c, "refId") or ""
                lang = P.get("ScriptLanguage") or P.get("Language") or ""
                if not blobs:
                    nosrc.append("%s / %s (ScriptComponent)" % (pkg, unit))
                elif looks_like_triples(blobs):
                    for i in range(0, len(blobs), 3):
                        dump(unit, rid, blobs[i].strip(), blobs[i + 2], "ScriptComponent", lang)
                else:
                    # unknown layout: keep only elements that are plausibly source, so the
                    # LOC total is not inflated by filename/encoding tokens
                    code = [b for b in blobs if looks_like_code(b)]
                    odd.append("%s / %s : %d SourceCode elements, not (name,encoding,content) "
                               "triples -- kept %d as source"
                               % (pkg, unit, len(blobs), len(code)))
                    ext = ".vb" if (lang or "").lower().startswith("v") else ".cs"
                    for i, b in enumerate(code):
                        dump(unit, rid, "raw%d%s" % (i, ext), b, "ScriptComponent", lang)
                continue

            canonid = LEGACY.get(cid, cid)
            if cid in LEGACY:
                legacy[cid].add(pkg)
            ci = at(c, "contactInfo") or ""
            seg = [s.strip() for s in ci.split(";")]
            vendor = seg[1] if len(seg) > 1 else ""
            if canonid.startswith("{"):
                # a component stored by CLSID identifies itself in contactInfo
                nm = seg[0] if seg and seg[0] else canonid
                g = guids[canonid]
                g["name"].add(nm)
                g["pkgs"].add(pkg)
                if vendor:
                    g["vendor"].add(vendor)
                if nm != canonid:
                    canonid = nm
            elif canonid in HOSTS:
                canonid = (P.get("UserComponentTypeName") or canonid).split(",")[0].strip() or canonid
            comp_pkgs[canonid].add(pkg)
            ms = (canonid.startswith(("Microsoft.", "DTS"))
                  or canonid in LEGACY.values()
                  or "microsoft" in ci.lower()
                  or "microsoft" in vendor.lower())
            if not ms:
                thirdparty["%s [%s]" % (canonid, vendor or "vendor unknown")].add(pkg)

            if canonid.endswith("Aggregate"):
                for oc in c.iter():
                    if ln(oc.tag) == "outputColumn":
                        t = nprop(oc, "AggregationType")
                        if t:
                            aggops.add(AGG.get(t, t))
            if canonid.endswith("Lookup"):
                caches.add(CACHE.get(P.get("CacheType", ""), P.get("CacheType", "?")))
                keyed = any(ln(i.tag) == "inputColumn" and nprop(i, "JoinToReferenceColumn")
                            for i in c.iter())
                lkkey.add("join key declared" if keyed else "NO join key declared")

        # ---- connections / constraints / expressions
        for cm in root.iter():
            if ln(cm.tag) == "ConnectionManager" and at(cm, "CreationName"):
                conn_pkgs[at(cm, "CreationName")].add(pkg)
        for pc in root.iter():
            if ln(pc.tag) != "PrecedenceConstraint":
                continue
            ev, val = at(pc, "EvalOp"), at(pc, "Value")
            if ev or val:
                cons.add("EvalOp=%s | Outcome=%s"
                         % (EVALOP.get(ev, ev or "Constraint(default)"),
                            OUTCOME.get(val, val or "Success(default)")))
        texts = [(x.text or "") for x in root.iter() if ln(x.tag) == "PropertyExpression"]
        for p in root.iter():
            if ln(p.tag) == "property" and at(p, "name") in ("Expression", "FriendlyExpression"):
                texts.append(p.text or "")
        for t in texts:
            funcs.update(m for m in FUNC.findall(t) if not m.startswith("DT_"))
            casts.update(CAST.findall(t))

        st["flows"] += counts["flow"]
        st["scripts"] += counts["scr"]
        st["disabled"] += counts["dis"]
        rows.append({"category": "package", "item": pkg, "package": pkg,
                     "notes": "format=%s; protection=%s; dataflows=%s; scripts=%s; "
                              "disabled=%s; txn=%s"
                              % (fmt, plname,
                                 "yes" if counts["flow"] else "no",
                                 "yes" if counts["scr"] else "no",
                                 "yes" if counts["dis"] else "no",
                                 ";".join(sorted(txn)) or "Supported(default/none)")})

    # ---------- the single csv ----------
    def ex5(s, k=5):
        v = sorted(s)
        return "; ".join(v[:k]) + (" ; ..." if len(v) > k else "")

    for k in sorted(task_pkgs):
        rows.append({"category": "task_type", "item": k, "notes": "e.g. " + ex5(task_pkgs[k])})
    for k in sorted(comp_pkgs):
        rows.append({"category": "component_type", "item": k, "notes": "e.g. " + ex5(comp_pkgs[k])})
    for k in sorted(legacy):
        rows.append({"category": "component_legacy_progid", "item": k, "canonical": LEGACY[k],
                     "notes": "stock Microsoft, legacy spelling; e.g. " + ex5(legacy[k])})
    for k in sorted(guids):
        rows.append({"category": "component_by_clsid", "item": k,
                     "canonical": "; ".join(sorted(guids[k]["name"])),
                     "vendor_contact": "; ".join(sorted(guids[k]["vendor"])) or "(none)",
                     "notes": "e.g. " + ex5(guids[k]["pkgs"])})
    for k in sorted(thirdparty):
        rows.append({"category": "component_THIRD_PARTY", "item": k,
                     "notes": "e.g. " + ex5(thirdparty[k])})
    for k in sorted(conn_pkgs):
        b = k.split(".")[0].upper()
        stock = "STOCK" if (k.upper() in STOCK_CM or b in STOCK_CM) else "NON-STOCK - VERIFY"
        rows.append({"category": "connection_type", "item": k,
                     "notes": stock + "; e.g. " + ex5(conn_pkgs[k])})
    rows.extend(sorted(scripts, key=lambda r: (r["package"], r["object_name"])))
    for cat, vals in (("aggregate_op", aggops), ("lookup_cache_mode", caches),
                      ("lookup_join_key", lkkey), ("precedence_constraint", cons),
                      ("expression_function", funcs), ("expression_cast", casts)):
        for k in sorted(vals):
            rows.append({"category": cat, "item": k})
    for n in sorted(drift):
        rows.append({"category": "PACKAGE_DRIFT", "item": n,
                     "notes": "same filename, DIFFERENT content in more than one folder - reconcile"})
    for p_, e in unread:
        rows.append({"category": "unreadable_file", "item": p_, "notes": e})

    cols = ["category", "item", "canonical", "package", "object_name", "language",
            "source_present", "vendor_contact", "notes"]
    with open(a.csv, "w", newline="", encoding="utf-8-sig") as fh:
        w = csv.DictWriter(fh, fieldnames=cols, extrasaction="ignore")
        w.writeheader()
        for r in rows:
            w.writerow(dict((c, r.get(c, "")) for c in cols))

    # ---------- console report: this is the bit to screenshot ----------
    p = print
    p("")
    p("=== DEDUPE ===")
    p("  .dtsx files on disk      : %d" % len(paths))
    p("  DISTINCT packages        : %d   <-- real portfolio size" % len(canon))
    p("  duplicate copies skipped : %d" % (len(paths) - len(canon)))
    p("  unreadable files         : %d" % len(unread))
    p("  PACKAGE DRIFT (same name, different content): %d%s"
      % (len(drift), "   <-- RECONCILE" if drift else ""))
    for n in sorted(drift)[:8]:
        p("      %s" % n)
    p("  top-level folders (files / unique-to-folder):")
    for k, tu in sorted(folders.items(), key=lambda x: -x[1][0])[:10]:
        tot, uniq = tu
        flag = "   <-- FULL COPY, safe to ignore" if tot and uniq == 0 else ""
        p("     %5d / %5d   %s%s" % (tot, uniq, k, flag))
    p("")
    p("=== SCOPE (distinct packages only) ===")
    p("  packages scanned         : %d   parse failures: %d   pre-2012: %d"
      % (st["packages"], st["parse_fail"], st["pre2012"]))
    p("  data flow tasks          : %d" % st["flows"])
    p("  DISTINCT component types : %d   <-- emitters to build" % len(comp_pkgs))
    p("  distinct task types      : %d" % len(task_pkgs))
    p("  disabled executables     : %d" % st["disabled"])
    p("  THIRD-PARTY components   : %d  %s"
      % (len(thirdparty), sorted(thirdparty) if thirdparty else "none - all stock Microsoft"))
    p("  protection levels        : %s" % dict(prot))
    p("  component types present:")
    for k in sorted(comp_pkgs):
        p("     %s" % k)
    p("  task types present:")
    for k in sorted(task_pkgs):
        p("     %s" % k)
    p("")
    p("=== SCRIPTS ===")
    p("  script units             : %d" % st["scripts"])
    p("  authored file instances  : %d" % sum(len(v) for v in bodies.values()))
    p("  DISTINCT bodies          : %d   <-- real port list" % len(bodies))
    p("  authored LOC by language : %s  TOTAL=%d" % (dict(langs), sum(langs.values())))
    p("  distinct-body LOC        : %d" % sum(v[0][3] for v in bodies.values()))
    p("  SOURCE MISSING           : %d%s"
      % (len(nosrc), "   <-- SHOWSTOPPER" if nosrc else ""))
    for x in nosrc[:10]:
        p("      %s" % x)
    if odd:
        p("  UNEXPECTED SourceCode layout: %d" % len(odd))
        for x in odd[:5]:
            p("      %s" % x)
    dups = [(h, v) for h, v in bodies.items() if len(v) > 1]
    p("  bodies duplicated        : %d" % len(dups))
    for h, v in sorted(dups, key=lambda x: -len(x[1]))[:6]:
        p("      x%-4d %3d loc  %s / %s" % (len(v), v[0][3], v[0][1], v[0][2]))
    if flows:
        ns = sorted(n for n, _, _ in flows)
        p("")
        p("  components/flow  median=%d  p90=%d  max=%d"
          % (ns[len(ns) // 2], ns[int(len(ns) * 0.9)], ns[-1]))
        for n, pk, nm in sorted(flows, reverse=True)[:6]:
            p("     %3d  %s / %s" % (n, pk, nm))
    p("")
    p("  csv -> %s     script source -> %s/" % (a.csv, a.scripts))
    p("")


if __name__ == "__main__":
    main()
