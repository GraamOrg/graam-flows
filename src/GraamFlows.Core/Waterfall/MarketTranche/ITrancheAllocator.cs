using GraamFlows.Objects.DataObjects;
using GraamFlows.RulesEngine;
using GraamFlows.Triggers;
using GraamFlows.Waterfall.Structures;

namespace GraamFlows.Waterfall.MarketTranche;

public interface ITrancheAllocator
{
    void AllocateTranches(IWaterfall waterfall, IFormulaExecutor formulaExecutor, IList<DynamicGroup> dynGroups,
        IRateProvider rateProvider, DateTime cfDate, List<TriggerValue> triggerValues, IList<PeriodCashflows> periodCfs,
        IList<DynamicClass> payFromAllocator);

    List<InterestPayment> GetInterestCollateralTranches(IList<DynamicGroup> dynGroup, IRateProvider rateProvider,
        DateTime cfDate, IList<PeriodCashflows> periodCf);
}