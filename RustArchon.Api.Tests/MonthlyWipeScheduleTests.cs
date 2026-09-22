// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The monthly wipe - the first Thursday of the month at 19:00 London time (Scott, 2026-09-21) - and the days-before-wipe hold that hangs off it.
/// The moments below are worked out by hand from the calendar, not by calling the code under test: October 1st 2026 is a Thursday and still
/// British Summer Time (18:00 UTC); November 5th 2026 is after the clocks went back (19:00 UTC).
/// </summary>
public class MonthlyWipeScheduleTests
{
    private static DateTimeOffset Utc(int y, int m, int d, int h = 0, int min = 0, int s = 0) => new(y, m, d, h, min, s, TimeSpan.Zero);

    [Theory]
    [InlineData(2026, 10, 1, 18)]     // Thursday the 1st, BST
    [InlineData(2026, 11, 5, 19)]     // first Thursday after the clocks went back
    [InlineData(2026, 1, 1, 19)]      // January 1st is itself a Thursday, GMT
    [InlineData(2026, 3, 5, 19)]      // clocks change on the 29th, so still GMT
    [InlineData(2026, 4, 2, 18)]      // BST
    [InlineData(2026, 7, 2, 18)]      // BST
    [InlineData(2027, 1, 7, 19)]      // January 2027 starts on a Friday
    public void TheWipeIsTheFirstThursdayAt1900LondonTime(int year, int month, int day, int utcHour)
    {
        Assert.Equal(Utc(year, month, day, utcHour), MonthlyWipeSchedule.WipeInMonth(year, month));
    }

    [Fact]
    public void EveryMonthForFiveYearsIsAThursdayInTheFirstWeekAtEighteenOrNineteenUtc()
    {
        for (var year = 2026; year <= 2030; year++)
        {
            for (var month = 1; month <= 12; month++)
            {
                var wipe = MonthlyWipeSchedule.WipeInMonth(year, month);
                Assert.Equal(DayOfWeek.Thursday, wipe.DayOfWeek);
                Assert.InRange(wipe.Day, 1, 7);
                Assert.Equal((year, month), (wipe.Year, wipe.Month));
                Assert.Contains(wipe.Hour, new[] { 18, 19 });     // 19:00 London is 18:00 UTC in summer, 19:00 in winter
                Assert.Equal(0, wipe.Minute);
            }
        }
    }

    [Fact]
    public void TheSummerAndWinterWipesDifferByTheClockChangeAndNothingElse()
    {
        // Same wall-clock time in London, so the UTC hour is what moves.
        Assert.Equal(18, MonthlyWipeSchedule.WipeInMonth(2026, 8).Hour);
        Assert.Equal(19, MonthlyWipeSchedule.WipeInMonth(2026, 12).Hour);
    }

    [Fact]
    public void BeforeThisMonthsWipeTheNextWipeIsThisMonths()
    {
        Assert.Equal(Utc(2026, 10, 1, 18), MonthlyWipeSchedule.NextWipeAfter(Utc(2026, 9, 21, 14, 2)));
        Assert.Equal(Utc(2026, 10, 1, 18), MonthlyWipeSchedule.NextWipeAfter(Utc(2026, 10, 1, 17, 59, 59)));
    }

    [Fact]
    public void AtTheMomentOfTheWipeItHasHappenedAndTheNextOneIsNextMonths()
    {
        Assert.Equal(Utc(2026, 11, 5, 19), MonthlyWipeSchedule.NextWipeAfter(Utc(2026, 10, 1, 18)));
        Assert.Equal(Utc(2026, 11, 5, 19), MonthlyWipeSchedule.NextWipeAfter(Utc(2026, 10, 20)));
    }

    [Fact]
    public void ADecemberWipeRollsOverToJanuaryOfTheNextYear()
    {
        Assert.Equal(Utc(2027, 1, 7, 19), MonthlyWipeSchedule.NextWipeAfter(Utc(2026, 12, 20)));
    }

    [Fact]
    public void TheClockIsReadAsAMomentNotAsItsOffset()
    {
        // 19:30 in Chicago on 30 September is 00:30 UTC on 1 October; either way the next wipe is October 1st, 18:00 UTC.
        var chicago = new DateTimeOffset(2026, 9, 30, 19, 30, 0, TimeSpan.FromHours(-5));
        Assert.Equal(Utc(2026, 10, 1, 18), MonthlyWipeSchedule.NextWipeAfter(chicago));
    }

    // ---- the hold ------------------------------------------------------------------------------------------------

    [Fact]
    public void AHoldOfZeroNeverHoldsAnythingNotEvenOnWipeDay()
    {
        Assert.Null(MonthlyWipeSchedule.HeldUntil(Utc(2026, 9, 30), 0));
        Assert.Null(MonthlyWipeSchedule.HeldUntil(Utc(2026, 10, 1, 17, 59, 59), 0));
    }

    [Fact]
    public void ASevenDayHoldStartsExactlySevenTimesTwentyFourHoursBeforeTheWipeAndLastsUntilIt()
    {
        var wipe = Utc(2026, 10, 1, 18);

        Assert.Null(MonthlyWipeSchedule.HeldUntil(Utc(2026, 9, 24, 17, 59, 59), 7));     // one second before the window
        Assert.Equal(wipe, MonthlyWipeSchedule.HeldUntil(Utc(2026, 9, 24, 18), 7));      // the window opens
        Assert.Equal(wipe, MonthlyWipeSchedule.HeldUntil(Utc(2026, 10, 1, 17, 59, 59), 7));
    }

    [Fact]
    public void TheHoldLiftsAtTheWipeAndStaysLiftedUntilTheNextWindow()
    {
        Assert.Null(MonthlyWipeSchedule.HeldUntil(Utc(2026, 10, 1, 18), 7));             // wipe moment: it has happened
        Assert.Null(MonthlyWipeSchedule.HeldUntil(Utc(2026, 10, 15), 7));
        Assert.Null(MonthlyWipeSchedule.HeldUntil(Utc(2026, 10, 29, 18, 59, 59), 7));    // a second before 5 Nov 19:00 minus 7 days
        Assert.Equal(Utc(2026, 11, 5, 19), MonthlyWipeSchedule.HeldUntil(Utc(2026, 10, 29, 19), 7));
    }

    [Fact]
    public void ANegativeHoldIsTreatedAsNone()
    {
        Assert.Null(MonthlyWipeSchedule.HeldUntil(Utc(2026, 10, 1, 12), -3));
    }

    [Fact]
    public void TheLongestHoldStillLeavesAnOpenWindowInEveryMonthForFiveYears()
    {
        // Walk from each wipe to the next: with the maximum hold there must always be a stretch of time that is not held.
        for (var year = 2026; year <= 2030; year++)
        {
            for (var month = 1; month <= 12; month++)
            {
                var wipe = MonthlyWipeSchedule.WipeInMonth(year, month);
                Assert.Null(MonthlyWipeSchedule.HeldUntil(wipe, MonthlyWipeSchedule.MaxHoldDays));
                Assert.Null(MonthlyWipeSchedule.HeldUntil(wipe.AddDays(1), MonthlyWipeSchedule.MaxHoldDays));
            }
        }
    }

    [Fact]
    public void TheDefaultHoldIsSevenDays()
    {
        Assert.Equal(7, MonthlyWipeSchedule.DefaultHoldDays);
        Assert.Equal(7, new RustArchon.Api.Data.RustServer().ThirdPartyPluginUpdateHoldDays);
    }

    [Fact]
    public void TheLimitTheApiAcceptsIsTheLimitTheScheduleIsSafeFor()
    {
        var attribute = typeof(UpdateThirdPartyPluginUpdateSettingsDto).GetProperty(nameof(UpdateThirdPartyPluginUpdateSettingsDto.HoldDays))!
            .GetCustomAttributes(typeof(RangeAttribute), false).Cast<RangeAttribute>().Single();
        Assert.Equal(0, attribute.Minimum);
        Assert.Equal(MonthlyWipeSchedule.MaxHoldDays, attribute.Maximum);
    }
}
