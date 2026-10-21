# Endpoint scan toolkit

Three files, keep them in one folder:

| File | Purpose |
|---|---|
| `EndpointScan.Common.ps1` | Shared helpers (dot-sourced; don't run directly) |
| `Get-EndpointInventory.ps1` | **Step 1** - build the de-duplicated list of WCF contracts and REST routes |
| `Find-EndpointUsage.ps1` | **Step 2** - find consumers of those endpoints, with duplicate-aware attribution |

Works on Windows PowerShell 5.1 and PowerShell 7+. No modules to install. Git is optional (used for last-commit dates as an ownership tie-breaker; `-SkipGit` turns it off).

## Run

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

## Known limits (read before trusting a "no consumers" result)

- Parsing is regex-based (comments/strings are masked, braces matched), not Roslyn. Not covered: routes inherited from a base controller, constants used in `[Route(Routes.X)]`, conventional MVC routing without attributes, VB/F#.
- Static analysis can't see third parties, hand-built URLs from config/DB, or callers outside these repos. For anything marked `NoStaticConsumerFound-checkRuntimeLogs`, confirm with gateway/IIS logs before decommissioning.
- Only the checked-out branch of each repo is scanned.
