// Summarizes a real `ssisx extract` spec.json (Ssis.Extract.Model.PackageSpec)
// into a safe, UI-friendly projection: connection managers (redacted fields
// only -- never UnredactedConnectionString/EncryptedProperties), and every
// Data Flow Task's own components + edges, derived defensively since a real
// portfolio will contain component types not specifically coded for here.

type Json = Record<string, any>;

export type ComponentRole = "Source" | "Destination" | "Transform" | "Other";

export type PackageComponent = {
  name: string;
  type: string;
  role: ComponentRole;
  detail: string;
  connection: string | null;
  inputs: string[];
  outputs: string[];
  payload: unknown;
};

export type PackageDataFlow = {
  taskName: string;
  components: PackageComponent[];
  edges: { from: string; to: string }[];
};

export type PackageConnectionManager = {
  name: string;
  type: string;
  server: string | null;
  database: string | null;
  provider: string | null;
  filePath: string | null;
};

export type PackageSummary = {
  package: string;
  connectionManagers: PackageConnectionManager[];
  dataFlows: PackageDataFlow[];
  roleCounts: Record<string, number>;
};

// Component-class payload fields known to carry a dedicated shape, in the
// order Microsoft.SqlServer... fields appear on a real PipelineComponentSpec.
const SOURCE_PAYLOAD_KEYS = ["OleDbSource", "AdoNetSource", "ExcelSource", "XmlSource"];
const DESTINATION_PAYLOAD_KEYS = ["OleDbDestination", "AdoNetDestination", "FlatFileDestination"];
const OTHER_TRANSFORM_PAYLOAD_KEYS = [
  "MergeJoin",
  "Sort",
  "Aggregate",
  "RowCount",
  "PctSampling",
  "CopyMap",
  "DataConvert",
  "OleDbCommand",
  "Scd",
  "ScriptComponent",
];

function stripTypeName(raw: string | undefined | null): string {
  if (!raw) return "Unknown";
  return raw.replace(/^Microsoft\./, "").replace(/^STOCK:/, "");
}

/** Extracts the component name from a path endpoint like
 *  `Package\DFT_X\CompName.Outputs[OutName]` or `...Inputs[InName]`. */
function componentNameFromPathEndpoint(endpoint: string | undefined | null): string | null {
  if (!endpoint) return null;
  const m = /\\([^\\]+)\.(?:Outputs|Inputs)\[/.exec(endpoint);
  return m ? m[1] : null;
}

function namesOf(list: unknown): string[] {
  if (!Array.isArray(list)) return [];
  return list.map((x) => x?.Name).filter((n): n is string => typeof n === "string");
}

function countDerivedColumns(component: Json): number {
  try {
    let count = 0;
    for (const output of component.Outputs ?? []) {
      for (const col of output?.Columns ?? []) {
        if (col?.Expression || col?.FriendlyExpression) count++;
      }
    }
    return count;
  } catch {
    return 0;
  }
}

function deriveRoleAndDetail(
  component: Json,
  connectionManagersByName: Map<string, PackageConnectionManager>,
): { role: ComponentRole; detail: string; connection: string | null; payload: unknown } {
  for (const key of SOURCE_PAYLOAD_KEYS) {
    const payload = component?.[key];
    if (payload) {
      const connection = payload.ConnectionName ?? null;
      let detail: string;
      if (payload.SqlCommand) detail = "Reads via SQL query";
      else if (payload.OpenRowset) detail = `Reads table ${payload.OpenRowset}`;
      else detail = "Reads from source";
      return { role: "Source", detail, connection, payload };
    }
  }

  for (const key of DESTINATION_PAYLOAD_KEYS) {
    const payload = component?.[key];
    if (payload) {
      const connection = payload.ConnectionName ?? null;
      const detail = payload.OpenRowset ? `Writes to ${payload.OpenRowset}` : "Writes to destination";
      return { role: "Destination", detail, connection, payload };
    }
  }

  if (component?.FlatFileSource) {
    const payload = component.FlatFileSource;
    const connection = payload.ConnectionName ?? null;
    const filePath = connection ? connectionManagersByName.get(connection)?.filePath : null;
    const detail = filePath ? `Reads flat file (${filePath})` : "Reads flat file";
    return { role: "Source", detail, connection, payload };
  }

  if (component?.Lookup) {
    const payload = component.Lookup;
    return { role: "Transform", detail: "Looks up reference data", connection: payload.ConnectionName ?? null, payload };
  }

  if (component?.ConditionalSplit) {
    const payload = component.ConditionalSplit;
    const branches = (Array.isArray(payload.Cases) ? payload.Cases.length : 0) + 1;
    return { role: "Transform", detail: `Routes into ${branches} branch(es)`, connection: null, payload };
  }

  for (const key of OTHER_TRANSFORM_PAYLOAD_KEYS) {
    const payload = component?.[key];
    if (payload) {
      return {
        role: "Transform",
        detail: stripTypeName(key),
        connection: payload.ConnectionName ?? null,
        payload,
      };
    }
  }

  if (component?.ComponentClassId === "Microsoft.DerivedColumn") {
    const n = countDerivedColumns(component);
    return {
      role: "Transform",
      detail: n > 0 ? `Computes ${n} column(s)` : "Computes column(s)",
      connection: null,
      payload: null,
    };
  }

  return {
    role: "Other",
    detail: stripTypeName(component?.ComponentClassId ?? component?.RawComponentClassId),
    connection: null,
    payload: null,
  };
}

function summarizeConnectionManagers(spec: Json): PackageConnectionManager[] {
  if (!Array.isArray(spec?.ConnectionManagers)) return [];
  return spec.ConnectionManagers.map((cm: Json) => ({
    name: cm?.ObjectName ?? "(unnamed)",
    type: cm?.CreationName ?? "Unknown",
    server: cm?.Parsed?.Server ?? null,
    database: cm?.Parsed?.Database ?? null,
    provider: cm?.Parsed?.Provider ?? null,
    filePath: cm?.Parsed?.FilePath ?? null,
  }));
}

function walkDataFlows(
  executables: unknown,
  connectionManagersByName: Map<string, PackageConnectionManager>,
  out: PackageDataFlow[],
) {
  if (!Array.isArray(executables)) return;
  for (const exe of executables) {
    try {
      const pipeline = exe?.DataFlowTask?.Pipeline;
      if (pipeline && Array.isArray(pipeline.Components)) {
        const components: PackageComponent[] = pipeline.Components.map((c: Json) => {
          const { role, detail, connection, payload } = deriveRoleAndDetail(c, connectionManagersByName);
          return {
            name: c?.Name ?? "(unnamed)",
            type: stripTypeName(c?.ComponentClassId ?? c?.RawComponentClassId),
            role,
            detail,
            connection,
            inputs: namesOf(c?.Inputs),
            outputs: namesOf(c?.Outputs),
            payload,
          };
        });

        const edges = (Array.isArray(pipeline.Paths) ? pipeline.Paths : [])
          .map((p: Json) => ({
            from: componentNameFromPathEndpoint(p?.StartId),
            to: componentNameFromPathEndpoint(p?.EndId),
          }))
          .filter(
            (e: { from: string | null; to: string | null }): e is { from: string; to: string } =>
              !!e.from && !!e.to,
          );

        out.push({ taskName: exe?.ObjectName ?? "(unnamed)", components, edges });
      }
    } catch {
      // A malformed executable must never break the whole summary.
    }

    if (Array.isArray(exe?.Children)) {
      walkDataFlows(exe.Children, connectionManagersByName, out);
    }
  }
}

export function summarizePackageSpec(spec: Json): PackageSummary {
  const connectionManagers = summarizeConnectionManagers(spec);
  const connectionManagersByName = new Map(connectionManagers.map((cm) => [cm.name, cm]));

  const dataFlows: PackageDataFlow[] = [];
  try {
    walkDataFlows(spec?.Executables, connectionManagersByName, dataFlows);
  } catch {
    // dataFlows stays whatever was collected before the failure.
  }

  const roleCounts: Record<string, number> = { Source: 0, Transform: 0, Destination: 0, Other: 0 };
  for (const flow of dataFlows) {
    for (const c of flow.components) {
      roleCounts[c.role] = (roleCounts[c.role] ?? 0) + 1;
    }
  }

  return {
    package: spec?.ObjectName ?? "(unknown)",
    connectionManagers,
    dataFlows,
    roleCounts,
  };
}
