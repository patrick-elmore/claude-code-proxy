namespace ClaudeCodeProxy;

internal static class StatsNavigation
{
    // Today is always the trailing edge — next is null even when target == today.
    internal static (DateOnly? prev, DateOnly? next) AdjacentDates(
        IReadOnlyList<DateOnly> all, DateOnly target, DateOnly today)
    {
        var idx = -1;
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i] == target) { idx = i; break; }
        }

        DateOnly? prev = null, next = null;
        if (idx > 0) prev = all[idx - 1];
        else if (idx < 0 && all.Count > 0) prev = all[^1]; // target not in list (no file yet for today)
        if (idx >= 0 && idx < all.Count - 1 && target != today) next = all[idx + 1];
        return (prev, next);
    }
}
