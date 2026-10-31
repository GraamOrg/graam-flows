using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
///     A collateral group that shares no class with another is its own deal: <c>collat_net_wac</c>
///     / <c>collat_wac</c> are its own collateral's rates. A group holding a shared class (group
///     "0") keeps the whole deal's blend. <c>group_net_wac</c> / <c>group_wac</c> are always the
///     group's own.
///
///     A CMBS loan-specific certificate is paid only from its own subordinate companion loan:
///     WFCM 2025-5C7 pays Classes CG-A … CG-HRR from the Crossgates Mall B-note at 14% and its
///     pooled certificates from 25 loans at ~6.57%. On the blend, every net-WAC class on both
///     sides earned the wrong coupon.
/// </summary>
public class GroupNetWacTests
{
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    private const double PoolBal = 100_000_000;
    private const double NoteBal = 50_000_000;
    private const double PoolWac = 6.0;
    private const double NoteWac = 14.0;
    private const double Fee = 0.25; // servicing, annual percent, both groups

    private const double PoolNet = PoolWac - Fee;
    private const double NoteNet = NoteWac - Fee;

    // Both groups amortize at the same rate (no prepayment), so the balance weights stay 2:1.
    private const double Blend = (PoolNet * 2 + NoteNet) / 3;

    private static CollateralCashflows TwoGroups() =>
        new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(FirstPayDate, 6, PoolBal, cpr: 0, cdr: 0, wac: PoolWac, serviceFee: Fee)
            .WithGroupNum("2")
            .WithConstantCashflows(FirstPayDate, 6, NoteBal, cpr: 0, cdr: 0, wac: NoteWac, serviceFee: Fee)
            .Build();

    /// <summary>Group 1 pays A and C from the pool; group 2 pays only N, from the note.</summary>
    private static TestDealBuilder SeparateGroups(string formula) =>
        new TestDealBuilder()
            .WithTranche("A", 90_000_000, 0, subOrder: 0, groupNum: "1", couponFormula: formula)
            .WithTranche("C", 10_000_000, 0, subOrder: 1, groupNum: "1", couponFormula: formula)
            .WithTranche("N", NoteBal, 0, subOrder: 0, groupNum: "2", couponFormula: formula)
            .WithPayRule("G1Interest", "SET_INTEREST_STRUCT(SEQ(SINGLE('A'), SINGLE('C')))")
            .WithPayRule("G1Sched", "SET_SCHED_STRUCT(SEQ(SINGLE('A'), SINGLE('C')))")
            .WithPayRule("G1Prepay", "SET_PREPAY_STRUCT(SEQ(SINGLE('A'), SINGLE('C')))")
            .WithPayRule("G1Recov", "SET_RECOV_STRUCT(SEQ(SINGLE('A'), SINGLE('C')))")
            .WithPayRule("G1Writedown", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('C'), SINGLE('A')))")
            .WithPayRule("G2Interest", "SET_INTEREST_STRUCT(SINGLE('N'))", "GROUP_2")
            .WithPayRule("G2Sched", "SET_SCHED_STRUCT(SINGLE('N'))", "GROUP_2")
            .WithPayRule("G2Prepay", "SET_PREPAY_STRUCT(SINGLE('N'))", "GROUP_2")
            .WithPayRule("G2Recov", "SET_RECOV_STRUCT(SINGLE('N'))", "GROUP_2")
            .WithPayRule("G2Writedown", "SET_WRITEDOWN_STRUCT(SINGLE('N'))", "GROUP_2");

    /// <summary>
    ///     GSMS 2017-GS8's shape: group 2 pays its own class N first, then the shared class S, which
    ///     group 1 pays too. Group 1 holds the shared class, so its classes keep the deal's blend.
    /// </summary>
    private static TestDealBuilder SharedClass() =>
        new TestDealBuilder()
            .WithTranche("A", 90_000_000, 0, subOrder: 0, groupNum: "1", couponFormula: "collat_net_wac")
            .WithTranche("S", 10_000_000, 0, subOrder: 1, groupNum: "0", couponFormula: "collat_net_wac")
            .WithTranche("N", NoteBal, 0, subOrder: 0, groupNum: "2", couponFormula: "group_net_wac")
            .WithPayRule("G1Interest", "SET_INTEREST_STRUCT(SEQ(SINGLE('A'), SINGLE('S')))")
            .WithPayRule("G1Sched", "SET_SCHED_STRUCT(SEQ(SINGLE('A'), SINGLE('S')))")
            .WithPayRule("G1Prepay", "SET_PREPAY_STRUCT(SEQ(SINGLE('A'), SINGLE('S')))")
            .WithPayRule("G1Recov", "SET_RECOV_STRUCT(SEQ(SINGLE('A'), SINGLE('S')))")
            .WithPayRule("G1Writedown", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('S'), SINGLE('A')))")
            .WithPayRule("G2Interest", "SET_INTEREST_STRUCT(SINGLE('N'))", "GROUP_2")
            .WithPayRule("G2Sched", "SET_SCHED_STRUCT(SEQ(SINGLE('N'), SINGLE('S')))", "GROUP_2")
            .WithPayRule("G2Prepay", "SET_PREPAY_STRUCT(SEQ(SINGLE('N'), SINGLE('S')))", "GROUP_2")
            .WithPayRule("G2Recov", "SET_RECOV_STRUCT(SEQ(SINGLE('N'), SINGLE('S')))", "GROUP_2")
            .WithPayRule("G2Writedown", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('S'), SINGLE('N')))", "GROUP_2");

    private static List<TrancheCashflow> Rows(DealCashflows cf, string name)
    {
        var match = cf.TrancheCashflows.FirstOrDefault(t => t.Key.TrancheName == name);
        return match.Value?.Cashflows.OrderBy(c => c.Key).Select(c => c.Value).ToList()
               ?? new List<TrancheCashflow>();
    }

    private static void ShouldEarn(DealCashflows cf, string name, double rate, string because) =>
        Rows(cf, name).Should().NotBeEmpty()
            .And.OnlyContain(c => Math.Abs(c.Coupon - rate) < 1e-6, because);

    [Fact]
    public void SeparateGroups_CollatNetWacIsEachGroupsOwnRate()
    {
        var (_, cf) = SeparateGroups("collat_net_wac").BuildAndRun(TwoGroups());

        ShouldEarn(cf, "A", PoolNet, "group 1 shares no class, so its net WAC is the pool's");
        ShouldEarn(cf, "C", PoolNet, "every group-1 class sees the pool's rate, not the blend");
        ShouldEarn(cf, "N", NoteNet, "group 2 is paid only from the note, at the note's net rate");
    }

    [Fact]
    public void GroupNetWac_IsTheGroupsOwnRate()
    {
        var (_, cf) = SeparateGroups("group_net_wac").BuildAndRun(TwoGroups());

        ShouldEarn(cf, "A", PoolNet, "group_net_wac is the tranche's own group's net rate");
        ShouldEarn(cf, "N", NoteNet, "group_net_wac is the tranche's own group's net rate");
    }

    [Fact]
    public void SeparateGroups_EachGroupPaysOnlyItsOwnClasses()
    {
        var (_, cf) = SeparateGroups("collat_net_wac").BuildAndRun(TwoGroups());

        Rows(cf, "N").First().Interest.Should().BeApproximately(NoteBal * NoteNet / 1200, 0.01,
            "the note's net interest pays N in full");
        Rows(cf, "A").First().Interest.Should().BeApproximately(90_000_000 * PoolNet / 1200, 0.01);
    }

    [Fact]
    public void AGroupHoldingASharedClass_KeepsTheDealsBlend()
    {
        var (_, cf) = SharedClass().BuildAndRun(TwoGroups());

        ShouldEarn(cf, "A", Blend, "group 1 holds the shared class S, so collat_net_wac stays the deal's blend");
        ShouldEarn(cf, "N", NoteNet, "group_net_wac is the note's own rate whether or not a class is shared");
    }
}
