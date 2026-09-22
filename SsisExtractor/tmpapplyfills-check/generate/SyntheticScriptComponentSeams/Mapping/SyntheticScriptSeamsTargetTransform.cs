using Etl.Core.Abstractions;
using SyntheticScriptComponentSeams.Sql;
using SyntheticScriptComponentSeams.Model;

namespace SyntheticScriptComponentSeams.Mapping;

internal readonly record struct SCR_CleanseResult(string FullName, bool IsValid);

/// <summary>Has 1 unimplemented Tier-2 seam(s): each is a Script Component, computing
/// one or more columns together, whose logic this tool does not translate. Implement each
/// one in a SECOND PART of this partial class -- a class part, not just a method body, so
/// the port can declare its own fields (a compiled Regex, a lookup table). Until then this
/// project deliberately does NOT compile: CS8795, one error per unfilled seam. Put the
/// part in fills/&lt;Package&gt;/SyntheticScriptSeamsTargetTransform.Fills.cs and run `ssisx apply-fills`.</summary>
public sealed partial class SyntheticScriptSeamsTargetTransform : IRowTransform<SyntheticScriptSeamsTargetSqlRow, SyntheticScriptSeamsTarget>
{
    public SyntheticScriptSeamsTarget Map(SyntheticScriptSeamsTargetSqlRow row, in RowContext ctx)
    {
        var sCR_CleanseResult = SCR_Cleanse(row, ctx);
        return new()
        {
            ID = row.ID,
            FirstName = row.FirstName,
            LastName = row.LastName,
            FullName = sCR_CleanseResult.FullName,
            IsValid = sCR_CleanseResult.IsValid,
        };
    }

    /// <summary>Script Component 'SCR_Cleanse', producing 2 column(s)
    /// together: FullName, IsValid. Work packet: SCRIPT-COLUMN SyntheticScriptSeamsTarget.SCR_Cleanse.</summary>
    private partial SCR_CleanseResult SCR_Cleanse(SyntheticScriptSeamsTargetSqlRow row, in RowContext ctx);
}
