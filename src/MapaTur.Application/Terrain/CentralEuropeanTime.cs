namespace MapaTur.Application.Terrain;

/// <summary>
/// UTC offset for Central European Time — the app's region (Poland) — honouring EU daylight-saving rules so
/// the night-sky model can turn the wall-clock time slider into the right Julian Date. Standard time CET is
/// UTC+1; summer time CEST is UTC+2 from the last Sunday of March to the last Sunday of October (EU Directive
/// 2000/84/EC). Pure/deterministic: resolved at date granularity, which is exact except inside the ~1 h
/// transition window twice a year — negligible for star directions, which drift only 15°/h. The
/// <see cref="System.DateTime"/> calls here read only the calendar (day-of-week, days-in-month), never the
/// clock, so the result depends solely on the arguments.
/// </summary>
public static class CentralEuropeanTime
{
    /// <summary>
    /// Hours to ADD to UTC for the given local calendar date: +2 during summer time (CEST), +1 otherwise
    /// (CET). To convert a local wall-clock hour to UTC for a Julian Date, subtract this from the local hour.
    /// </summary>
    public static double UtcOffsetHours(int year, int month, int day)
        => IsSummerTime(year, month, day) ? 2.0 : 1.0;

    private static bool IsSummerTime(int year, int month, int day)
    {
        // Cheap exits for the clearly-winter / clearly-summer months — only March and October straddle a
        // transition.
        if (month < 3 || month > 10)
        {
            return false;
        }

        if (month > 3 && month < 10)
        {
            return true;
        }

        // Resolve at date granularity: summer time runs [last Sunday of March, last Sunday of October).
        // On the spring Sunday DST starts at 02:00 local, on the autumn Sunday standard time resumes at
        // 03:00 CEST — so treating the whole spring Sunday as CEST and the whole autumn Sunday as CET is
        // correct apart from those few small-hour transition hours.
        if (month == 3)
        {
            return day >= LastSundayOfMonth(year, 3);
        }

        return day < LastSundayOfMonth(year, 10);
    }

    /// <summary>Day-of-month of the last Sunday in the given month (DayOfWeek.Sunday == 0).</summary>
    private static int LastSundayOfMonth(int year, int month)
    {
        int daysInMonth = DateTime.DaysInMonth(year, month);
        int lastDayWeekday = (int)new DateTime(year, month, daysInMonth).DayOfWeek;
        return daysInMonth - lastDayWeekday;
    }
}