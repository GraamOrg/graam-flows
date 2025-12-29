using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall;

public class DynamicPseudoClass : DynamicClass
{
    public DynamicPseudoClass(DynamicGroup dynamicGroup, ITranche tranche, IList<DynamicTranche> dynamicTranches,
        IEnumerable<DynamicClass> actualClasses) : base(dynamicGroup, tranche, dynamicTranches)
    {
        ActualClasses = actualClasses.ToList();
    }

    public DynamicPseudoClass(DynamicGroup dynamicGroup, ITranche tranche, IEnumerable<DynamicClass> actualClasses) :
        base(dynamicGroup, tranche)
    {
        ActualClasses = actualClasses.ToList();
    }

    public List<DynamicClass> ActualClasses { get; }

    public bool IsApplicableClass(DynamicClass dynamicClass)
    {
        return ActualClasses.Any(ac => ac.Tranche.TrancheName == dynamicClass.Tranche.TrancheName);
    }

    public override double CreditSupport(DateTime cashflowDate)
    {
        var maxSubOrder = ActualClasses.Max(ac => ac.DealStructure.SubordinationOrder);
        var subBal = DynamicGroup.SubordinateClasses(maxSubOrder).Sum(dc => dc.Balance);
        return subBal > 0 ? subBal / DynamicGroup.Balance() : 0;
    }
}