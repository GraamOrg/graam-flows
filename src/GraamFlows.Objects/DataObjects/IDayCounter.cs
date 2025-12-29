namespace GraamFlows.Objects.DataObjects;

public interface IDayCounter
{
    string Name { get; }
    int DayCount(DateTime start, DateTime end);
    double YearFraction(DateTime start, DateTime end);
    double YearFraction(DateTime start, DateTime end, DateTime refStart, DateTime refEnd);
    DateTime GetFirstDateFromYearFraction(DateTime start, double yearFraction);
    DateTime GetLastDateFromYearFraction(DateTime start, double yearFraction);
}