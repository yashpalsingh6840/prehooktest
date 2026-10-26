# Service usage toolkit

Two commands for the normal workflow. Keep all files in one folder; needs Windows PowerShell 5.1 or PowerShell 7+ and git.

## 1. Clone everything

```powershell
$env:GITHUB_TOKEN = '<token>'        # only needed for private repos / to avoid API limits
.\Clone-GitHubRepos.ps1 -BaseUrl https://github.com/contoso -TargetPath C:\Proj
```

Lists all repos of the org/user through the API and clones them (parallel on PowerShell 7). Shallow, single-branch (latest code only),
archived repos and forks skipped, existing clones left alone. Prints one summary line plus any failures.

| Option | Meaning |
|---|---|
| `-Update` | refresh existing clones (repos with local changes are never touched) |
| `-IncludeArchived` / `-IncludeForks` | include them |
| `-Include` / `-Exclude` | wildcards on repo names |
| `-Depth 0` | full history |
| `-ListOnly` | just list what would be cloned |

Token: fine-grained = Contents *Read-only* on the repos (public repos need no permissions); classic = `repo`. The token is passed to git through the environment only; it is never written to `.git/config`.
GitHub Enterprise Server works (`-BaseUrl https://git.contoso.com/platform`).

## 2. Find the usage

```powershell
# services of ONE repo (the usual case): name the repo once, list the services
.\Find-ServiceUsage.ps1 -HomeRepoPath C:\Proj\Orders.Api -Service OrderController.cs,EmployeeController.cs,OrderService.svc

# ... or leave -Service out: every SOAP service and controller in that repo is searched
.\Find-ServiceUsage.ps1 -HomeRepoPath C:\Proj\Orders.Api

# services from any repos (names must then be unambiguous; or write Name@Repo)
.\Find-ServiceUsage.ps1 -RootPath C:\Proj -Service OrderService.svc,InventoryService.svc,EmployeeController.cs,OrderController.cs
.\Find-ServiceUsage.ps1 -RootPath C:\Proj -ServiceList .\services.txt -OutputPath C:\reports -HostMapPath .\hosts.csv
```

`-HomeRepoPath` = full path of the repo that hosts the services. It must be one of the cloned repos (inside `-RootPath`, which defaults to its parent folder);
a sub-folder of a repo means that repo. All names in `-Service` are taken from it, same-named services in other repos are ignored, and the search covers the home repo
itself (its own frontend) and every other repo.

Input: names, file names or full paths (comma separated and/or a list file, one per line). `X.svc` / `X.cs` match files of that type,
a bare name also matches interface / class / controller names.

**Output: exactly two files** in `-OutputPath` (default: current folder):

| File | Content |
|---|---|
| `service-usage.csv` | one row per service x consuming repo: HostedBy, Confidence, EndpointsUsed (SOAP operations / REST verb+route), Evidence, Example (file:line) |
| `service-summary.csv` | one row per service: HostedBy, Contract, Endpoints, Consumers, ConsumerRepos, EndpointsNotSeenUsed, Notes; plus a row for every input that matched nothing |

Only **High/Medium** confidence consumers are listed. Weak candidates are hidden and counted in Notes (`-IncludeLow` shows them).
`-Detail` adds `service-usage-evidence.csv` (file, line, code snippet per consumer).

Working files live in a cache outside your repo folder and outside the output folder (`%LOCALAPPDATA%\endpoint-scan`).
It is reused between runs and refreshed automatically when any repo's HEAD changed. `-Rescan` forces a refresh, `-NoCache` deletes it afterwards.

`hosts.csv` (optional, recommended): tells the tool which host name belongs to which hosting repo. It confirms REST/SOAP matches and separates
identical routes/interface names in *different* services.

```
Repo,Host
Orders.Api,orders.contoso.com
Orders.Service,orders.contoso.com;orders-internal.contoso.local
```

### Home repo, duplicates and usage inside the home repo

* Routes / operations are read from the code of the home repo - nothing has to be supplied by hand. With **`-HomeRepoPath`** the metadata comes only from that repo.
* Without `-HomeRepoPath`, the hosting repo is discovered automatically. If the same name exists in several repos (e.g. `OrderController.cs` in two repos) each is reported as its own
  service with its own `HostedBy`, and a note says so. Pick one with **`Name@Repo`** (`OrderController.cs@Orders.Api`), a full path, or `-InRepo <repo folder name>`.
* Usage **inside the home repo** is reported too (it is labelled `Orders.Api (home)`), because a frontend living next to the API is a real consumer. `-ExcludeHome` leaves it out.
  The controller's own attribute/route lines, tests, `swagger.json`/`openapi.*`, `launchSettings.json`, markdown docs and build output (`dist`, `node_modules`, `*.min.js`) are never counted.
  For SOAP, inside the home repo only client-side use counts (own generated proxy, `<client>` endpoint config); the implementation, host config and wiring are not consumers.
* Frontend code is scanned: `.js .jsx .ts .tsx .vue .svelte .html .cshtml .razor .aspx` - Angular `http.get<T>(\`/api/x/${id}\`)`, axios/fetch/jQuery, NSwag clients, `"/api/x/" + id`.
  The HTTP verb is taken from the surrounding lines (`axios.delete`, `method: 'POST'`, `type: 'DELETE'`, `HttpMethod.Put`, `PostAsJsonAsync`, Refit attributes ...).

## How matching works (short)

* SOAP: contracts are identified by SOAP namespace + operation set (not by C# name); the host is the repo with the `.svc` / `<service>` / `ServiceHost` / implementing class.
  Consumers: generated proxies, `<client><endpoint>` config, `ChannelFactory<T>` / client classes (attributed to the right contract via local declaration or package reference), `.svc` URLs, operation calls.
* REST: controller actions (attribute routes, `[controller]` tokens, Web API 2 conventions, Minimal APIs) -> routes; consumers are URL string literals matched segment-wise,
  with the HTTP verb read from the surrounding code and host names checked when `hosts.csv` is given.

## Known limits - read before trusting "no consumers"

* Static analysis only: callers outside the scanned repos, URLs assembled from config/DB at runtime, and third parties are invisible. Confirm "No consumer found" with gateway/IIS logs.
* Regex-based parsing (comments/strings masked), not Roslyn. Routes are read from the code: literal `[Route]`/`[HttpGet]` templates, constants (`[Route(Routes.Employees)]`, concatenations) and `[Route]` inherited from a base controller are resolved when the constant / base class is in the same repo. Not resolved (flagged in Notes): constants from other repos or packages, interpolated/computed routes, actions inherited from a base controller, pure convention routing.
* Only the checked-out (default) branch of each repo is scanned.
* If a service's interface lives in a package whose source isn't among the repos, it is still found via its `.svc` / implementing class; its operation list is complete only if a consumer's generated proxy reveals it.

## Other scripts (the engine; normally not needed)

| Script | Purpose |
|---|---|
| `Get-EndpointInventory.ps1` | scan all repos -> de-duplicated list of SOAP contracts / REST routes |
| `Find-EndpointUsage.ps1` | consumers for the inventory (detailed evidence CSVs) |
| `Find-ServiceConsumers.ps1` | one-shot engine used by `Find-ServiceUsage.ps1`; writes the full set of detailed CSVs |
| `Find-ServiceConsumers.GitHub.ps1` | same without cloning: GitHub code search + sparse download (limits: default branch, files < 384 KB, 1000 results/query, 10 searches/min) |
