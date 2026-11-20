namespace GridSafety.Core;

/// <summary>
/// Calendar days for reports, in the service area's local time (US Pacific), so a day's work isn't split at UTC
/// midnight (4–5 pm here). The DST rule is built in rather than read from the OS time zone database, which isn't on
/// every Lambda image: daylight time runs from 2 am on the second Sunday of March to 2 am on the first Sunday of November.
/// </summary>
public static class Periods
{
    public static readonly TimeSpan Standard = TimeSpan.FromHours(-8), Daylight = TimeSpan.FromHours(-7);

    /// <summary>The UTC offset of US Pacific time at <paramref name="t"/>.</summary>
    public static TimeSpan OffsetAt(DateTimeOffset t)
    {
        var y = t.UtcDateTime.Year;
        // 2 am local: 10:00 UTC in March (still standard time), 09:00 UTC in November (still daylight time).
        var start = new DateTimeOffset(Sunday(y, 3, 2), new TimeOnly(10, 0), TimeSpan.Zero);
        var end = new DateTimeOffset(Sunday(y, 11, 1), new TimeOnly(9, 0), TimeSpan.Zero);
        return t >= start && t < end ? Daylight : Standard;
    }

    /// <summary>The Pacific calendar day <paramref name="t"/> falls on.</summary>
    public static DateOnly DayOf(DateTimeOffset t) => DateOnly.FromDateTime(t.ToOffset(OffsetAt(t)).DateTime);

    /// <summary>The <paramref name="nth"/> Sunday of a month.</summary>
    private static DateOnly Sunday(int year, int month, int nth)
    {
        var first = new DateOnly(year, month, 1);
        return first.AddDays((7 - (int)first.DayOfWeek) % 7 + 7 * (nth - 1));
    }
}
