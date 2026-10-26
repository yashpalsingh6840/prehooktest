/** Plain-language explanations shown at the top of each drill-down modal in the
 * package summary: one per category, one per pipeline component type and one per
 * connection type. Keys match what packageSummary.ts produces. */

import type { DetailKey } from "./packageSummary";

export const DETAIL_INFO: Record<DetailKey, string> = {
  executables:
    "The control-flow tasks and containers in this package, in the order SSIS runs them (the Control Flow tab). A container such as a Sequence or ForEach Loop can hold more executables. A Data Flow Task counts as one executable here; its sources, transforms and destinations are listed under Components.",
  dataFlows:
    "Data Flow Tasks: each one moves rows from sources, through transforms, into destinations. Each row shows how many components it contains.",
  components:
    "The steps inside Data Flow Tasks: sources, transformations and destinations (the Data Flow tab). Each row shows the component's type and the Data Flow Task it belongs to.",
  sqlTasks:
    "Execute SQL Tasks, which run a SQL statement against a database. The note shows the start of each statement.",
  coverage:
    "How much of this package's XML the extractor understood. Anything it could not read is listed as unmapped and lowers the score.",
  connections:
    "Connection managers: where the package reads or writes data (databases, files, Excel workbooks). The note shows the provider and the scope.",
  variables:
    "Variables hold values the package uses at run time, such as file paths, counters or dates. The note shows the data type.",
  precedence:
    "Precedence constraints are the links between executables that set their order: success, failure or completion. Each row is one container and how many links it has.",
  parallel:
    "Groups of tasks with no link between them, which SSIS can run at the same time. Each row lists the tasks in one such group.",
  disabled:
    "Executables switched off in SSIS. SSIS skips them when the package runs, so the generated code skips them too.",
  scripts:
    "Script Tasks and Script Components: custom code written inside the package. The generator cannot translate this code automatically, so it needs a hand-written port.",
};

/** Keyed by the component type's short class name (the Microsoft. prefix removed). */
const COMPONENT_INFO: Record<string, string> = {
  FlatFileSource: "Reads rows from a delimited or fixed-width text file.",
  FlatFileDestination: "Writes rows into a delimited or fixed-width text file.",
  ExcelSource: "Reads rows from a worksheet in an Excel workbook.",
  OLEDBSource: "Reads rows from a database table, view or SQL query.",
  OLEDBDestination: "Writes rows into a database table.",
  OLEDBCommand: "Runs a SQL command once for each row, for example an UPDATE.",
  DerivedColumn: "Adds or changes columns using expressions, such as string or date functions.",
  DataConvert: "Converts a column to another data type.",
  CharacterMap: "Changes text in a column, for example to upper or lower case.",
  ConditionalSplit: "Sends each row to one of several outputs, based on conditions.",
  Multicast: "Copies every row to several outputs.",
  PctSampling: "Sends a percentage of the rows to one output.",
  Lookup: "Finds a matching row in a reference table and adds its columns to the row.",
  Sort: "Sorts rows by one or more key columns.",
  MergeJoin: "Joins two sorted inputs (inner, left or full outer join).",
  Merge: "Combines two sorted inputs into one sorted output.",
  UnionAll: "Stacks rows from several inputs into one output, without sorting.",
  Aggregate: "Groups rows and calculates totals such as sums and counts.",
  RowCount: "Counts the rows that pass through and stores the total in a variable.",
  SCD: "Slowly Changing Dimension: compares incoming rows with a dimension table to find new, changed and unchanged rows.",
  CopyMap: "Copies column values from input to output using an explicit mapping.",
  XmlSourceAdapter: "Reads rows from an XML file.",
  ADONETSource: "Reads rows from a database through an ADO.NET connection.",
  DataReaderSourceAdapter: "Reads rows from a database through an ADO.NET connection.",
  ADONETDestination: "Writes rows into a database through an ADO.NET connection.",
  ManagedComponentHost:
    "A custom .NET component, such as an ADO.NET source or destination, or a Script Component. Its class name is generic in the file, so the row shows its own name.",
  ScriptComponentHost: "A Script Component: custom code inside a Data Flow Task. The generator cannot translate it automatically.",
};

const COMPONENT_FALLBACK =
  "A pipeline component of this type. Open the package in SSIS to see its exact settings.";

/** Keyed by the connection kind shown in the summary (ADO.NET kinds are grouped by provider). */
const CONNECTION_INFO: Record<string, string> = {
  OLEDB: "Connects to a database through the OLE DB provider, for example SQL Server.",
  FLATFILE: "A delimited or fixed-width text file, described by its column layout.",
  FILE: "A plain file path, used by File System Tasks or as the source of a file read.",
  EXCEL: "An Excel workbook (.xlsx) used as a data source.",
};

const CONNECTION_FALLBACK = "A connection to a data source or file. Open the package in SSIS for its exact settings.";

export function componentInfo(type: string): string {
  return COMPONENT_INFO[type] ?? COMPONENT_FALLBACK;
}

export function connectionInfo(kind: string): string {
  if (kind.startsWith("ADO.NET")) {
    return "Connects to a database through an ADO.NET provider. The provider name is shown after the dot.";
  }
  return CONNECTION_INFO[kind] ?? CONNECTION_FALLBACK;
}
