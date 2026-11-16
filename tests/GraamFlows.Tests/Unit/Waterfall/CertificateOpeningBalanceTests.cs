using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// A certificate's balance is the overcollateralization: the pool less the notes. The engine set it
/// at the end of every period and never at the start of the first, so the certificate ran its first
/// period on whatever balance its tranche was sent with.
///
/// Sent with zero, as a certificate usually is, it ran its first period at zero: the release
/// structure had no balance to pay into (the release is now paid to it at the end of the period,
/// since #161, but not by its step), a write-down order headed by it found nothing there, and the
/// first period's triggers read it as zero. A certificate sent with no balance now opens at the pool
/// less the notes; one sent with any balance keeps it, as before. And in every period the row's
/// begin balance was written after the period's payments to the certificate.
/// </summary>
public class CertificateOpeningBalanceTests
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

    /// <summary>
    /// Notes on a 100M pool with a 10% OC target and a certificate the release pays. With 80M of
    /// notes the deal opens 20M over the pool, above target, so principal is released from month 1.
    /// </summary>
    private static DealCashflows Run(double notes = 80_000_000, double certificateSentWith = 0) =>
        new TestDealBuilder()
            .WithTranche("A", notes, 5.0, subOrder: 0)
            .WithCertificateTranche(originalBalance: certificateSentWith)
            .WithSequentialWaterfall("A")
            .WithPayRule("ReleaseStruct", "SET_RELEASE_STRUCT(SEQ(SINGLE('Certificates')))")
            .WithOcTarget(0.10, 0)
            .BuildAndRun(Pool()).Cashflows;

    private static List<TrancheCashflow> Certificate(DealCashflows cf) =>
        cf.ClassCashflows.First(c => c.Key.TrancheName == "Certificates").Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).ToList();

    private static List<TrancheCashflow> Notes(DealCashflows cf) =>
        cf.TrancheCashflows.First(c => c.Key.TrancheName == "A").Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).ToList();

    private static double Principal(TrancheCashflow r) => r.ScheduledPrincipal + r.UnscheduledPrincipal;

    [Fact]
    public void Sent_with_no_balance_the_certificate_is_paid_the_first_periods_release()
    {
        var cert = Certificate(Run());

        Principal(cert[0]).Should().BeApproximately(2_000_000, 0.01,
            "the deal opens above its OC target, so month 1's principal is released to the certificate");
        cert.Sum(Principal).Should().BeApproximately(11_200_000, 0.01,
            "12M of principal over six months, of which the notes are paid 0.8M");
    }

    [Fact]
    public void The_first_period_opens_at_the_pool_less_the_notes()
    {
        var cert = Certificate(Run());

        cert[0].BeginBalance.Should().BeApproximately(20_000_000, 0.01);
        cert[0].Balance.Should().BeApproximately(18_000_000, 0.01);
    }

    [Fact]
    public void Every_period_opens_on_the_balance_the_last_one_closed_on()
    {
        var cert = Certificate(Run());

        for (var i = 1; i < cert.Count; i++)
            cert[i].BeginBalance.Should().BeApproximately(cert[i - 1].Balance, 0.01, $"period {i + 1}");
        // With no losses here the balance moves only by what the certificate is paid.
        foreach (var row in cert)
            (row.BeginBalance - Principal(row)).Should().BeApproximately(row.Balance, 0.01);
    }

    [Fact]
    public void A_certificate_that_has_a_balance_is_not_opened_again_from_the_pool()
    {
        // Month 3's pool opens 5M above where month 2 closed (collateral added between periods).
        // The certificate still opens month 3 on the balance it closed month 2 with; the added
        // collateral reaches it at the end of the period, as before.
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 100_000_000.0;
        for (var i = 0; i < 6; i++)
        {
            if (i == 2) bal += 5_000_000;
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: bal, scheduledPrincipal: 2_000_000,
                unscheduledPrincipal: 0, interest: 750_000);
            bal -= 2_000_000;
        }

        var cert = Certificate(new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithCertificateTranche()
            .WithSequentialWaterfall("A")
            .WithPayRule("ReleaseStruct", "SET_RELEASE_STRUCT(SEQ(SINGLE('Certificates')))")
            .WithOcTarget(0.10, 0)
            .BuildAndRun(b.Build()).Cashflows);

        cert[2].BeginBalance.Should().BeApproximately(cert[1].Balance, 0.01);
    }

    [Theory]
    [InlineData(50_000_000)]
    [InlineData(5_000_000)]
    public void Sent_with_a_balance_the_certificate_keeps_it_for_the_first_period(double sentWith)
    {
        // Only a certificate sent with nothing is opened from the pool. A stated balance is the
        // deal's own statement and runs the first period as before; from the end of that period
        // the balance is the pool less the notes, as it always was.
        var cert = Certificate(Run(certificateSentWith: sentWith));

        cert[0].BeginBalance.Should().BeApproximately(sentWith, 0.01);
        cert[0].Balance.Should().BeApproximately(18_000_000, 0.01);
        cert[1].BeginBalance.Should().BeApproximately(18_000_000, 0.01);
    }

    [Fact]
    public void The_notes_are_paid_the_same_whatever_the_certificate_was_sent_with()
    {
        var a = Notes(Run());
        var b = Notes(Run(certificateSentWith: 50_000_000));

        for (var i = 0; i < a.Count; i++)
        {
            Principal(b[i]).Should().BeApproximately(Principal(a[i]), 0.01);
            b[i].Interest.Should().BeApproximately(a[i].Interest, 0.01);
            b[i].Balance.Should().BeApproximately(a[i].Balance, 0.01);
        }
    }

    [Fact]
    public void A_first_period_loss_is_taken_by_the_certificate_when_the_writedown_order_starts_with_it()
    {
        // The pool loses 1M in month 1 and the write-down order is the certificate, then the note.
        // Opened at zero, the certificate had nothing to write down and the note took the loss.
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 100_000_000.0;
        for (var i = 0; i < 3; i++)
        {
            var loss = i == 0 ? 1_000_000.0 : 0.0;
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: bal, scheduledPrincipal: 2_000_000,
                unscheduledPrincipal: 0, interest: 750_000, defaultedPrincipal: loss);
            bal -= 2_000_000 + loss;
        }

        var cf = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithCertificateTranche()
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('Certificates'), SINGLE('A')))")
            .WithExecutionOrder("INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY",
                "WRITEDOWN")
            .BuildAndRun(b.Build()).Cashflows;

        Notes(cf)[0].Writedown.Should().BeApproximately(0, 0.01, "the 20M of OC takes the 1M loss");
        Notes(cf)[0].Balance.Should().BeApproximately(78_000_000, 0.01);
        Certificate(cf)[0].Balance.Should().BeApproximately(19_000_000, 0.01, "97M of pool less 78M of notes");
    }

    [Fact]
    public void Months_folded_into_the_first_distribution_open_at_the_first_months_balance()
    {
        // The pool starts two months before the first Payment Date and those months are folded
        // into it. The Collection Period opened at 100M, not at the last folded month's 96M.
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 100_000_000.0;
        for (var i = -2; i < 4; i++)
        {
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: bal, scheduledPrincipal: 2_000_000,
                unscheduledPrincipal: 0, interest: 750_000);
            bal -= 2_000_000;
        }

        var cert = Certificate(new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithCertificateTranche()
            .WithSequentialWaterfall("A")
            .WithPayRule("ReleaseStruct", "SET_RELEASE_STRUCT(SEQ(SINGLE('Certificates')))")
            .WithOcTarget(0.10, 0)
            .WithFirstPeriodCollateral("Fold")
            .BuildAndRun(b.Build()).Cashflows);

        cert[0].BeginBalance.Should().BeApproximately(20_000_000, 0.01);
        Principal(cert[0]).Should().BeApproximately(6_000_000, 0.01, "three months of principal are released");
        cert[0].Balance.Should().BeApproximately(14_000_000, 0.01);
    }

    [Fact]
    public void The_first_periods_triggers_read_the_certificate_at_its_opening_balance()
    {
        // 20M of OC under an 80M note is 25% credit enhancement from the first day. Opened after
        // the triggers ran, the certificate read as zero and a 20% minimum failed in month 1.
        var cf = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithCertificateTranche()
            .WithSequentialWaterfall("A")
            .WithPayRule("ReleaseStruct", "SET_RELEASE_STRUCT(SEQ(SINGLE('Certificates')))")
            .WithOcTarget(0.10, 0)
            .WithCreditEnhancementTrigger("MinCE", 0.20, "A")
            .BuildAndRun(Pool()).Cashflows;

        var first = cf.TriggerResults.Where(t => t.TriggerName == "MinCE").OrderBy(t => t.CashflowDate).First();
        first.ActualValue.Should().BeGreaterThan(0.20);
        first.Passed.Should().BeTrue();
    }

    [Fact]
    public void A_fall_in_the_balance_is_not_booked_as_principal_in_any_period()
    {
        // The pool loses 1M in each of months 1 and 2 and nothing writes the note down, so the
        // certificate's balance falls by it. A fall is not cash, and no period books it, the one
        // the certificate was opened in included (#161 stopped the end-of-period reset booking it).
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 100_000_000.0;
        for (var i = 0; i < 3; i++)
        {
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: bal, scheduledPrincipal: 2_000_000,
                unscheduledPrincipal: 0, interest: 750_000, defaultedPrincipal: i < 2 ? 1_000_000 : 0);
            bal -= i < 2 ? 3_000_000 : 2_000_000;
        }

        var cert = Certificate(new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithCertificateTranche()
            .WithSequentialWaterfall("A")
            .WithExecutionOrder("INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY")
            .BuildAndRun(b.Build()).Cashflows);

        cert[0].BeginBalance.Should().BeApproximately(20_000_000, 0.01);
        cert[0].Balance.Should().BeApproximately(19_000_000, 0.01);
        Principal(cert[0]).Should().BeApproximately(0, 0.01);
        Principal(cert[1]).Should().BeApproximately(0, 0.01, "a later period does not book the fall either");
        cert[1].Balance.Should().BeApproximately(18_000_000, 0.01);
    }

    [Fact]
    public void Below_its_target_the_deal_releases_nothing_and_the_certificate_is_paid_no_principal()
    {
        // 95M of notes: the deal opens 5M over the pool, under the 10M target, so excess interest
        // pays the notes down and no principal is released.
        var cert = Certificate(Run(notes: 95_000_000));

        cert[0].BeginBalance.Should().BeApproximately(5_000_000, 0.01);
        cert.Should().OnlyContain(r => Math.Abs(Principal(r)) < 0.01);
        cert.Last().Balance.Should().BeGreaterThan(cert[0].BeginBalance, "the turbo builds the OC");
    }

    [Fact]
    public void With_no_release_structure_the_first_periods_release_is_paid_to_the_certificate()
    {
        // The second review's first finding: an OC target and no release structure. The release
        // held back in each period has no step to pay it, so it is paid to the certificate at the
        // end of the period (#161), the first period included, and the ledger has every unit.
        var cf = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithCertificateTranche()
            .WithSequentialWaterfall("A")
            .WithOcTarget(0.10, 0)
            .WithCashLedger()
            .BuildAndRun(Pool()).Cashflows;
        var cert = Certificate(cf);
        var paidToNotes = Notes(cf).Sum(Principal);

        Principal(cert[0]).Should().BeApproximately(2_000_000, 0.01, "the first period's release");
        cert.Sum(Principal).Should().BeApproximately(6 * 2_000_000 - paidToNotes, 0.01,
            "every unit the notes did not get reaches the certificate");
        cf.CashLedger!.Steps.Should().OnlyContain(st => st.Value.TakenNotPaid < 0.01 && st.Value.PaidNotTaken < 0.01);
        cf.CashLedger.LeftPrincipal.Should().BeApproximately(0, 0.01);
    }

    /// <summary>A pool of 100M paying 2M a month, with the losses given by month.</summary>
    private static (CollateralCashflows Pool, List<double> Ends) PoolWithLosses(Func<int, double> loss, int months = 4)
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 100_000_000.0;
        var ends = new List<double>();
        for (var i = 0; i < months; i++)
        {
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: bal, scheduledPrincipal: 2_000_000,
                unscheduledPrincipal: 0, interest: 750_000, defaultedPrincipal: loss(i));
            bal -= 2_000_000 + loss(i);
            ends.Add(bal);
        }

        return (b.Build(), ends);
    }

    // ---- WRITEUP built without the request mapper (graam-harmony#6199 D7) -------------------------

    private static readonly string[] WriteUpOrder =
    {
        "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN", "WRITEUP"
    };

    [Fact]
    public void A_certificate_heading_the_write_down_is_passed_by_WRITEUP()
    {
        var (pool, _) = PoolWithLosses(i => i == 0 ? 25_000_000 : 0);
        var act = () => CertificateFirstInTheWritedownOrder(80_000_000)
            .WithWritedownSize(WritedownSize.OverPool).WithWritesUp().WithExecutionOrder(WriteUpOrder)
            .BuildAndRun(pool);
        act.Should().NotThrow("the certificate is not a note: the write-up passes it by");
    }

    [Fact]
    public void An_order_naming_WRITEUP_on_a_deal_that_does_not_state_it_is_refused()
    {
        var (pool, _) = PoolWithLosses(_ => 0);
        var act = () => CertificateFirstInTheWritedownOrder(80_000_000)
            .WithWritedownSize(WritedownSize.OverPool).WithExecutionOrder(WriteUpOrder)
            .BuildAndRun(pool);
        act.Should().Throw<DealModelingException>().WithMessage("*names WRITEUP but the deal does not state it*");
    }

    [Fact]
    public void WRITEUP_on_a_deal_with_pseudo_classes_is_refused()
    {
        var (pool, _) = PoolWithLosses(_ => 0);
        var act = () => CertificateFirstInTheWritedownOrder(80_000_000)
            .WithWritedownSize(WritedownSize.OverPool).WithWritesUp().WithExecutionOrder(WriteUpOrder)
            .WithPseudoClass("AP", "A")
            .BuildAndRun(pool);
        act.Should().Throw<DealModelingException>().WithMessage("*WRITEUP and pseudo classes*");
    }

    private static TestDealBuilder CertificateFirstInTheWritedownOrder(double notes) => new TestDealBuilder()
        .WithTranche("A", notes, 5.0, subOrder: 0)
        .WithCertificateTranche()
        .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A')))")
        .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A')))")
        .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A')))")
        .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A')))")
        .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('Certificates'), SINGLE('A')))");

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Notes_written_down_to_the_pool_end_every_period_at_the_pool_with_the_certificate_first(int lossMonth)
    {
        // The second review's second finding, with a loss larger than the overcollateralization:
        // 99M of notes on a 100M pool lose 3M, which leaves them 2M over the pool. The
        // certificate holds nothing then, so the write-down to the pool is the note's. Opened at
        // 1M (in the first month) or carried at its last balance (later), the certificate took 1M
        // of it and the note ended the month 1M over the pool, catching up a month late.
        var (pool, ends) = PoolWithLosses(i => i == lossMonth ? 3_000_000 : 0);
        var cf = CertificateFirstInTheWritedownOrder(99_000_000)
            .WithNotesWrittenDownToPool()
            .WithExecutionOrder("INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY")
            .BuildAndRun(pool).Cashflows;

        for (var i = 0; i < ends.Count; i++)
            Notes(cf)[i].Balance.Should().BeApproximately(Math.Min(ends[i], 99_000_000 - 2_000_000 * (i + 1)), 0.01,
                $"period {i + 1}: the notes end at the pool");
        Notes(cf)[lossMonth].Writedown.Should().BeApproximately(2_000_000, 0.01, "the 2M the notes stand over the pool");
        Certificate(cf)[lossMonth].Balance.Should().BeApproximately(0, 0.01);
    }

    [Fact]
    public void An_overPool_writedown_with_the_certificate_first_writes_the_note_down_from_the_first_month()
    {
        // The third review's F3: a 25M loss in the first month on 20M of overcollateralization.
        // The certificate, opened at 20M, holds nothing once the notes stand 5M over the pool, so
        // it takes nothing and the write-down is not refused for writing down a class that is not a note.
        var (pool, ends) = PoolWithLosses(i => i == 0 ? 25_000_000 : 0);
        var cf = CertificateFirstInTheWritedownOrder(80_000_000)
            .WithWritedownSize(WritedownSize.OverPool)
            .WithExecutionOrder("INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY",
                "WRITEDOWN")
            .BuildAndRun(pool).Cashflows;

        Notes(cf)[0].Writedown.Should().BeApproximately(5_000_000, 0.01);
        Notes(cf)[0].Balance.Should().BeApproximately(ends[0], 0.01, "the note ends at the pool");
    }

    [Fact]
    public void A_certificate_sent_with_any_balance_keeps_it_however_small()
    {
        // Only a certificate at zero (under a cent) is opened from the pool. One sent with half a
        // dollar keeps it, so its first release is paid at the end of the period, as on main.
        var cert = Certificate(Run(certificateSentWith: 0.50));
        cert[0].BeginBalance.Should().BeApproximately(0.50, 0.001);
    }

    [Fact]
    public void Every_certificate_class_opens_at_the_overcollateralization()
    {
        // A stated limit: with two certificate classes each opens at the full OC, as each already
        // closes at it at the end of every period. Pinned so a change to it is a decision.
        var cf = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithCertificateTranche("C1", subOrder: 98)
            .WithCertificateTranche("C2", subOrder: 99)
            .WithSequentialWaterfall("A")
            .BuildAndRun(Pool()).Cashflows;
        foreach (var name in new[] { "C1", "C2" })
            cf.ClassCashflows.First(c => c.Key.TrancheName == name).Value.Cashflows
                .OrderBy(c => c.Key).First().Value.BeginBalance.Should().BeApproximately(20_000_000, 0.01, name);
    }
}
