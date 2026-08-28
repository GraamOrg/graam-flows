using System.Reflection;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Domain;

public class Deal : IDeal
{
    public Deal(string name, DateTime factorDate)
    {
        Assets = new List<IAsset>();
        Tranches = new List<ITranche>();
        DealStructures = new List<IDealStructure>();
        DealStructurePseudo = new List<IDealStructurePseudo>();
        DealTriggers = new List<IDealTrigger>();
        DealVariables = new List<IDealVariables>();
        DealFieldValues = new List<IDealFieldValue>();
        PayRules = new List<IPayRule>();
        ScheduledVariables = new List<IScheduledVariable>();
        ExchShares = new List<IExchShare>();
        ExecutionOrder = new List<string>();
        DealName = name;
        FactorDate = factorDate;
    }

    public DateTime UpdatedDate { get; set; }
    public string DealName { get; }
    public IList<IAsset> Assets { get; }
    public IList<ITranche> Tranches { get; }
    public IList<IDealStructure> DealStructures { get; }
    public IList<IDealStructurePseudo> DealStructurePseudo { get; }
    public IList<IDealTrigger> DealTriggers { get; }
    public IList<IDealVariables> DealVariables { get; }
    public IList<IDealFieldValue> DealFieldValues { get; }
    public DateTime FactorDate { get; set; }
    public IList<IPayRule> PayRules { get; }
    public IList<IScheduledVariable> ScheduledVariables { get; }
    public IList<IExchShare> ExchShares { get; }

    [Database("Interest_Treatment")] public string InterestTreatment { get; set; }

    [Database("Balance_At_Issuance")] public double BalanceAtIssuance { get; set; }

    public string EncodedRules { get; set; }

    public InterestTreatmentEnum InterestTreatmentEnum
    {
        get
        {
            if (InterestTreatment == null)
                throw new ArgumentException(
                    $"{DealName} must contain an Interest Treatment (Collateral or Guaranteed)");
            if (Enum.TryParse(InterestTreatment, out InterestTreatmentEnum interestTreatment))
                return interestTreatment;
            if (InterestTreatment.ToLower().Contains("col"))
                return InterestTreatmentEnum.Collateral;
            if (InterestTreatment.ToLower().Contains("gu"))
                return InterestTreatmentEnum.Guaranteed;
            throw new ArgumentException($"{DealName} has an invalid interest treatment {InterestTreatment}");
        }
    }

    [Database("Cashflow_Engine")] public string CashflowEngine { get; set; }

    public string WaterfallType { get; set; }
    public IList<string> ExecutionOrder { get; set; }

    public DateTime? CollateralAccrualStartDate { get; set; }
    public string FirstPeriodCollateralPolicy { get; set; }

    /// <summary>
    /// Parsed policy. An UNRECOGNISED value throws rather than silently defaulting: the
    /// two behaviours move money in opposite directions, so quietly picking one would
    /// mis-state a deal without failing.
    /// </summary>
    public FirstPeriodCollateralPolicyEnum FirstPeriodCollateralPolicyEnum
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FirstPeriodCollateralPolicy))
                return FirstPeriodCollateralPolicyEnum.Drop;
            if (Enum.TryParse<FirstPeriodCollateralPolicyEnum>(
                    FirstPeriodCollateralPolicy.Trim(), true, out var parsed))
                return parsed;
            // ArgumentException, not DealModelingException: Domain does not reference Core.
            throw new ArgumentException(
                $"firstPeriodCollateralPolicy '{FirstPeriodCollateralPolicy}' is not recognised. "
                + "Supported: Fold (accumulate pre-first-pay collateral into the first "
                + "distribution) or Drop (exclude it). Omit the field for Drop.");
        }
    }
    public OcTargetConfig? OcTargetConfig { get; set; }
    public ReinvestmentConfig? ReinvestmentConfig { get; set; }
    public IList<CoverageLevelConfig>? CoverageCascade { get; set; }
    public WaterfallOrderEnum WaterfallOrder { get; set; } = WaterfallOrderEnum.Standard;

    public Assembly RuleAssembly { get; set; }
}