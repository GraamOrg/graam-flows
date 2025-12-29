namespace GraamFlows.Objects.DataObjects;

public interface IDealStructure
{
    string DealName { get; }
    string ClassGroupName { get; }
    int SubordinationOrder { get; }
    string ExchangableTranche { get; }
    string GroupNum { get; }
    string PayFrom { get; }
    string ClassTags { get; }
    PayFromEnum PayFromEnum { get; }
}

public enum PayFromEnum
{
    ExcessServicing,
    ProRata,
    Residual,
    Rule,
    Sequential,
    Expense,
    Exchange,
    Accrual,
    Group,
    Notional
}