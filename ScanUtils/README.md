# Endpoint scan toolkit

Five files, keep them in one folder:

| File | Purpose |
|---|---|
| `EndpointScan.Common.ps1` | Shared helpers (dot-sourced; don't run directly) |
| `Get-EndpointInventory.ps1` | **Step 1** - build the de-duplicated list of WCF contracts and REST routes |
| `Find-EndpointUsage.ps1` | **Step 2** - find consumers of those endpoints, with duplicate-aware attribution |
| `Find-ServiceConsumers.ps1` | **Shortcut** - give a service name (`OrderService.svc`, `IOrderService.cs`), it discovers the contract and runs steps 1 + 2 for just that service |
| `Find-ServiceConsumers.GitHub.ps1` | **Same, but straight from GitHub** - no cloning of the 100 repos; downloads only what is needed |

Works on Windows PowerShell 5.1 and PowerShell 7+. No modules to install. Git is optional (used for last-commit dates as an ownership tie-breaker; `-SkipGit` turns it off).

## Shortcut: start from a service name

```powershell
.\Find-ServiceConsumers.ps1 -RootPath D:\repos -Service OrderService.svc
.\Find-ServiceConsumers.ps1 -RootPath D:\repos -Service OrderService.svc,IOrderService.cs -InRepo Orders.Service -HostMapPath .\hosts.csv
.\Find-ServiceConsumers.ps1 -RootPath D:\repos -Service IOrderService -DiscoverOnly     # show what it found, don't search
```

- `-Service` takes a comma-separated list of names, file names or **full paths** (mixed is fine), and/or `-ServiceList services.txt` (one per line, `#` comments allowed).
- A path also pins the repo: `D:\repos\Orders.Service\Web\OrderService.svc` only matches endpoints hosted/defined in repo `Orders.Service`, which resolves same-name collisions without `-InRepo`. Paths from another machine work too if a folder name matches a scanned repo.
- Inputs that matched nothing are listed; `input-resolution.csv` in `-WorkPath` shows which input matched which endpoint.
- `X.svc` matches `.svc` files only, `X.cs` matches source files only, a bare name matches svc file, contract, interface, implementing class, generated client, REST controller.
- The repo scan is cached in `-WorkPath` (default `.\service-scan`); add `-Rescan` after pulling new code.
- If the name matches several unrelated contracts, all are searched and reported separately; `-InRepo <repo folder name>` narrows to the one hosted/defined there.

## Remote mode: GitHub org/user, nothing cloned

```powershell
$env:GITHUB_TOKEN = '<token>'     # read access to the org's repos; code search requires auth
.\Find-ServiceConsumers.GitHub.ps1 -GitHubUrl https://github.com/contoso -Service OrderService.svc
.\Find-ServiceConsumers.GitHub.ps1 -GitHubUrl https://github.com/contoso -Service https://github.com/contoso/Users.Api/blob/main/Controllers/UserController.cs -HostMapPath .\hosts.csv
.\Find-ServiceConsumers.GitHub.ps1 -GitHubUrl https://git.contoso.com/platform -Service OrderService.svc   # GitHub Enterprise Server
```

What it downloads: (1) the 1-5 repos that define/host the service, as source tarballs (no history); (2) for every other repo
only the files GitHub code search says mention the service, plus that repo's `*.csproj`. Then the normal local analysis runs
on that small mirror. Everything downloaded is listed in `fetch-log.csv`; the mirror is in `<WorkPath>\mirror`.

Limits come from GitHub code search: default branch only, files < 384 KB, max 1000 results per query, 10 searches/minute
(the script waits between calls - expect roughly 7 s per search term; `-SearchDelaySec`), most forks are not indexed,
and the index can lag very recent commits. Terms that hit the 1000-result cap are reported. Caps: `-MaxFullRepos` (8),
`-MaxFullRepoSizeMB` (300), `-MaxFilesPerRepo` (150). The token is only sent to the API host and is never written to disk.

## Run (full control, step by step)

```powershell
# 0. Clone / pull every repo under one folder (default branch is what gets scanned)

# 1. Inventory
.\Get-EndpointInventory.ps1 -RootPath D:\repos -OutputPath D:\scan\out

# 2. Review D:\scan\out\endpoint-clusters.csv  (this is your migration list; fix/ignore rows if needed)

# 3. Usage - everything, or only the endpoints owned by the repos you are migrating
.\Find-EndpointUsage.ps1 -RootPath D:\repos -InventoryPath D:\scan\out
.\Find-EndpointUsage.ps1 -RootPath D:\repos -InventoryPath D:\scan\out -OwnerRepo Orders.Service,Orders.Api -HostMapPath .\hosts.csv
```

`hosts.csv` (optional, strongly recommended for REST):

```
Repo,Host
Orders.Api,orders.contoso.com
Orders.Service,orders.contoso.com;orders-internal.contoso.local
```

## Step 1 outputs

| File | What it is |
|---|---|
| `endpoint-clusters.csv` | One row per unique endpoint. `Owner`/`OwnerConfidence`, `HostRepos`, `DefinitionRepos`, `ProxyRepos` (already-known consumers), `Flags` |
| `endpoint-inventory.csv` | One row per occurrence x operation (every repo that declares, hosts or proxies it) |
| `repo-references.csv` | Package/project/assembly references per repo |
| `repos.csv` | Repos actually scanned (duplicate clones removed) |
| `scan-warnings.csv` | Ambiguous / unparsable items to look at by hand |

Flags: `MultiHost`, `DefinedInMultipleRepos`, `NameCollision`, `ContractDrift`, `NoHostFound`, `DuplicateRoute`, `ConventionRoute`/`ConventionVerb`.

## Step 2 outputs

| File | What it is |
|---|---|
| `cluster-usage-rollup.csv` | Per endpoint: consumers (High/Medium vs Low), operations seen used / not seen, review hints |
| `consumer-summary.csv` | Endpoint x consumer repo with best confidence and evidence types |
| `usage-detail.csv` | Every hit: file, line, snippet, how it was resolved, candidate clusters if ambiguous |
| `host-references.csv` | Repos that mention a host from `hosts.csv` |

Confidence: **High** = contract/route unambiguously tied to the endpoint; **Medium** = likely; **Low** = name-only / prefix-only / ambiguous - review.

## Repo discovery

Every folder under `-RootPath` that contains a `.git` is a repo. First-level folders with no `.git` (exported/zipped sources) are also treated as repos, with a console note. Duplicate clones of the same git remote are scanned once.

## Known limits (read before trusting a "no consumers" result)

- Parsing is regex-based (comments/strings are masked, braces matched), not Roslyn. Not covered: routes inherited from a base controller, constants used in `[Route(Routes.X)]`, conventional MVC routing without attributes, VB/F#.
- Static analysis can't see third parties, hand-built URLs from config/DB, or callers outside these repos. For anything marked `NoStaticConsumerFound-checkRuntimeLogs`, confirm with gateway/IIS logs before decommissioning.
- If a service's interface lives in a package whose source is not among the scanned repos, the service is still found from its `.svc`/implementing class (cluster flagged `ContractNotInScannedRepos`); the contract's operations are filled in only if a consumer's generated proxy reveals them.
- Only the checked-out branch of each repo is scanned.
