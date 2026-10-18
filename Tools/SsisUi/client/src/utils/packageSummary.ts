/** Headline numbers derived from one extracted package spec (spec.json). Read
 * defensively: the JSON is produced by ssisx and the UI only ever needs a
 * handful of fields, so anything missing simply counts as zero or null. */

export type DetailKey =
  | "executables"
  | "dataFlows"
  | "components"
  | "sqlTasks"
  | "coverage"
  | "connections"
  | "variables"
  | "precedence"
  | "parallel"
  | "disabled"
  | "scripts";

/** One line in a stat tile's drill-down: a name, and an optional muted note. */
export interface DetailRow {
  primary: string;
  secondary?: string;
}

export interface PackageSummary {
  name: string | null;
  description: string | null;
  formatVersion: string | null;
  productVersion: string | null;
  coveragePercent: number | null;
  /** The XML elements behind the coverage percentage (see spec-schema.md). */
  coverageTotal: number | null;
  coverageExcluded: number | null;
  coverageUnmapped: number | null;
  /** Every executable in the control-flow tree, containers included. */
  executables: number;
  containers: number;
  dataFlows: number;
  sqlTasks: number;
  disabled: number;
  /** Script Tasks plus Script Components -- custom code the generator can't translate. */
  scripts: number;
  precedenceConstraints: number;
  /** Waves where more than one task runs at once (from each container's DAG). */
  parallelWaves: number;
  components: number;
  componentTypes: [string, number][];
  connections: number;
  connectionTypes: [string, number][];
  variables: number;
  parameters: number;
  eventHandlers: number;
  unmapped: number;
  /** Drill-down rows behind each tile, keyed the same way as the tiles. */
  details: Record<DetailKey, DetailRow[]>;
}

type Obj = Record<string, unknown>;

const asObj = (v: unknown): Obj | null =>
  v !== null && typeof v === "object" && !Array.isArray(v) ? (v as Obj) : null;
const asArr = (v: unknown): unknown[] => (Array.isArray(v) ? v : []);
const asStr = (v: unknown): string | null => (typeof v === "string" ? v : null);
const isTrue = (v: unknown) => v === true || v === "True" || v === "true";
const SQL_PREVIEW_LIMIT = 140;

/** "Microsoft.FlatFileSource" -> "FlatFileSource", "STOCK:SEQUENCE" -> "SEQUENCE". */
function shortName(s: string): string {
  return s.replace(/^(Microsoft\.|STOCK:)/, "");
}

/** The raw CreationName for an ADO.NET connection is a full .NET type name, e.g.
 * "ADO.NET:System.Data.SqlClient.SqlConnection, System.Data, Version=...". Keep
 * the provider family only. Other providers (FLATFILE, OLEDB, EXCEL...) are
 * already short and pass through unchanged. */
function shortConnectionType(s: string): string {
  if (!s.startsWith("ADO.NET:")) return s;
  const typeName = s.slice("ADO.NET:".length).split(",")[0];
  const parts = typeName.split(".");
  const cls = parts[parts.length - 1];
  const provider = parts.length > 1 ? parts[parts.length - 2] : null;
  return provider ? `ADO.NET · ${provider}` : `ADO.NET · ${cls}`;
}

function tally(counts: Map<string, number>): [string, number][] {
  return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
}

function preview(sql: string): string {
  const oneLine = sql.replace(/\s+/g, " ").trim();
  return oneLine.length > SQL_PREVIEW_LIMIT ? `${oneLine.slice(0, SQL_PREVIEW_LIMIT)}…` : oneLine;
}

export function summarizePackage(json: unknown): PackageSummary {
  const pkg = asObj(json) ?? {};
  const coverage = asObj(pkg.Coverage);
  const pkgName = asStr(pkg.ObjectName) ?? "Package";

  const details: Record<DetailKey, DetailRow[]> = {
    executables: [],
    dataFlows: [],
    components: [],
    sqlTasks: [],
    coverage: [],
    connections: [],
    variables: [],
    precedence: [],
    parallel: [],
    disabled: [],
    scripts: [],
  };

  const s = {
    executables: 0,
    containers: 0,
    dataFlows: 0,
    sqlTasks: 0,
    disabled: 0,
    scripts: 0,
    precedenceConstraints: 0,
    parallelWaves: 0,
    components: 0,
  };
  const componentCounts = new Map<string, number>();

  // Precedence links and parallel waves for one container (package root or nested).
  // Each container's constraints only order its own direct children, so summing
  // per container never double-counts.
  const countContainer = (owner: Obj, ownerName: string) => {
    const links = asArr(owner.PrecedenceConstraints).length;
    s.precedenceConstraints += links;
    if (links > 0) {
      details.precedence.push({ primary: ownerName, secondary: `${links} link${links === 1 ? "" : "s"}` });
    }
    for (const level of asArr(asObj(owner.Dag)?.ParallelLevels)) {
      const tasks = asArr(level).map((t) => String(t).split("\\").pop() ?? String(t));
      if (tasks.length > 1) {
        s.parallelWaves++;
        details.parallel.push({ primary: ownerName, secondary: tasks.join(", ") });
      }
    }
  };

  countContainer(pkg, pkgName);

  const walk = (nodes: unknown[], parentName: string) => {
    for (const raw of nodes) {
      const e = asObj(raw);
      if (!e) continue;
      const name = asStr(e.ObjectName) ?? asStr(e.RefId) ?? "(unnamed)";
      const children = asArr(e.Children);
      const type = asStr(e.ExecutableType) ?? "";
      s.executables++;
      details.executables.push({
        primary: name,
        secondary: [shortName(type), children.length > 0 ? `${children.length} children` : null]
          .filter(Boolean)
          .join(" · "),
      });

      if (children.length > 0) s.containers++;
      if (isTrue(e.Disabled)) {
        s.disabled++;
        details.disabled.push({ primary: name, secondary: `in ${parentName}` });
      }
      countContainer(e, name);

      if (type === "Microsoft.ScriptTask" || type === "STOCK:ScriptTask") {
        s.scripts++;
        details.scripts.push({ primary: name, secondary: `Script Task · in ${parentName}` });
      }

      if (type === "Microsoft.Pipeline") {
        s.dataFlows++;
        const components = asArr(asObj(asObj(e.DataFlowTask)?.Pipeline)?.Components);
        details.dataFlows.push({ primary: name, secondary: `${components.length} components` });
        for (const c of components) {
          const comp = asObj(c);
          const cls = asStr(comp?.ComponentClassId);
          const key = cls ? shortName(cls) : "unknown";
          s.components++;
          componentCounts.set(key, (componentCounts.get(key) ?? 0) + 1);
          const compName = asStr(comp?.Name) ?? "(unnamed)";
          details.components.push({ primary: compName, secondary: `${key} · ${name}` });
          if (comp?.ScriptComponent != null) {
            s.scripts++;
            details.scripts.push({ primary: compName, secondary: `Script Component · ${name}` });
          }
        }
      } else if (type === "Microsoft.ExecuteSQLTask") {
        s.sqlTasks++;
        const sql = asStr(asObj(e.ExecuteSqlTask)?.SqlStatementSource);
        details.sqlTasks.push({ primary: name, secondary: sql ? preview(sql) : undefined });
      }
      walk(children, name);
    }
  };
  walk(asArr(pkg.Executables), pkgName);

  const connections = asArr(pkg.ConnectionManagers);
  const connectionCounts = new Map<string, number>();
  for (const c of connections) {
    const co = asObj(c);
    const kind = shortConnectionType(asStr(co?.CreationName) ?? "unknown");
    connectionCounts.set(kind, (connectionCounts.get(kind) ?? 0) + 1);
    details.connections.push({
      primary: asStr(co?.ObjectName) ?? "(unnamed)",
      secondary: [kind, asStr(co?.Scope)].filter(Boolean).join(" · "),
    });
  }

  const variables = asArr(pkg.Variables);
  for (const v of variables) {
    const vo = asObj(v);
    const ns = asStr(vo?.Namespace);
    const objName = asStr(vo?.ObjectName) ?? "(unnamed)";
    details.variables.push({
      primary: ns ? `${ns}::${objName}` : objName,
      secondary: vo?.DataType != null ? String(vo.DataType) : undefined,
    });
  }

  for (const u of asArr(pkg.Unmapped)) {
    const uo = asObj(u);
    const label = asStr(uo?.Name) ?? asStr(uo?.Element) ?? JSON.stringify(u).slice(0, 120);
    details.coverage.push({ primary: label });
  }

  return {
    name: asStr(pkg.ObjectName),
    description: asStr(pkg.Description),
    formatVersion: pkg.PackageFormatVersion != null ? String(pkg.PackageFormatVersion) : null,
    productVersion: asStr(pkg.LastModifiedProductVersion),
    coveragePercent: typeof coverage?.CoveragePercent === "number" ? coverage.CoveragePercent : null,
    coverageTotal: typeof coverage?.TotalElements === "number" ? coverage.TotalElements : null,
    coverageExcluded: typeof coverage?.ExcludedElements === "number" ? coverage.ExcludedElements : null,
    coverageUnmapped: typeof coverage?.UnmappedElements === "number" ? coverage.UnmappedElements : null,
    ...s,
    componentTypes: tally(componentCounts),
    connections: connections.length,
    connectionTypes: tally(connectionCounts),
    variables: variables.length,
    parameters: asArr(pkg.Parameters).length,
    eventHandlers: asArr(pkg.EventHandlers).length,
    unmapped: asArr(pkg.Unmapped).length,
    details,
  };
}
