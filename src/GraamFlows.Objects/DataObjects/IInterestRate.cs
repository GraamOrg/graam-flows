using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Objects.DataObjects;

public interface IInterestRate
{
    double Term { get; }
    double Rate { get; }
    CompoundingTypeEnum Compounding { get; }
    FrequencyTypeEnum Frequency { get; }
    IDayCounter DayCounter { get; }
    double DiscountFactor(double t, double s);
    double DiscountFactor(double t);
    double DiscountFactor(DateTime d1, DateTime d2);
    double DiscountFactor(DateTime d1, DateTime d2, double s);
    double CompoundFactor(double t);
    IInterestRate EquivalentRate(CompoundingTypeEnum comp, FrequencyTypeEnum freq, double t);

    IInterestRate EquivalentRate(IDayCounter dayCounter, CompoundingTypeEnum comp, FrequencyTypeEnum freq, DateTime d1,
        DateTime d2);
}