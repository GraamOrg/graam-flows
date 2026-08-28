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
    /// Date from which collateral cashflows participate in distributions. Null keeps the
    /// derived boundary (first-of-month of the first tranche's FirstPayDate), so an
    /// existing caller is unaffected.
    /// </summary>
    DateTime? CollateralAccrualStartDate { get; }

    /// <summary>
    /// What to do with collateral periods dated before that boundary. Null means Fold —
    /// what the engine did on every path where the two policies differ, and the only one
    /// of them that conserves principal. Drop pays the excluded stub to nobody and writes
    /// it down nowhere.
    /// </summary>
    string FirstPeriodCollateralPolicy { get; }

    FirstPeriodCollateralPolicyEnum FirstPeriodCollateralPolicyEnum { get; }

    /// <summary>
    /// Configuration for OC turbo paydown step (optional).
    /// </summary>
    OcTargetConfig? OcTargetConfig { get; }

    /// <summary>
    /// Configuration for a revolving / reinvesting collateral pool (optional).
    /// Input only; the reinvestment loop that consumes it is tracked in
    /// graam-flows#49.
    /// </summary>
    ReinvestmentConfig? ReinvestmentConfig { get; }

    /// <summary>
    /// CLO per-level OC/IC coverage tests with interest→principal diversion cure
    /// (optional). Ordered senior→junior; executed inside the INTEREST step by
    /// ComposableStructure. Distinct from <see cref="OcTargetConfig"/>, the
    /// single-level RMBS-style OC turbo.
    /// </summary>
    IList<CoverageLevelConfig>? CoverageCascade { get; }

    /// <summary>
    /// Controls interleaving of INTEREST and PRINCIPAL steps.
    /// Standard: all interest then all principal. InterestFirst/PrincipalFirst: lockstep by seniority.
    /// </summary>
    WaterfallOrderEnum WaterfallOrder { get; }
}