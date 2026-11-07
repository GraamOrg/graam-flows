using FluentAssertions;
using GraamFlows.Revolving;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>
///     The pool ledger against closed forms and the American Express Credit Account Master Trust
///     statements for the September and August 2026 monthly periods.
/// </summary>
public class PoolLedgerTests
{
    // American Express Credit Account Master Trust, September 2026 monthly period (30 days).
    private const double SepBeginning = 24_975_208_451.43;
    private const double SepNewReceivables = 12_498_514_556.18;
    private const double SepPrincipal = 12_712_741_903.27;
    private const double SepDefaults = 35_740_822.51;
    private const double SepFinance = 687_263_527.44;
    private const double SepRecoveries = 15_412_642.37;
    private const double SepEnding = 24_725_240_281.83;

    private static PoolActuals September => new()
    {
        Beginning = SepBeginning,
        NewReceivables = SepNewReceivables,
        PrincipalCollections = SepPrincipal,
        ChargeOffs = SepDefaults,
        FinanceCollections = SepFinance,
        Recoveries = SepRecoveries
    };

    [Fact]
    public void ThePrintedSeptemberDollarsCloseToThePrintedEndingBalance()
    {
        PoolLedger.Replay(September, RecoveryBucket.Finance).Ending
            .Should().BeApproximately(SepEnding, 0.01);
    }

    [Fact]
    public void TheSameMonthAsSharesOfBeginningClosesToTheSameBalance()
    {
        var drivers = new PoolDrivers
        {
            PaymentRate = SepPrincipal / SepBeginning,
            NewReceivablesRate = SepNewReceivables / SepBeginning,
            ChargeOffRate = SepDefaults / SepBeginning,
            Yield = SepFinance / SepBeginning,
            RecoveryRate = SepRecoveries / SepBeginning
        };

        PoolLedger.Roll(SepBeginning, drivers, RecoveryBucket.Finance).Ending
            .Should().BeApproximately(SepEnding, 0.01);
    }

    [Fact]
    public void ThePrintedAugustDollarsCloseToSeptembersBeginningBalance()
    {
        var august = new PoolActuals
        {
            Beginning = 24_930_085_370.00,
            NewReceivables = 12_972_421_911.28,
            PrincipalCollections = 12_892_126_005.47,
            ChargeOffs = 35_172_824.38
        };

        PoolLedger.Replay(august, RecoveryBucket.Finance).Ending
            .Should().BeApproximately(SepBeginning, 0.01);
    }

    [Fact]
    public void FinanceRecoveriesAddUpToThePrintedFinanceChargeCollections()
    {
        PoolLedger.Replay(September, RecoveryBucket.Finance).FinanceCash
            .Should().BeApproximately(702_676_169.81, 0.01);
    }

    [Fact]
    public void AllRatesApplyToTheBeginningBalanceInParallel()
    {
        var month = PoolLedger.Roll(1_000.0, new PoolDrivers
        {
            PaymentRate = 0.50,
            NewReceivablesRate = 0.45,
            Yield = 0.02,
            ChargeOffRate = 0.01,
            RecoveryRate = 0.002,
            DilutionRate = 0.005
        }, RecoveryBucket.Finance);

        month.PrincipalCollections.Should().Be(500.0);
        month.NewReceivables.Should().Be(450.0);
        month.FinanceCollections.Should().Be(20.0);
        month.ChargeOffs.Should().Be(10.0);
        month.Recoveries.Should().Be(2.0);
        month.Dilution.Should().Be(5.0);
        month.Ending.Should().Be(935.0);
    }

    [Fact]
    public void NewReceivablesEarnNothingInTheirFirstMonth()
    {
        var withNew = PoolLedger.Roll(1_000.0, new PoolDrivers { Yield = 0.02, NewReceivablesRate = 1.0 },
            RecoveryBucket.Finance);
        var withoutNew = PoolLedger.Roll(1_000.0, new PoolDrivers { Yield = 0.02 }, RecoveryBucket.Finance);

        withNew.FinanceCollections.Should().Be(withoutNew.FinanceCollections);
        withNew.Ending.Should().Be(2_000.0);
    }

    [Fact]
    public void ASequenceOfMonthsCompoundsToTheClosedForm()
    {
        var drivers = new PoolDrivers
        {
            PaymentRate = 0.40,
            NewReceivablesRate = 0.41,
            ChargeOffRate = 0.004,
            DilutionRate = 0.001
        };

        var balance = 1_000_000.0;
        for (var month = 0; month < 24; month++)
            balance = PoolLedger.Roll(balance, drivers, RecoveryBucket.Finance).Ending;

        balance.Should().BeApproximately(1_000_000.0 * Math.Pow(1.005, 24), 1e-6);
    }

    [Theory]
    [InlineData(6.0, 30, 0.49315)]
    [InlineData(6.0, 31, 0.50959)]
    [InlineData(12.0, 28, 0.92055)]
    public void AnAnnualRateIsSimpleOverTheDaysOfTheMonth(double annualPct, int days, double monthlyPct)
    {
        PoolLedger.MonthlyShare(annualPct, days).Should().BeApproximately(monthlyPct, 1e-5);
    }

    [Fact]
    public void AnAnnualRateIsNeverConvertedGeometrically()
    {
        var geometricMonthly = 1.0 - Math.Pow(1.0 - 0.06, 1.0 / 12.0);
        geometricMonthly.Should().BeApproximately(0.005143, 1e-6);

        // A year of simple months adds back to the annual rate; geometric months do not.
        var year = Enumerable.Range(1, 12).Sum(month => DateTime.DaysInMonth(2026, month));
        Enumerable.Range(1, 12).Sum(month => PoolLedger.MonthlyShare(0.06, DateTime.DaysInMonth(2026, month)))
            .Should().BeApproximately(0.06, 1e-15);
        year.Should().Be(365);
    }

    [Fact]
    public void HoldingThePoolReplacesPrincipalCollectionsOnly()
    {
        var month = PoolLedger.Roll(1_000.0, new PoolDrivers
        {
            PaymentRate = 0.30,
            HoldPool = true,
            NewReceivablesRate = 0.99,
            ChargeOffRate = 0.01,
            DilutionRate = 0.002
        }, RecoveryBucket.Finance);

        month.NewReceivables.Should().Be(month.PrincipalCollections);
        month.Ending.Should().Be(1_000.0 - 10.0 - 2.0);
    }

    [Theory]
    [InlineData(40.0, 100.0, 40.0, 0.0)]
    [InlineData(100.0, 100.0, 100.0, 0.0)]
    [InlineData(130.0, 100.0, 100.0, 30.0)]
    public void PrincipalBucketRecoveriesStopAtTheMonthsNewDefaults(double recoveries, double defaults,
        double toPrincipal, double toFinance)
    {
        var month = PoolLedger.Replay(new PoolActuals
        {
            Beginning = 1_000.0,
            ChargeOffs = defaults,
            FinanceCollections = 5.0,
            PrincipalCollections = 200.0,
            NewReceivables = 200.0,
            Recoveries = recoveries
        }, RecoveryBucket.PrincipalUpToNewDefaults);

        month.RecoveriesToPrincipal.Should().Be(toPrincipal);
        month.RecoveriesToFinance.Should().Be(toFinance);
        month.PrincipalCash.Should().Be(200.0 + toPrincipal);
        month.FinanceCash.Should().Be(5.0 + toFinance);
    }

    [Fact]
    public void FinanceBucketRecoveriesAreAllFinance()
    {
        var month = PoolLedger.Replay(new PoolActuals { Beginning = 1_000.0, ChargeOffs = 100.0, Recoveries = 40.0 },
            RecoveryBucket.Finance);

        month.RecoveriesToFinance.Should().Be(40.0);
        month.RecoveriesToPrincipal.Should().Be(0.0);
    }

    [Fact]
    public void RecoveriesDoNotMoveTheLedgerInEitherBucket()
    {
        var without = new PoolActuals { Beginning = 1_000.0, ChargeOffs = 100.0 };
        var with = without with { Recoveries = 40.0 };

        var plain = PoolLedger.Replay(without, RecoveryBucket.Finance).Ending;
        PoolLedger.Replay(with, RecoveryBucket.Finance).Ending.Should().Be(plain);
        PoolLedger.Replay(with, RecoveryBucket.PrincipalUpToNewDefaults).Ending.Should().Be(plain);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AProgramWithNoBucketIsRefusedByName(string? bucket)
    {
        var refuse = () => RecoveryBucketParser.Parse("Example Trust", bucket);

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*Example Trust*recoveryBucket*");
    }

    [Fact]
    public void AnUnknownBucketIsRefusedByName()
    {
        var refuse = () => RecoveryBucketParser.Parse("Example Trust", "PRINCIPAL");

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*Example Trust*PRINCIPAL*");
    }

    [Theory]
    [InlineData("finance", RecoveryBucket.Finance)]
    [InlineData("PRINCIPAL_UP_TO_NEW_DEFAULTS", RecoveryBucket.PrincipalUpToNewDefaults)]
    public void AStatedBucketParses(string bucket, RecoveryBucket parsed)
    {
        RecoveryBucketParser.Parse("Example Trust", bucket).Should().Be(parsed);
    }

    [Fact]
    public void DriversThatTakeMoreThanThePoolHoldsAreRefused()
    {
        var refuse = () => PoolLedger.Roll(1_000.0, new PoolDrivers { PaymentRate = 1.2 }, RecoveryBucket.Finance);

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*negative*");
    }
}
