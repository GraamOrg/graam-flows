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
}