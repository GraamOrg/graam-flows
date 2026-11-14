using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// A certificate's balance is the overcollateralization: the pool less the notes. The engine set it
/// at the end of every period and never at the start of the first, so the certificate ran its first
/// period on whatever balance its tranche was sent with.
///
/// Sent with zero, as a certificate usually is, a release structure had no balance to pay into: on
/// a deal already above its OC target, the first period's release reached no one and showed nowhere
/// in the output. A certificate sent with no balance now opens at the pool less the notes; one sent
/// with a balance keeps it for the first period, as before. And in every period the row's begin
/// balance was written after the period's payments, so it read as the end balance.
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
    public void Below_its_target_the_deal_releases_nothing_and_the_certificate_is_paid_no_principal()
    {
        // 95M of notes: the deal opens 5M over the pool, under the 10M target, so excess interest
        // pays the notes down and no principal is released.
        var cert = Certificate(Run(notes: 95_000_000));

        cert[0].BeginBalance.Should().BeApproximately(5_000_000, 0.01);
        cert.Should().OnlyContain(r => Math.Abs(Principal(r)) < 0.01);
        cert.Last().Balance.Should().BeGreaterThan(cert[0].BeginBalance, "the turbo builds the OC");
    }
}
