using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures;

/// <summary>
///     What a Modification Loss Priority rung does to the classes it names.
/// </summary>
public enum ModificationLossEffect
{
    /// <summary>
    ///     Reduce the Class Notional Amount, capped at the rung's Preliminary Class Notional
    ///     Amount. Agency CRT: these amounts are "included in the Principal Loss Amount", so they
    ///     erode subordination and therefore credit enhancement.
    /// </summary>
    Notional,

    /// <summary>
    ///     Reduce the period's Interest Accrual Amount, capped at it. Absorbed as reduced
    ///     interest — the notional is untouched, so credit enhancement is untouched too.
    /// </summary>
    Interest
}

/// <summary>
///     One priority of a Modification Loss Priority.
///
///     Agency CRT allocates a Modification Loss Amount down a ladder that is NOT the Tranche
///     Write-down Priority: above the first-loss class every level takes an interest-accrual-sized
///     bite before a notional one (STACR 2025-DNA1, "Allocation of Modification Loss Amount",
///     printed p.83-84 — thirteen priorities, alternating). A single-amount write-down leg walking
///     a single structure can express the FIRST priority and none of the rest, which is the
///     boundary graam-harmony #4777 stopped at and #4794 removes.
/// </summary>
public class ModificationLossRung
{
    public ModificationLossRung(ModificationLossEffect effect, IPayable target, string capClass = null)
    {
        Effect = effect;
        Target = target;
        CapClass = capClass;
    }

    public ModificationLossEffect Effect { get; }

    /// <summary>
    ///     The classes this rung names — a SINGLE, or a PRORATA over the pair. Reusing IPayable
    ///     means the pro-rata split is the engine's own, not a second implementation of it.
    /// </summary>
    public IPayable Target { get; }

    /// <summary>
    ///     For an <see cref="ModificationLossEffect.Interest" /> rung over a pro-rata pair, the
    ///     ONE class whose Interest Accrual Amount states the cap. The document writes the cap on
    ///     a member, not on the group: "to the Class M-2B and Class M-2BH Reference Tranches, pro
    ///     rata based on their Class Notional Amounts ... until the amount allocated to the Class
    ///     M-2B Reference Tranche is equal to the Class M-2B Notes Interest Accrual Amount". So
    ///     the rung's capacity is that class's accrual grossed up by its pro-rata weight, not the
    ///     pair's combined accrual — the retained H class takes its share alongside.
    ///
    ///     Contrast the NOTIONAL rungs, which the same document caps on the AGGREGATE ("until the
    ///     aggregate amount allocated to the Class M-2B and Class M-2BH Reference Tranches is
    ///     equal to the aggregate of the Preliminary Class Notional Amounts"). The two rung kinds
    ///     genuinely read their caps differently, which is why one shared cap expression on a SEQ
    ///     child could not express this ladder.
    ///
    ///     Null for a single-class rung, where the target IS the capped class.
    /// </summary>
    public string CapClass { get; }
}

/// <summary>
///     A deal's Modification Loss Priority: the ordered rungs, plus the class whose Class Notional
///     Amount absorbs the notional bites.
/// </summary>
public class ModificationLossLadder
{
    public ModificationLossLadder(IList<ModificationLossRung> rungs)
    {
        Rungs = rungs ?? new List<ModificationLossRung>();
    }

    public IList<ModificationLossRung> Rungs { get; }

    /// <summary>
    ///     The class the notional bites are transferred TO, or null when the deal states none.
    ///
    ///     A modification notional bite is a TRANSFER up the stack, not a destruction: "the Class
    ///     Notional Amount for the Class A-H Reference Tranche will be increased by the sum of
    ///     amounts included in the first, third, fifth, eighth, ninth, eleventh and thirteenth
    ///     priorities above". The junior notional falls and the retained senior rises by the same
    ///     amount, so the reference tranches still sum to the reference pool.
    ///
    ///     Getting this wrong biases credit enhancement, which is what the whole axis moves
    ///     through. Credit support here is `SubordinateBalance / DynamicGroup.Balance()`, and
    ///     `Balance()` sums the CLASSES, not the pool. Without the transfer a bite of X gives
    ///     `(S-X)/(T-X)` where the document gives `(S-X)/T` — and since S &lt; T the engine's
    ///     ratio is the HIGHER one, so a Minimum Credit Enhancement Test trips later than it
    ///     should. On STACR 2025-DNA1 that test opens with 0.63bp of headroom, so the direction
    ///     matters more than the size.
    ///
    ///     Null leaves the bite as a pure reduction — the behaviour every non-CRT deal has today,
    ///     and the reason this is declared per deal rather than assumed.
    /// </summary>
    public DynamicClass WriteUpClass { get; set; }

    /// <summary>
    ///     The rung's capacity for this period, BEFORE anything a credit event has already taken.
    ///     Pure — it reads balances and accruals and mutates nothing, which is what lets the step
    ///     compute the whole allocation against "immediately prior to such Payment Date" balances.
    /// </summary>
    public static double RungCapacity(ModificationLossRung rung, DateTime cfDate, IRateProvider rateProvider,
        IEnumerable<DynamicTranche> allTranches)
    {
        if (rung.Target == null)
            return 0;

        if (rung.Effect == ModificationLossEffect.Notional)
            // WritedownCapacity, not CurrentBalance: an excess-spread strip or a REMIC residual
            // has no principal to write down, and reading its notional balance as capacity would
            // silently swallow the allocation there instead of passing it down the ladder.
            return Math.Max(rung.Target.WritedownCapacity(cfDate), 0);

        var leaves = rung.Target.Leafs().OfType<DynamicClass>().ToList();
        if (leaves.Count == 0)
            return 0;

        if (string.IsNullOrEmpty(rung.CapClass))
            return Math.Max(leaves.Sum(l => l.ModificationInterestAccrual(cfDate, rateProvider, allTranches)), 0);

        var capped = leaves.FirstOrDefault(l =>
            string.Equals(l.Tranche.TrancheName, rung.CapClass, StringComparison.OrdinalIgnoreCase));
        if (capped == null)
            // Unreachable in a deal built through the normal path: ML_INTEREST refuses a cap
            // class the rung does not allocate to, and ML_NOTIONAL / ML_INTEREST refuse a rung
            // whose classes the roster does not carry. Kept as a floor rather than a throw
            // because this runs per period inside the allocation walk, and OVER-stating the rung
            // (by falling back to the group's whole accrual) would absorb allocation the classes
            // below it are owed. An earlier comment here claimed the step "reports the skew";
            // nothing did, which is why the refusal moved to rung construction.
            return 0;

        var groupBalance = leaves.Sum(l => l.CurrentBalance(cfDate));
        var cappedBalance = capped.CurrentBalance(cfDate);
        if (groupBalance <= 0 || cappedBalance <= 0)
            return 0;

        // Gross the capped class's accrual up by its pro-rata weight: allocation stops when the
        // capped class's SHARE reaches its Interest Accrual Amount, and the sibling takes its own
        // share alongside on the way there.
        var cappedAccrual = capped.ModificationInterestAccrual(cfDate, rateProvider, allTranches);
        return Math.Max(cappedAccrual * groupBalance / cappedBalance, 0);
    }
}
