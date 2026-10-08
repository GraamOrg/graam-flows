using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Objects.Util;

/// <summary>
///     Reads an asset's day-count string off the wire. Absent means 30/360 (the engine's historic
///     accrual); a spelling the engine does not know is an error, never a silent 30/360 — a caller
///     that states a basis must get that basis or be told it did not.
/// </summary>
public static class AccrualBasisParser
{
    public static AccrualBasis Parse(string? dayCount)
    {
        if (string.IsNullOrWhiteSpace(dayCount))
            return AccrualBasis.Thirty360;

        var key = new string(dayCount.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return key switch
        {
            "30360" or "THIRTY360" => AccrualBasis.Thirty360,
            "ACTUAL360" or "ACT360" or "A360" => AccrualBasis.Actual360,
            _ => throw new ArgumentException(
                $"Unknown asset day count '{dayCount}'. Supported: '30/360' (default) and 'Actual/360'.")
        };
    }

    /// <summary>
    ///     The Actual/360 year fraction of each monthly projection period. Period <c>p</c> is dated
    ///     <c>firstProjectionDate + p months</c> and accrues the month ENDING there, so its fraction
    ///     is the day count of the month that starts at <c>firstProjectionDate + (p − 1) months</c>,
    ///     over 360. Between two same-day-of-month dates that day count is the length of the earlier
    ///     calendar month, whatever the day.
    /// </summary>
    public static double[] Actual360Fractions(DateTime firstProjectionDate, int periods)
    {
        var fractions = new double[Math.Max(periods, 0)];
        for (var p = 0; p < fractions.Length; p++)
        {
            var start = firstProjectionDate.AddMonths(p - 1);
            fractions[p] = DateTime.DaysInMonth(start.Year, start.Month) / 360.0;
        }

        return fractions;
    }
}
