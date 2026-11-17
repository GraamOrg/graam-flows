using System.Globalization;
using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Maps <c>ocTarget.ysoc</c> onto <see cref="YsocSchedule" />. Shared by the API controller and
///     the CLI runner, so neither can drop it (the seam <c>fullTurboPoolPct</c> was once dropped on).
///     A schedule that cannot be read is refused, not run as if the deal had none.
/// </summary>
public static class YsocMapper
{
    public static YsocSchedule? Map(YsocDto? dto, IEnumerable<string> trancheNames, string dealName)
    {
        if (dto == null)
            return null;

        string Fail(string what) => throw new InvalidOperationException($"Deal {dealName}: ocTarget.ysoc {what}");

        var amounts = Amounts(dto.Schedule, "schedule", Fail);
        if (amounts.Count == 0)
            Fail("has an empty schedule. State the YSOC for each Payment Date, or leave ysoc out.");

        YsocSwitch? after = null;
        if (dto.AfterPaidInFull != null)
        {
            var classes = (dto.AfterPaidInFull.Classes ?? new List<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            if (classes.Count == 0)
                Fail("afterPaidInFull names no class. Name the classes whose payment in full switches the schedule.");
            var known = new HashSet<string>(trancheNames, StringComparer.OrdinalIgnoreCase);
            var unknown = classes.Where(c => !known.Contains(c)).ToList();
            if (unknown.Count > 0)
                Fail($"afterPaidInFull names {string.Join(", ", unknown)}, which the deal does not have.");
            var afterAmounts = Amounts(dto.AfterPaidInFull.Schedule, "afterPaidInFull.schedule", Fail);
            if (afterAmounts.Count == 0)
                Fail("afterPaidInFull has an empty schedule.");
            after = new YsocSwitch { Classes = classes, Amounts = afterAmounts };
        }

        return new YsocSchedule { Amounts = amounts, After = after };
    }

    private static Dictionary<(int Year, int Month), double> Amounts(
        List<YsocAmountDto>? rows, string where, Func<string, string> fail)
    {
        var amounts = new Dictionary<(int Year, int Month), double>();
        foreach (var row in rows ?? new List<YsocAmountDto>())
        {
            if (!DateTime.TryParseExact(row.Month, "yyyy-MM", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var month))
                fail($"{where} has month '{row.Month}'. A month is written YYYY-MM.");
            if (double.IsNaN(row.Amount) || row.Amount < 0)
                fail($"{where} has {row.Amount} for {row.Month}. A YSOC amount is zero or more.");
            if (!amounts.TryAdd((month.Year, month.Month), row.Amount))
                fail($"{where} states {row.Month} twice.");
        }

        return amounts;
    }
}
