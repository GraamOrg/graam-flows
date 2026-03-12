using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Objects.DataObjects;

public interface IDeal : IPayRuleAssemblyStore
{
    string DealName { get; }
    IList<IAsset> Assets { get; }
    IList<ITranche> Tranches { get; }
    IList<IDealStructure> DealStructures { get; }
    IList<IDealStructurePseudo> DealStructurePseudo { get; }
    DateTime FactorDate { get; set; }
    IList<IDealTrigger> DealTriggers { get; }
    IList<IDealVariables> DealVariables { get; }
    string CashflowEngine { get; }
    IList<IDealFieldValue> DealFieldValues { get; }
    IList<IPayRule> PayRules { get; }
    IList<IScheduledVariable> ScheduledVariables { get; }
    IList<IExchShare> ExchShares { get; }
    string InterestTreatment { get; }
    InterestTreatmentEnum InterestTreatmentEnum { get; }
    double BalanceAtIssuance { get; }
    string EncodedRules { get; set; }

    /// <summary>
    /// Waterfall structure type (e.g., "UnifiedStructure", "ComposableStructure")
    /// </summary>
    string WaterfallType { get; }

    /// <summary>
    /// Waterfall execution order for ComposableStructure
    /// (e.g., ["EXPENSE", "INTEREST", "PRINCIPAL_SCHEDULED", ...])
    /// </summary>
    IList<string> ExecutionOrder { get; }

    /// <summary>
    /// Configuration for OC turbo paydown step (optional).
    /// </summary>
    OcTargetConfig? OcTargetConfig { get; }

    /// <summary>
    /// Controls interleaving of INTEREST and PRINCIPAL steps.
    /// Standard: all interest then all principal. InterestFirst/PrincipalFirst: lockstep by seniority.
    /// </summary>
    WaterfallOrderEnum WaterfallOrder { get; }
}