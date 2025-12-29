namespace GraamFlows.Objects.DataObjects;

public interface ITermStructure
{
    DateTime SettleDate { get; }
    List<IInterestRate> Curve { get; }
    IInterestRate GetRate(double t);
    IInterestRate GetRate(IDayCounter dayCounter, DateTime date);
}