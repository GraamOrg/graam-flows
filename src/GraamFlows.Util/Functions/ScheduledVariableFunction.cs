using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Util.Functions;

public class ScheduledVariableFunction
{
    private readonly IScheduledVariable[] _schedVars;

    public ScheduledVariableFunction(IScheduledVariable[] schedVars)
    {
        _schedVars = schedVars.OrderBy(sched => sched.BeginDate).ThenBy(sched => sched.EndDate).ToArray();
    }

    public static ScheduledVariableFunction FromScheduleVariables(IScheduledVariable[] schedVars)
    {
        return new ScheduledVariableFunction(schedVars);
    }

    public static ScheduledVariableFunction FromPoints(DateTime startDate, int dateSpacingInMonths, double[] values)
    {
        var schedVars = new List<IScheduledVariable>();
        for (var i = 0; i < values.Length; ++i)
        {
            var schedVar = new ScheduledVariableItem();
            schedVar.BeginDate = startDate.AddMonths(i * dateSpacingInMonths);
            schedVar.EndDate = startDate.AddMonths((i + 1) * dateSpacingInMonths);
            schedVar.ValueNum = values[i];
            schedVars.Add(schedVar);
        }

        return new ScheduledVariableFunction(schedVars.ToArray());
    }

    public double ValueAt(DateTime date)
    {
        var schedVar = SchedVarForDate(date);
        return schedVar.ValueNum;
    }

    public IScheduledVariable SchedVarForDate(DateTime date)
    {
        foreach (var schedVar in _schedVars.Reverse())
            if (date >= schedVar.BeginDate && date <= schedVar.EndDate)
                return schedVar;

        // constant extrapolation, so far nothing requires a different extrapolation method.
        var distanceFromBegin = Math.Abs((_schedVars.First().BeginDate - date).TotalDays);
        var distanceFromEnd = Math.Abs((_schedVars.Last().EndDate - date).TotalDays);
        if (distanceFromBegin < distanceFromEnd)
            return _schedVars.First();
        return _schedVars.Last();
    }
}

internal class ScheduledVariableItem : IScheduledVariable
{
    public string DealName { get; set; }
    public string GroupNum { get; set; }
    public string ScheduleVariableName { get; set; }
    public DateTime BeginDate { get; set; }
    public DateTime EndDate { get; set; }
    public double ValueNum { get; set; }
    public string ValueString { get; set; }
    public DateTime ValueDate { get; set; }
}