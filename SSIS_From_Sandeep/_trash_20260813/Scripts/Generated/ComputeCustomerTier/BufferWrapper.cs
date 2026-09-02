using System;
using Microsoft.SqlServer.Dts.Pipeline;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace SC_ComputeCustomerTier
{
    /// <summary>
    /// Index N is the ordinal position in GetColumnIndexes(inputID): the six input
    /// columns in input-column order, then the two added output columns.
    /// </summary>
    public class Input0Buffer : ScriptBuffer
    {
        public Input0Buffer(PipelineBuffer BufferToUse, int[] BufferColumnIndexesToUse, OutputNameMap OutputMap)
            : base(BufferToUse, BufferColumnIndexesToUse, OutputMap)
        {
        }

        // ---- input columns (read) ----
        public int CustomerID        { get { return (int)this[0]; } }
        public string FullName       { get { return IsNull(1) ? null : (string)this[1]; } }
        public string Email          { get { return IsNull(2) ? null : (string)this[2]; } }
        public int TotalOrders       { get { return (int)this[3]; } }
        public double TotalRevenue   { get { return IsNull(4) ? 0d : (double)this[4]; } }
        public DateTime LastOrderDate { get { return (DateTime)this[5]; } }

        // ---- added output columns (write) ----
        public string CustomerTier        { set { this[6] = value; } }
        public DateTime ProcessedDateTime { set { this[7] = value; } }

        public new bool NextRow()     { return base.NextRow(); }
        public new bool EndOfRowset() { return base.EndOfRowset(); }
    }
}
