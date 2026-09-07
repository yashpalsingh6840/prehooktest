namespace Etl.Core.Abstractions;

/// <summary>
/// The SSIS package-variable bag, for the one thing that genuinely needs it: a ported Script Task
/// reading a variable a DIFFERENT ported Script Task wrote.
///
/// That is not hypothetical. RBC_Demo_ETL's own Package has <c>SCR_ValidateAndLogStart</c> stamping
/// <c>User::BatchStartTime</c> and <c>SCR_NotifyAndLogProgress</c> reading it back to work out
/// elapsed time -- across two tasks, in two different languages (C# and VB). Nothing else in
/// Etl.Core carries state between steps: <see cref="LoadContext"/> is deliberately immutable run
/// identity, and <see cref="IUnitOfWork"/> is a database connection. So without this, one of those
/// two tasks could not be ported faithfully at all.
///
/// Deliberately NOT modelled on SSIS's own variables in any deeper way -- no scoping, no
/// expression evaluation, no type coercion, no declared type at all. Values are whatever the
/// ported code put in. Generated code never reads or writes this bag itself; only hand-written
/// Script Task fills do, which is why a plain dictionary is the honest shape: anything richer
/// would be inventing semantics no `.dtsx` evidence asked for.
///
/// One instance per package run, created by generated <c>Program.cs</c> and shared by every
/// Script Task step. Backed by a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// so concurrent SSIS branches (emitter rewrite phase 7, <c>Docs/Emitter-Rewrite-Plan.md</c>) can
/// each read/write it without corrupting the bag itself -- this only makes individual Set/Get calls
/// safe, not a read-modify-write sequence across two concurrent branches (e.g. one branch reading a
/// counter another branch is simultaneously incrementing). No tracked package does that; if one
/// ever does, it needs a reported gap, not a lock added here on spec.
/// </summary>
public sealed class PackageVariables
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object?> _values = new();

    /// <summary>Every variable name currently set, in no particular order. Useful for logging a
    /// run's final variable state; not something a port should branch on.</summary>
    public IReadOnlyCollection<string> Names => _values.Keys.ToArray();

    /// <summary>Sets a variable, using its full SSIS name including namespace -- e.g.
    /// <c>User::BatchStartTime</c>. Overwrites silently, as SSIS does.</summary>
    public void Set(string name, object? value) => _values[name] = value;

    /// <summary>True if the variable has been set (even to null).</summary>
    public bool Contains(string name) => _values.ContainsKey(name);

    /// <summary>
    /// The variable's value as <typeparamref name="T"/>, or <paramref name="fallback"/> if it was
    /// never set or holds a different type.
    ///
    /// Returning a fallback rather than throwing on a type mismatch is deliberate: SSIS variables
    /// are loosely typed and a port reading one written by another ported task is exactly where a
    /// mismatch would surface. A port that needs to KNOW whether the value was there should call
    /// <see cref="Contains"/> or <see cref="TryGet{T}"/> instead of inferring it from a fallback.
    /// </summary>
    public T? Get<T>(string name, T? fallback = default) =>
        _values.TryGetValue(name, out var value) && value is T typed ? typed : fallback;

    /// <summary>
    /// The variable's value as <typeparamref name="T"/>, throwing if it was never set or holds a
    /// different type.
    ///
    /// For GENERATED code -- specifically a conditional-precedence-constraint guard, which uses
    /// a variable's value to decide whether a step runs at all. <see cref="Get{T}"/>'s fallback
    /// is right for hand-written port code but wrong here: a guard reading
    /// <c>Get&lt;int&gt;("User::RowsLoaded")</c> against a value some ported Script Task stored
    /// as a <c>long</c> would silently see 0 and silently skip the step, which is a wrong
    /// execution decision reported as success. Generated guards therefore call this, so a
    /// mismatch fails loudly and names both types.
    ///
    /// Generated <c>Program.cs</c> seeds every variable a guard reads with its design-time
    /// default before any step runs, so "never set" should be unreachable in generated output --
    /// if it happens, the seeding and the guard have drifted apart, which is exactly what should
    /// throw rather than pick a branch.
    /// </summary>
    public T GetRequired<T>(string name)
    {
        if (!_values.TryGetValue(name, out var raw))
            throw new InvalidOperationException(
                $"Package variable '{name}' was never set, but something requires its value. Known variables: {(_values.Count == 0 ? "(none)" : string.Join(", ", _values.Keys))}.");

        if (raw is not T typed)
            throw new InvalidOperationException(
                $"Package variable '{name}' holds {(raw is null ? "null" : $"a {raw.GetType().Name}")}, but {typeof(T).Name} was required. Returning a default here would silently change an execution decision.");

        return typed;
    }

    /// <summary>As <see cref="Get{T}"/>, but distinguishes "not set / wrong type" from "set to a
    /// value that happens to equal the fallback".</summary>
    public bool TryGet<T>(string name, out T value)
    {
        if (_values.TryGetValue(name, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }
}
