#!/usr/bin/env python3
"""
Stage 0 discovery for the DI Functional Baseline bundle.

Pure scan, no LLM calls. Scopes every later Copilot prompt to an exact,
small set of files instead of letting the model "explore the repo" at
model-reasoning cost.

Usage:
    python discover.py --repo <path-to-dotnet-repo> [--ssis-meta <path-to-svk-walkthrough-output>] [--out discovery.json]

What it finds:
  - a Docs/ or docs/ folder (existing functional docs rank above code-tracing)
  - .csproj projects, classified as WebAPI / WCF / BatchJob / Unknown
  - for BatchJob projects, any .dtsx / dtexec / ExecutePackageTask references,
    matched by package name against a folder of svk-style walkthrough output
    (*.walkthrough.md + claims/*.claims.json)
"""
import argparse
import json
import re
from pathlib import Path

WEBAPI_HINTS = re.compile(r"ApiController|ControllerBase|\[Route\(|Microsoft\.AspNet(Core)?\.Mvc|System\.Web\.Http", re.I)
WCF_HINTS = re.compile(r"\[ServiceContract\]|\[OperationContract\]|System\.ServiceModel", re.I)
SSIS_REF = re.compile(r"([A-Za-z0-9_]+)\.dtsx|dtexec[^\n]*?/f(?:ile)?\s+\"?([^\"\s]+\.dtsx)\"?|ExecutePackageTask", re.I)
EXE_HINT = re.compile(r"<OutputType>\s*Exe\s*</OutputType>", re.I)


def find_docs_folder(repo: Path):
    for name in ("Docs", "docs", "Documentation", "documentation"):
        p = repo / name
        if p.is_dir():
            files = [str(f.relative_to(repo)) for f in p.rglob("*") if f.is_file()]
            return {"found": True, "path": str(p.relative_to(repo)), "files": files}
    return {"found": False, "path": None, "files": []}


def classify_project(csproj: Path):
    """Return (type, evidence_files) for one .csproj by scanning its own source tree."""
    proj_dir = csproj.parent
    cs_files = list(proj_dir.rglob("*.cs"))
    is_exe = EXE_HINT.search(csproj.read_text(errors="ignore")) is not None

    webapi_hits, wcf_hits, ssis_hits = [], [], []
    for f in cs_files:
        try:
            text = f.read_text(errors="ignore")
        except OSError:
            continue
        if WEBAPI_HINTS.search(text):
            webapi_hits.append(str(f))
        if WCF_HINTS.search(text):
            wcf_hits.append(str(f))
        if SSIS_REF.search(text):
            ssis_hits.append((str(f), text))

    svc_files = [str(f) for f in proj_dir.rglob("*.svc")]

    if webapi_hits:
        return "WebAPI", webapi_hits[:25]
    if wcf_hits or svc_files:
        return "WCF", (wcf_hits + svc_files)[:25]
    if is_exe and (ssis_hits or True):
        kind = "BatchJob"
        return kind, [h[0] for h in ssis_hits][:25] or [str(f) for f in cs_files[:10]]
    return "Unknown", [str(f) for f in cs_files[:10]]


def extract_ssis_package_names(evidence_files):
    names = set()
    for f in evidence_files:
        try:
            text = Path(f).read_text(errors="ignore")
        except OSError:
            continue
        for m in SSIS_REF.finditer(text):
            for g in m.groups():
                if g:
                    names.add(Path(g).stem)
    return sorted(names)


def match_ssis_metadata(package_names, ssis_meta_root: Path):
    matches = []
    if not ssis_meta_root or not ssis_meta_root.is_dir():
        return matches
    walkthroughs = {p.name.replace(".walkthrough.md", ""): p for p in ssis_meta_root.glob("*.walkthrough.md")}
    claims_dir = ssis_meta_root / "claims"
    claims = {p.name.replace(".claims.json", ""): p for p in claims_dir.glob("*.claims.json")} if claims_dir.is_dir() else {}

    for name in package_names:
        wt = walkthroughs.get(name)
        cl = claims.get(name)
        if wt or cl:
            matches.append({
                "package_name": name,
                "walkthrough_md": str(wt) if wt else None,
                "claims_json": str(cl) if cl else None,
            })
        else:
            matches.append({
                "package_name": name,
                "walkthrough_md": None,
                "claims_json": None,
                "note": "No matching svk walkthrough/claims found for this package name - mark as Test Evidence Required / SME Confirmation Required in the output.",
            })
    return matches


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", required=True, help="Path to the .NET repository root")
    ap.add_argument("--ssis-meta", default=None, help="Path to svk walkthrough output root (contains *.walkthrough.md and claims/)")
    ap.add_argument("--out", default="discovery.json")
    args = ap.parse_args()

    repo = Path(args.repo).resolve()
    ssis_meta_root = Path(args.ssis_meta).resolve() if args.ssis_meta else None

    docs = find_docs_folder(repo)

    entry_points = []
    ep_counter = 0
    for csproj in repo.rglob("*.csproj"):
        ptype, evidence = classify_project(csproj)
        if ptype == "Unknown" and not evidence:
            continue
        ep_counter += 1
        ep = {
            "id": f"EP-{ep_counter}",
            "type": ptype,
            "project_file": str(csproj.relative_to(repo)),
            "evidence_files": [str(Path(e).relative_to(repo)) if Path(e).is_absolute() else e for e in evidence],
        }
        if ptype == "BatchJob":
            pkg_names = extract_ssis_package_names(evidence)
            if pkg_names:
                ep["ssis_packages"] = match_ssis_metadata(pkg_names, ssis_meta_root)
        entry_points.append(ep)

    result = {
        "repo_path": str(repo),
        "ssis_meta_root": str(ssis_meta_root) if ssis_meta_root else None,
        "docs_folder": docs,
        "entry_points": entry_points,
    }

    Path(args.out).write_text(json.dumps(result, indent=2))
    print(f"Wrote {args.out}: {len(entry_points)} entry point(s), docs_folder found={docs['found']}")


if __name__ == "__main__":
    main()
