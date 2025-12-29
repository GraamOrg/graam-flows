namespace GraamFlows.Objects.DataObjects;

public interface IDealFieldValue
{
    string DealName { get; }
    string GroupNum { get; }
    DateTime FactorDate { get; }
    string FieldName { get; }
    double ValueNum { get; }
    string ValueString { get; }
    DateTime ValueDate { get; }
}