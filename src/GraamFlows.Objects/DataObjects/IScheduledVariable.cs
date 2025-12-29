namespace GraamFlows.Objects.DataObjects;

public interface IScheduledVariable
{
    string DealName { get; }
    string ScheduleVariableName { get; }
    string GroupNum { get; }
    DateTime BeginDate { get; }
    DateTime EndDate { get; }
    double ValueNum { get; }
    string ValueString { get; }
    DateTime ValueDate { get; }
}