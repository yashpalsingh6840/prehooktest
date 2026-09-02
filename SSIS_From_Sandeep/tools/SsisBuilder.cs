// Shared helpers for building SSIS packages from the command line.
//
// WHY THIS IS C# AND NOT POWERSHELL
// ---------------------------------
// The data-flow object model is COM (DTSPipelineWrap). PowerShell reaches it only
// through IDispatch late binding, which silently fails on these interfaces:
// MainPipe cannot be cast, and IDTSComponentMetaData100.Instantiate() throws
// TYPE_E_ELEMENTNOTFOUND (0x8002802B). Compiled C# against the strongly-typed
// interop works for all 59 registered components. Measured, not assumed.
//
// Compile with: Add-Type -Path tools\SsisBuilder.cs -ReferencedAssemblies <interop>
using System;
using System.Collections.Generic;
using Microsoft.SqlServer.Dts.Runtime;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using RTVarType = Microsoft.SqlServer.Dts.Runtime.Wrapper.DataType;

namespace SsisBuild
{
    public static class Ssis
    {
        // ---------------------------------------------------------- component ids
        // Stock components must be referenced by their VERSIONED moniker
        // (DTSTransform.DerivedColumn.8). The unversioned form is not registered and
        // fails with TYPE_E_ELEMENTNOTFOUND. Resolved from the live registry so this
        // keeps working if the SSIS version changes.
        static Dictionary<string, string> _ids;

        public static string ClassId(string displayName)
        {
            if (_ids == null)
            {
                _ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var app = new Application();
                foreach (PipelineComponentInfo pci in app.PipelineComponentInfos)
                    _ids[pci.Name] = pci.CreationName;
            }
            if (!_ids.ContainsKey(displayName))
                throw new Exception("pipeline component not registered: " + displayName);
            return _ids[displayName];
        }

        // ------------------------------------------------------ connection managers
        public static ConnectionManager OleDb(Package p, string name, string connStr)
        {
            var cm = p.Connections.Add("OLEDB");
            cm.Name = name;
            cm.ConnectionString = connStr;
            return cm;
        }

        public static ConnectionManager AdoNet(Package p, string name, string connStr)
        {
            var cm = p.Connections.Add(
                "ADO.NET:System.Data.SqlClient.SqlConnection, System.Data, Version=4.0.0.0, " +
                "Culture=neutral, PublicKeyToken=b77a5c561934e089");
            cm.Name = name;
            cm.ConnectionString = connStr;
            return cm;
        }

        /// <param name="format">Delimited | FixedWidth | RaggedRight</param>
        public static ConnectionManager FlatFile(Package p, string name, string path,
                                                 bool headerRow, string format)
        {
            var cm = p.Connections.Add("FLATFILE");
            cm.Name = name;
            cm.ConnectionString = path;
            cm.Properties["Format"].SetValue(cm, format);
            cm.Properties["ColumnNamesInFirstDataRow"].SetValue(cm, headerRow);
            cm.Properties["HeaderRowDelimiter"].SetValue(cm, "\r\n");
            cm.Properties["RowDelimiter"].SetValue(cm, "\r\n");
            return cm;
        }

        /// Adds one column to a flat file connection manager.
        /// delimiter is "" for the fixed-width case; width is ignored when delimited.
        public static void FlatFileColumn(ConnectionManager cm, string name,
                                          string delimiter, int width, int length)
        {
            var ff = cm.InnerObject as Microsoft.SqlServer.Dts.Runtime.Wrapper.IDTSConnectionManagerFlatFile100;
            var col = ff.Columns.Add();
            col.ColumnType = string.IsNullOrEmpty(delimiter) ? "FixedWidth" : "Delimited";
            col.ColumnDelimiter = delimiter ?? "";
            col.ColumnWidth = width;
            col.MaximumWidth = length;
            // DT_WSTR, not DT_STR: every target column in SSISDemo is NVARCHAR, and an
            // ANSI flat-file column cannot be joined against an NVARCHAR reference
            // column ("... have incompatible data types" in the Lookup).
            col.DataType = RTVarType.DT_WSTR;
            col.DataPrecision = 0;
            col.DataScale = 0;
            // The column object exposes no Name member - naming goes through IDTSName100.
            ((Microsoft.SqlServer.Dts.Runtime.Wrapper.IDTSName100)col).Name = name;
        }

        /// usage: 0 = file exists, 1 = create file, 2 = folder exists, 3 = create folder.
        /// Must be the INT enum value - passing the name throws 0x80020005 (type mismatch).
        public static ConnectionManager FileConn(Package p, string name, string path, int usage)
        {
            var cm = p.Connections.Add("FILE");
            cm.Name = name;
            cm.ConnectionString = path;
            cm.Properties["FileUsageType"].SetValue(cm, usage);
            return cm;
        }

        public static ConnectionManager Excel(Package p, string name, string xlsxPath)
        {
            var cm = p.Connections.Add("EXCEL");
            cm.Name = name;
            cm.ConnectionString =
                "Provider=Microsoft.ACE.OLEDB.12.0;Data Source=" + xlsxPath +
                ";Extended Properties=\"Excel 12.0 XML;HDR=YES\";";
            return cm;
        }

        // ---------------------------------------------------------- control flow
        public static TaskHost ExecSql(Package p, string name, ConnectionManager cm, string sql)
        {
            var th = (TaskHost)p.Executables.Add("Microsoft.ExecuteSQLTask");
            th.Name = name;
            th.Properties["Connection"].SetValue(th, cm.Name);
            th.Properties["SqlStatementSource"].SetValue(th, sql);
            return th;
        }

        public static TaskHost DataFlow(Package p, string name)
        {
            var th = (TaskHost)p.Executables.Add("Microsoft.Pipeline");
            th.Name = name;
            return th;
        }

        public static MainPipe Pipe(TaskHost dataFlowTask)
        {
            return (MainPipe)dataFlowTask.InnerObject;
        }

        public static Sequence SequenceContainer(Package p, string name)
        {
            var s = (Sequence)p.Executables.Add("STOCK:SEQUENCE");
            s.Name = name;
            return s;
        }

        /// Success/Failure/Completion link between two executables.
        public static PrecedenceConstraint Link(Package p, Executable from, Executable to,
                                                DTSExecResult onValue)
        {
            var pc = p.PrecedenceConstraints.Add(from, to);
            pc.Value = onValue;
            pc.EvalOp = DTSPrecedenceEvalOp.Constraint;
            return pc;
        }

        /// Link that also requires an expression to be true.
        public static PrecedenceConstraint LinkExpr(Package p, Executable from, Executable to,
                                                    DTSExecResult onValue, string expression)
        {
            var pc = p.PrecedenceConstraints.Add(from, to);
            pc.Value = onValue;
            pc.Expression = expression;
            pc.EvalOp = DTSPrecedenceEvalOp.ExpressionAndConstraint;
            return pc;
        }

        public static Variable Var(Package p, string name, object value)
        {
            var v = p.Variables.Add(name, false, "User", value);
            return v;
        }

        // ------------------------------------------------------------- data flow
        public static IDTSComponentMetaData100 Comp(MainPipe pipe, string name, string displayName)
        {
            var cm = pipe.ComponentMetaDataCollection.New();
            cm.ComponentClassID = ClassId(displayName);
            var inst = cm.Instantiate();
            inst.ProvideComponentProperties();
            // MUST be after ProvideComponentProperties(): that call resets Name to the
            // component's default, so naming first leaves every Derived Column called
            // "Derived Column" and the package fails to load on duplicate names.
            cm.Name = name;
            return cm;
        }

        public static CManagedComponentWrapper Wrap(IDTSComponentMetaData100 cm)
        {
            return cm.Instantiate();
        }

        /// Attach a runtime connection manager to a component.
        public static void SetConn(IDTSComponentMetaData100 cm, Package p, ConnectionManager conn)
        {
            cm.RuntimeConnectionCollection[0].ConnectionManagerID = conn.ID;
            cm.RuntimeConnectionCollection[0].ConnectionManager =
                Microsoft.SqlServer.Dts.Runtime.DtsConvert.GetExtendedInterface(conn);
        }

        public static IDTSPath100 Connect(MainPipe pipe,
                                          IDTSComponentMetaData100 from, int outputIndex,
                                          IDTSComponentMetaData100 to, int inputIndex)
        {
            var path = pipe.PathCollection.New();
            path.AttachPathAndPropagateNotifications(
                from.OutputCollection[outputIndex],
                to.InputCollection[inputIndex]);
            return path;
        }

        /// Mark every available input column as used - the common case for
        /// destinations and pass-through transforms.
        public static void UseAllInputColumns(IDTSComponentMetaData100 cm, int inputIndex)
        {
            var input = cm.InputCollection[inputIndex];
            var wrap = cm.Instantiate();

            // GetVirtualInput() returns a NEW object each call. SetUsageType must be
            // given the SAME instance the lineage ids were read from, or it fails with
            // 0xC0047041. Capture it once, and snapshot the ids before mutating, since
            // each SetUsageType invalidates the collection being enumerated.
            var vInput = input.GetVirtualInput();
            var lineageIds = new List<int>();
            foreach (IDTSVirtualInputColumn100 vc in vInput.VirtualInputColumnCollection)
                lineageIds.Add(vc.LineageID);

            foreach (int lid in lineageIds)
                wrap.SetUsageType(input.ID, vInput, lid, DTSUsageType.UT_READONLY);
        }

        /// Explicitly wire a destination: select ONLY the listed pipeline columns and
        /// map each to the named table column.
        ///
        /// Name-matching is not enough here. An OLE DB Destination requires every
        /// SELECTED input column to be mapped, so selecting everything fails; and the
        /// converted columns (CustomerID_i4, SignupDate_dt) deliberately differ in name
        /// from their table columns, while same-named string originals still exist
        /// upstream and would bind with the wrong type -> VS_NEEDSNEWMETADATA.
        public static int MapDestination(IDTSComponentMetaData100 cm, int inputIndex,
                                         string[] pipelineColumns, string[] tableColumns)
        {
            if (pipelineColumns.Length != tableColumns.Length)
                throw new ArgumentException("column arrays must be the same length");

            var input = cm.InputCollection[inputIndex];
            var wrap = cm.Instantiate();
            var vIn = input.GetVirtualInput();

            var ids = new List<int>();
            foreach (string want in pipelineColumns)
            {
                int lid = 0;
                foreach (IDTSVirtualInputColumn100 vc in vIn.VirtualInputColumnCollection)
                    if (string.Equals(vc.Name, want, StringComparison.OrdinalIgnoreCase))
                    { lid = vc.LineageID; break; }
                if (lid == 0) throw new Exception("pipeline column not found: " + want);
                ids.Add(lid);
            }
            foreach (int lid in ids)
                wrap.SetUsageType(input.ID, vIn, lid, DTSUsageType.UT_READONLY);

            int mapped = 0;
            for (int i = 0; i < pipelineColumns.Length; i++)
            {
                IDTSInputColumn100 ic = null;
                foreach (IDTSInputColumn100 c in input.InputColumnCollection)
                    if (string.Equals(c.Name, pipelineColumns[i], StringComparison.OrdinalIgnoreCase))
                    { ic = c; break; }
                if (ic == null) throw new Exception("input column missing after select: " + pipelineColumns[i]);

                IDTSExternalMetadataColumn100 ec = null;
                foreach (IDTSExternalMetadataColumn100 e in input.ExternalMetadataColumnCollection)
                    if (string.Equals(e.Name, tableColumns[i], StringComparison.OrdinalIgnoreCase))
                    { ec = e; break; }
                if (ec == null) throw new Exception("table column not found: " + tableColumns[i]);

                wrap.MapInputColumn(input.ID, ic.ID, ec.ID);
                mapped++;
            }
            return mapped;
        }

        /// Map destination input columns to external (table) columns by name.
        public static int MapByName(IDTSComponentMetaData100 cm, int inputIndex)
        {
            var input = cm.InputCollection[inputIndex];
            int mapped = 0;
            foreach (IDTSInputColumn100 ic in input.InputColumnCollection)
            {
                foreach (IDTSExternalMetadataColumn100 ec in input.ExternalMetadataColumnCollection)
                {
                    if (string.Equals(ec.Name, ic.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        ic.ExternalMetadataColumnID = ec.ID;
                        mapped++;
                        break;
                    }
                }
            }
            return mapped;
        }

        // ------------------------------------------------------------------ save
        public static void Save(Package p, string path)
        {
            new Application().SaveToXml(path, p, null);
        }

        public static Package Load(string path)
        {
            return new Application().LoadPackage(path, null);
        }
    }
}
