namespace GraamFlows.Objects.DataObjects;

public interface IExchShare
{
    string DealName { get; }
    string ClassGroupName { get; }
    string TrancheName { get; }
    double Quantity { get; }

    /// <summary>
    ///     The component named by <see cref="TrancheName" /> is a TRANCHE, not a class group.
    ///
    ///     A MACR recombination is drawn from a tranche INSIDE another class — STACR 2025-DNA1
    ///     Combination 15 is Class M-2B plus 80% of Class M-2AI, and M-2AI is a tranche of class
    ///     M-2A, not a class of its own. The class-level share cannot name it, which is why a
    ///     name alone will not do here: "M2B" is simultaneously a class and a tranche, so
    ///     resolution has to be declared rather than inferred.
    /// </summary>
    bool ByTranche { get; }
}