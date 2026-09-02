using System;
using Microsoft.SqlServer.Dts.Pipeline;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace SC_CleanseCustomerRow
{
    /// <summary>
    /// Mirrors the BufferWrapper.cs that VSTA generates from the component's column
    /// metadata. Index N is the ordinal position in the array returned by
    /// ScriptComponent.GetColumnIndexes(inputID): input columns first, in input-column
    /// collection order, then the synchronous output's added columns.
    /// </summary>
    public class Input0Buffer : ScriptBuffer
    {
        public Input0Buffer(PipelineBuffer BufferToUse, int[] BufferColumnIndexesToUse, OutputNameMap OutputMap)
            : base(BufferToUse, BufferColumnIndexesToUse, OutputMap)
        {
        }

        // ---- input columns (read) ----
        public string CustomerID { get { return IsNull(0) ? null : (string)this[0]; } }
        public string FirstName  { get { return IsNull(1) ? null : (string)this[1]; } }
        public string LastName   { get { return IsNull(2) ? null : (string)this[2]; } }
        public string Email      { get { return IsNull(3) ? null : (string)this[3]; } }
        public string Phone      { get { return IsNull(4) ? null : (string)this[4]; } }
        public string SignupDate { get { return IsNull(5) ? null : (string)this[5]; } }
        public string Country    { get { return IsNull(6) ? null : (string)this[6]; } }

        // ---- added output columns (write) ----
        public string FullName        { set { this[7] = value; } }
        public string CleanEmail      { set { this[8] = value; } }
        public bool IsValidRow        { set { this[9] = value; } }
        public DateTime LoadDateTime  { set { this[10] = value; } }

        public void FullName_SetNull()     { SetNull(7); }
        public void CleanEmail_SetNull()   { SetNull(8); }
        public void IsValidRow_SetNull()   { SetNull(9); }
        public void LoadDateTime_SetNull() { SetNull(10); }

        public new bool NextRow()      { return base.NextRow(); }
        public new bool EndOfRowset()  { return base.EndOfRowset(); }
    }
}
