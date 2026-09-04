namespace Svk.Core;

/// <summary>
/// Deterministic key pool for a Lookup's join key, shared between the reference table's own
/// rows and the main flow's source rows, so some values deliberately collide (exercising the
/// match path) and some deliberately don't (exercising the no-match path). Two independently-
/// random key columns would almost never collide on their own -- this is what makes a Lookup's
/// no-match handling actually reachable in generated/synthetic data.
/// </summary>
public sealed class SharedKeyPool
{
    private readonly List<object> _matchableKeys;
    private readonly List<object> _mainFlowOnlyKeys;

    public SharedKeyPool(int matchableCount, int mainFlowOnlyCount, Func<Random, object> keyFactory, Random rng)
    {
        _matchableKeys = DistinctKeys(matchableCount, keyFactory, rng);
        _mainFlowOnlyKeys = DistinctKeys(mainFlowOnlyCount, keyFactory, rng);
    }

    /// <summary>Every key the reference table should carry a row for.</summary>
    public IReadOnlyList<object> ReferenceTableKeys => _matchableKeys;

    /// <summary>One key for the main flow's Nth row -- roughly 70% drawn from the matchable
    /// pool, the rest from a pool the reference table never carries, deterministically by row
    /// index so re-running with the same seed reproduces the identical match/no-match mix.</summary>
    public object MainFlowKeyForRow(int rowIndex)
    {
        if (_matchableKeys.Count > 0 && rowIndex % 10 < 7)
        {
            return _matchableKeys[rowIndex % _matchableKeys.Count];
        }
        if (_mainFlowOnlyKeys.Count > 0)
        {
            return _mainFlowOnlyKeys[rowIndex % _mainFlowOnlyKeys.Count];
        }
        return _matchableKeys.Count > 0 ? _matchableKeys[rowIndex % _matchableKeys.Count] : rowIndex;
    }

    private static List<object> DistinctKeys(int count, Func<Random, object> keyFactory, Random rng)
    {
        var seen = new HashSet<object>();
        var result = new List<object>();
        var attempts = 0;
        while (result.Count < count && attempts < count * 20 + 50)
        {
            attempts++;
            var key = keyFactory(rng);
            if (seen.Add(key)) result.Add(key);
        }
        return result;
    }
}
