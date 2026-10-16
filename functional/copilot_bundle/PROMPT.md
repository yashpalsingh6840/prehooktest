# DI Functional Baseline — one-shot Copilot prompt

Paste this whole file into GitHub Copilot Chat (Agent mode, terminal + file
access enabled) at the root of the target .NET repo. Fill in the three paths
under CONFIG first.

Everything mechanical runs as scripts (zero model reasoning). You only spend
model credits analyzing the actual business logic for each entry point.

---

## CONFIG — set these, then run

- REPO_PATH = `.`                         (the .NET repo you are documenting)
- SSIS_META_ROOT = `D:\SSIS_Out\svk\walkthrough`   (set to "" if this repo has no SSIS-triggered batch jobs)
- BASELINE_DOCX = `DI_Repository_Functional_Baseline_Sample_n600-ProdProc.docx`
- BUNDLE_DIR = `copilot_bundle`             (folder containing discover.py, merge_to_docx.py, schemas.json)
- FACTS_DIR = `{BUNDLE_DIR}/facts`          (scratch folder for generated facts JSON - never the repo root)

Before Step 1, create `{FACTS_DIR}` if it does not exist, and write every
`discovery.json` / `facts_EP-i.json` / `facts_overview.json` there, not at
the repo root. These are intermediate working files, not deliverables — keep
them out of the repo root so they don't clutter `git status` or get mistaken
for source files. If this repo has a `.gitignore`, add a line for
`{FACTS_DIR}/` if one isn't already present.

## Step 1 — Discover (script only, do not reason about repo structure yourself)

Run:
```
python {BUNDLE_DIR}/discover.py --repo {REPO_PATH} --ssis-meta "{SSIS_META_ROOT}" --out {FACTS_DIR}/discovery.json
```
Read the resulting `{FACTS_DIR}/discovery.json`. It lists every entry point already
classified (WebAPI / WCF / BatchJob), the exact evidence files for each one,
whether a Docs/ folder exists, and — for batch jobs that trigger SSIS
packages — the matched `*.walkthrough.md` / `*.claims.json` files.

Do not scan the repo yourself beyond what `discovery.json` names. If an entry
point's evidence list looks incomplete, say so in the output as
`SME Confirmation Required` rather than widening your own search.

## Step 2 — For EACH entry point in discovery.json, do this loop

For entry point `EP-i`:

1. **Check evidence priority, in this order:**
   - If `docs_folder.found` is true and any listed doc plausibly covers this
     entry point, read it first and treat it as the primary source. Only
     fall back to code for what the doc does not answer.
   - If the entry point is `"type": "BatchJob"` and has `ssis_packages`, read
     the matched `walkthrough_md` and `claims_json` files. **Do not re-parse
     the underlying .dtsx — that file already contains the extracted
     control-flow, data-flow, SQL, and column lineage.** Your job is to
     translate it into business-functional language and carry its evidence
     citations forward (cite the walkthrough file + component name).
   - Otherwise, read only the files listed in `evidence_files` for this
     entry point, plus one hop of their direct dependencies if needed to
     resolve a call (service class, repository, DTO). Do not browse beyond
     that without a reason you can state.

2. **Produce exactly one JSON file**, `{FACTS_DIR}/facts_EP-i.json`, shaped like this
   (only include sections you actually have evidence for — omit the rest,
   do not pad with empty rows):

```json
{
  "sections": {
    "3": [ { "Function ID": "AUTO", "Functionality / Capability": "...", "Description from supplied material": "...", "Priority": "...", "Status": "..." } ],
    "4": [ { "UC ID": "AUTO", "Important Use Case": "...", "Business Purpose": "...", "Priority": "...", "Preconditions": "...", "Input": "...", "Expected Output": "...", "Dependencies": "...", "Sample Data": "...", "Sign-off?": "...", "Evidence / Status": "file.cs:42" } ],
    "5": [ { "Use Case": "UC-xx", "Input": "...", "Validation / Rule": "...", "Processing": "...", "Expected Output": "...", "Negative / Error": "...", "Evidence": "file.cs:10-20" } ],
    "6": [ { "Dependency": "...", "Operations": "...", "Type": "...", "Direction": "...", "Purpose": "...", "Interface / Protocol": "...", "Owner": "...", "Criticality": "...", "Related Use Case": "UC-xx", "Migration Impact / Notes": "..." } ],
    "7": [ { "Use Case": "UC-xx", "Data Type": "...", "Sample / Reference": "...", "Expected Result": "...", "Data Source / Owner": "...", "Sensitive?": "...", "Status": "Test Evidence Required" } ],
    "8": [ { "API ID": "AUTO", "Method": "GET|POST|...", "Endpoint": "...", "Purpose": "...", "Related Use Case": "UC-xx", "Request/Input": "...", "Expected Response": "...", "Auth/Headers": "...", "Postman Reference": "..." } ],
    "9": [ { "Rule ID": "AUTO", "Rule": "...", "Applies To": "...", "Expected Behaviour": "...", "Source / SME Confirmation": "file.cs:30 | SME Confirmation Required" } ],
    "10": [ { "Scenario": "...", "Related Use Case": "UC-xx", "Trigger/Input": "...", "Expected Error/Response": "...", "Business Impact": "...", "Validation Evidence": "..." } ],
    "11": [ { "Area": "...", "As-Is": "...", "Target": "Architecture Decision Required", "Functional Impact": "...", "Validation Approach": "...", "Approved Deviation?": "No" } ],
    "12": [ { "Test ID": "AUTO", "Related Use Case": "UC-xx", "Scenario": "...", "Input/Data": "...", "Expected Result": "...", "Priority": "...", "Status": "Proposed - Test Evidence Required" } ],
    "13": [ { "ID": "AUTO", "Question / Gap": "...", "Owner": "SME", "Status": "Open", "Impact": "..." } ]
  }
}
```

**Rules while filling this JSON (non-negotiable):**
- Never invent an endpoint, rule, dependency, or sample value. If it isn't in
  the evidence you read, write `SME Confirmation Required`,
  `Architecture Decision Required`, or `Test Evidence Required` instead.
- Every row needs an `Evidence` / citation value pointing at a real
  `relative/path.cs:line` or the walkthrough file name + component, unless
  the row itself is a confirmation/decision/evidence gap marker.
- Use `"AUTO"` for every ID field — the merge script assigns real numbers,
  do not invent `UC-07` etc. yourself.
- Keep each entry point's JSON scoped to what that entry point itself
  contributes. Shared dependencies (e.g. a common auth library used by three
  entry points) only need one row total — check `discovery.json`/already
  merged doc before adding a duplicate.

3. **Merge immediately, then move to the next entry point** (do not batch all
   entry points into memory first):
```
python {BUNDLE_DIR}/merge_to_docx.py --doc {BASELINE_DOCX} --facts {FACTS_DIR}/facts_EP-i.json --schemas {BUNDLE_DIR}/schemas.json
```

## Step 3 — Document control / overview (once, after the loop)

Using the overall facts gathered across entry points (not a fresh repo scan),
write `{FACTS_DIR}/facts_overview.json`:
```json
{
  "document_control": { "Repository Name": "...", "Migration Stream": "...", "Version": "..." },
  "functional_overview": [ { "Item": "...", "Current Understanding": "...", "Validation Status": "..." } ]
}
```
Merge it the same way:
```
python {BUNDLE_DIR}/merge_to_docx.py --doc {BASELINE_DOCX} --facts {FACTS_DIR}/facts_overview.json --schemas {BUNDLE_DIR}/schemas.json
```

## Step 4 — Report back (short, do not restate the whole document)

Leave `{FACTS_DIR}` in place (it is useful as an audit trail of exactly what
evidence produced each row) unless the user asks you to delete it. Do not
move or copy anything out of `{FACTS_DIR}` into the repo root.

List: entry points analyzed, which used Docs-folder evidence vs. code vs.
SSIS walkthroughs, how many rows were written per section, and the count of
`SME Confirmation Required` / `Architecture Decision Required` /
`Test Evidence Required` markers left outstanding. Do not mark anything as
signed off.
