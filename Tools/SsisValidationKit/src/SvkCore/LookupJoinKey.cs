using Ssis.Extract.Model.Pipeline;

namespace Svk.Core;

/// <summary>
/// A Lookup's own join key, read from the JoinToReferenceColumn custom property on the
/// joining input column -- the same generic property PackageGenerator.TryDeriveLookupJoinKey
/// (Ssis.Extract.Codegen) reads. Reimplemented here in a few lines rather than referencing
/// Codegen, per this tool's own governing rule: reference Model/Dtsx (pure functions over the
/// already-parsed shape), reimplement the rest.
/// </summary>
public static class LookupJoinKey
{
    public static (string InputColumn, string ReferenceColumn)? TryDerive(PipelineComponentSpec lookup)
    {
        foreach (var column in lookup.Inputs.SelectMany(i => i.Columns))
        {
            var joinTo = column.Properties.FirstOrDefault(p => p.Name == "JoinToReferenceColumn")?.Value;
            if (!string.IsNullOrWhiteSpace(joinTo)) return (column.CachedName, joinTo);
        }
        return null;
    }
}
