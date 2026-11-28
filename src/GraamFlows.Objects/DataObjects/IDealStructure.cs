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

    /// <summary>
    ///     An exchange class that is interest-only: the classes its NOTIONAL follows
    ///     (comma-separated), when the document states it apart from its components. Null for
    ///     every other class. See <c>BaseStructure.PayExchangeables</c>.
    /// </summary>
    string? NotionalReference { get; }

    /// <summary>
    ///     A notional class that follows its classes at different shares of each: the share of each
    ///     class its ExchangableTranche names. Null: one share, its original over theirs.
    /// </summary>
    IReadOnlyDictionary<string, double>? NotionalWeights { get; }

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