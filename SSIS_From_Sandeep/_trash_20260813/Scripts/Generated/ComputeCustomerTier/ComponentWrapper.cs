using System;
using Microsoft.SqlServer.Dts.Pipeline;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace SC_ComputeCustomerTier
{
    public class UserComponent : ScriptComponent
    {
        public override void ProcessInput(int InputID, string InputName, PipelineBuffer Buffer, OutputNameMap OutputMap)
        {
            Input0Buffer Row = new Input0Buffer(Buffer, GetColumnIndexes(InputID), OutputMap);

            while (Row.NextRow())
            {
                Input0_ProcessInputRow(Row);
            }

            if (Row.EndOfRowset())
            {
                Input0_ProcessInput(Row);
            }
        }

        public virtual void Input0_ProcessInputRow(Input0Buffer Row) { }

        public virtual void Input0_ProcessInput(Input0Buffer Buffer) { }
    }
}
