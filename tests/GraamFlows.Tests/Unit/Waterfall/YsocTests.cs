using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// Yield supplement overcollateralization (YSOC). A pool with subsidized-APR receivables is
/// discounted by its YSOC: the notes are paid down to the Adjusted Pool Balance (the pool less the
/// period's YSOC) less the OC target. Measured on the pool, the YSOC reads as overcollateralization
/// above target and the release holds that much principal back from the notes — a captive auto
/// trust's Class A-1 came out at 0.89 years against the 0.49 its prospectus prints.
/// </summary>
public class YsocTests
{
    private static readonly DateTime FirstPay = TestConstants.DefaultFirstPayDate;

    /// <summary>100M pool repaying 2M a month, 750k of interest.</summary>
    private static CollateralCashflows Pool()
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 100_000_000.0;
        for (var i = 0; i < 6; i++)
        {
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: bal, scheduledPrincipal: 2_000_000,
                unscheduledPrincipal: 0, interest: 750_000);
            bal -= 2_000_000;
        }

        return b.Build();
    }

    /// <summary>A YSOC of 20M stepping down 0.5M a month, keyed by each Payment Date's month.</summary>
    private static YsocSchedule Schedule() => new()
    {
        Amounts = Enumerable.Range(0, 6).ToDictionary(
            i => (FirstPay.AddMonths(i).Year, FirstPay.AddMonths(i).Month), i => 20_000_000 - 500_000.0 * (i + 1))
    };

    /// <summary>
    /// 80M of notes on the 100M pool, a 1% OC target and a certificate the release pays. On the
    /// pool the deal opens 20M over its notes, far above target. On the Adjusted Pool Balance
    /// (100M less a 20M YSOC) it opens level with them, below target.
    /// </summary>
    private static DealCashflows Run(YsocSchedule? ysoc) =>
        new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithCertificateTranche()
            .WithSequentialWaterfall("A")
            .WithPayRule("ReleaseStruct", "SET_RELEASE_STRUCT(SEQ(SINGLE('Certificates')))")
            .WithOcTarget(new OcTargetConfig { TargetPct = 0.01, Ysoc = ysoc })
            .BuildAndRun(Pool()).Cashflows;

    private static List<TrancheCashflow> Rows(DealCashflows cf, string name) =>
        cf.TrancheCashflows.First(c => c.Key.TrancheName == name).Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).ToList();

    private static List<TrancheCashflow> Certificate(DealCashflows cf) =>
        cf.ClassCashflows.First(c => c.Key.TrancheName == "Certificates").Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).ToList();

    private static double Principal(TrancheCashflow r) => r.ScheduledPrincipal + r.UnscheduledPrincipal;

    [Fact]
    public void Without_a_YSOC_the_pool_above_the_notes_is_released()
    {
        var cert = Certificate(Run(null));

        Principal(cert[0]).Should().BeApproximately(2_000_000, 0.01,
            "98M of pool over 80M of notes is far above a 1% target, so the month's principal is released");
    }

    [Fact]
    public void With_a_YSOC_the_notes_are_held_at_the_adjusted_pool_less_its_target()
    {
        // Month 1 opens with the notes level with the adjusted pool, below target: the notes are
        // paid the month's 2M and turbo-paid the rest of the way, to 99% of 98M - 19.5M. From then
        // on the release gives back only what stands above that, so every month closes with the
        // notes at 99% of the pool less that month's YSOC.
        var a = Rows(Run(Schedule()), "A");

        for (var i = 0; i < a.Count; i++)
        {
            var adjusted = 100_000_000 - 2_000_000.0 * (i + 1) - (20_000_000 - 500_000.0 * (i + 1));
            a[i].Balance.Should().BeApproximately(0.99 * adjusted, 1.0, $"period {i + 1}");
        }
    }

    [Fact]
    public void With_a_YSOC_the_release_is_what_the_adjusted_pool_stands_above_target()
    {
        var cert = Certificate(Run(Schedule()));

        Principal(cert[0]).Should().BeApproximately(0, 0.01,
            "month 1 closes with the notes below target on the adjusted pool, so nothing is released");
        // The adjusted pool falls 1.5M a month (2M of principal, 0.5M less YSOC) and the notes
        // 99% of that: the release is the 2M collected less the notes' 1.485M.
        Principal(cert[1]).Should().BeApproximately(515_000, 1.0);
    }

    [Fact]
    public void Once_a_switch_class_is_paid_off_the_waterfall_runs_on_the_second_schedule()
    {
        // A1 (2M) is paid off in month 1, on the first schedule (notes to 99% of 98M - 19.5M). In
        // month 2 it opens with no balance, so the second schedule's 10M applies: the adjusted pool
        // (96M - 10M) stands far above the 77.715M of notes and the month's principal is released.
        // On the first schedule the notes would have been paid down to 99% of 96M - 19M.
        var schedule = Schedule() with
        {
            After = new YsocSwitch
            {
                Classes = new[] { "A1" },
                Amounts = Enumerable.Range(0, 6).ToDictionary(
                    i => (FirstPay.AddMonths(i).Year, FirstPay.AddMonths(i).Month), _ => 10_000_000.0)
            }
        };
        var cf = new TestDealBuilder()
            .WithTranche("A1", 2_000_000, 5.0, subOrder: 0)
            .WithTranche("A2", 78_000_000, 5.0, subOrder: 1)
            .WithCertificateTranche()
            .WithSequentialWaterfall("A1", "A2")
            .WithPayRule("ReleaseStruct", "SET_RELEASE_STRUCT(SEQ(SINGLE('Certificates')))")
            .WithOcTarget(new OcTargetConfig { TargetPct = 0.01, Ysoc = schedule })
            .BuildAndRun(Pool()).Cashflows;

        Rows(cf, "A1")[0].Balance.Should().BeApproximately(0, 0.01);
        var a2 = Rows(cf, "A2");
        a2[0].Balance.Should().BeApproximately(0.99 * 78_500_000, 1.0);
        a2[1].Balance.Should().BeApproximately(0.99 * 78_500_000, 1.0,
            "month 2 runs on the second schedule, which leaves the notes above target");
    }

    [Fact]
    public void The_schedule_is_read_by_the_Payment_Dates_month()
    {
        var s = Schedule();
        s.AmountOn(FirstPay, _ => false).Should().Be(19_500_000);
        var third = FirstPay.AddMonths(2);
        s.AmountOn(new DateTime(third.Year, third.Month, 1), _ => false)
            .Should().Be(18_500_000, "any day of the Payment Date's month reads that month");
    }

    [Fact]
    public void Before_its_first_month_the_schedule_holds_its_first_amount_and_after_its_last_it_is_zero()
    {
        var s = Schedule();
        s.AmountOn(FirstPay.AddMonths(-1), _ => false).Should().Be(19_500_000);
        s.AmountOn(FirstPay.AddMonths(6), _ => false).Should().Be(0);
    }

    [Fact]
    public void Once_the_named_classes_are_paid_in_full_the_second_schedule_applies()
    {
        var after = new YsocSwitch
        {
            Classes = new[] { "A2a", "A2b" },
            Amounts = new Dictionary<(int, int), double> { [(FirstPay.Year, FirstPay.Month)] = 7_000_000 }
        };
        var s = Schedule() with { After = after };

        s.AmountOn(FirstPay, _ => false).Should().Be(19_500_000);
        IReadOnlyList<string>? asked = null;
        s.AmountOn(FirstPay, c => { asked = c; return true; }).Should().Be(7_000_000);
        asked.Should().BeEquivalentTo(new[] { "A2a", "A2b" });
    }
}
