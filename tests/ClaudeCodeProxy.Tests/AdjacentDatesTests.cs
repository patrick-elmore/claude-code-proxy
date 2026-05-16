using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class AdjacentDatesTests
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);
    private static readonly DateOnly Jan2 = new(2026, 1, 2);
    private static readonly DateOnly Jan3 = new(2026, 1, 3);

    [Fact]
    public void SingleDate_TargetIsToday_NoPrevOrNext()
    {
        var all = List(Jan1);

        var (prev, next) = StatsNavigation.AdjacentDates(all, Jan1, today: Jan1);

        prev.ShouldBeNull();
        next.ShouldBeNull();
    }

    [Fact]
    public void MultiDate_TargetIsFirst_NoPrev()
    {
        var all = List(Jan1, Jan2, Jan3);

        var (prev, next) = StatsNavigation.AdjacentDates(all, Jan1, today: Jan3);

        prev.ShouldBeNull();
        next.ShouldBe(Jan2);
    }

    [Fact]
    public void MultiDate_TargetIsMiddle_NotToday_HasPrevAndNext()
    {
        var all = List(Jan1, Jan2, Jan3);

        var (prev, next) = StatsNavigation.AdjacentDates(all, Jan2, today: Jan3);

        prev.ShouldBe(Jan1);
        next.ShouldBe(Jan3);
    }

    [Fact]
    public void MultiDate_TargetIsToday_NextAlwaysNull()
    {
        var all = List(Jan1, Jan2, Jan3);

        var (prev, next) = StatsNavigation.AdjacentDates(all, Jan3, today: Jan3);

        prev.ShouldBe(Jan2);
        next.ShouldBeNull();
    }

    [Fact]
    public void TargetNotInList_PrevIsLastKnownDate()
    {
        var all = List(Jan1, Jan2);
        var today = new DateOnly(2026, 1, 3); // not in list

        var (prev, next) = StatsNavigation.AdjacentDates(all, today, today);

        prev.ShouldBe(Jan2);
        next.ShouldBeNull();
    }

    [Fact]
    public void EmptyList_NoPrevOrNext()
    {
        var (prev, next) = StatsNavigation.AdjacentDates([], Jan1, today: Jan1);

        prev.ShouldBeNull();
        next.ShouldBeNull();
    }

    private static IReadOnlyList<DateOnly> List(params DateOnly[] dates) => dates;
}
