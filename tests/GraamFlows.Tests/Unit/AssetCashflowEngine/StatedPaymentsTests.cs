using FluentAssertions;
using GraamFlows.AssetCashflowEngine;
using GraamFlows.Assumptions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// An asset that states its own scheduled payments (<see cref="IAsset.ScheduledPayments" />): an
/// equipment-loan pool whose balance is its receivables' scheduled payments discounted monthly at a
/// stated rate, most of whose obligors pay once a year. Sent as the schedule, the discount rate as
/// the coupon and the discounted value as the balance, it pays the schedule itself; a level payment
/// cannot. All numbers here are synthetic.
/// </summary>
public class StatedPaymentsTests
{
    private const double RatePct = 6.0;
    private const double R = RatePct / 1200.0;
    private static readonly DateTime FirstProjDate = new(2026, 2, 1);

    /// <summary>The schedule's value discounted monthly at the asset rate (payment 1 one month out).</summary>
    private static double PresentValue(double[] payments) =>
        payments.Select((p, i) => p / Math.Pow(1 + R, i + 1)).Sum();

    private static Asset Receivable(double[] payments, double? balance = null) => new()
    {
        AssetName = "EQ",
        AssetId = "EQ",
        InterestRateType = InterestRateType.FRM,
        OriginalDate = FirstProjDate,
        OriginalBalance = balance ?? PresentValue(payments),
        CurrentBalance = balance ?? PresentValue(payments),
        BalanceAtIssuance = balance ?? PresentValue(payments),
        OriginalInterestRate = RatePct,
        CurrentInterestRate = RatePct,
        OriginalAmortizationTerm = payments.Length,
        GroupNum = "1",
        ScheduledPayments = payments
    };

    private static List<PeriodCashflows> Run(IAsset asset, double cpr = 0.0, double cdr = 0.0)
    {
        var anchorAbsT = DateUtil.CalcAbsT(FirstProjDate);
        IAssetAssumptions Assump(IAsset _) => new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, cpr),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, cdr),
            new ConstVector(anchorAbsT, 0.0));
        var result = CfCore.GenerateAssetCashflows(new List<IAsset> { asset }, FirstProjDate, null, Assump, null!);
        return result.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();
    }

    /// <summary>Paid this row: the interest and principal the row collects (prepayments aside).</summary>
    private static double Paid(PeriodCashflows p) => p.Interest + p.ScheduledPrincipal;

    /// <summary>Interest accreted into the balance this row, by the balance identity.</summary>
    private static double Accreted(PeriodCashflows p) =>
        p.Balance - p.BeginBalance + p.ScheduledPrincipal + p.UnscheduledPrincipal + p.DefaultedPrincipal;

    /// <summary>An annual payer: only the January rows pay; 3 years from a February start.</summary>
    private static double[] AnnualSchedule() =>
        Enumerable.Range(0, 36).Select(i => FirstProjDate.AddMonths(i).Month == 1 ? 10_000.0 : 0.0).ToArray();

    [Fact]
    public void A_Balance_At_The_Schedules_Value_Pays_Exactly_The_Schedule()
    {
        // An uneven monthly schedule: seasonal, every amount above the month's interest.
        var payments = Enumerable.Range(0, 48).Select(i => 2_000.0 + 150.0 * (i % 12) + 3.17 * i).ToArray();
        var periods = Run(Receivable(payments));

        periods.Should().HaveCount(payments.Length);
        for (var t = 0; t < payments.Length; t++)
        {
            Paid(periods[t]).Should().BeApproximately(payments[t], 0.005, $"row {t} pays its stated amount");
            periods[t].Interest.Should().BeApproximately(R * periods[t].BeginBalance, 1e-6);
            periods[t].UnscheduledPrincipal.Should().Be(0.0);
        }

        periods[^1].Balance.Should().BeApproximately(0.0, 0.005);
        periods.Sum(p => p.ScheduledPrincipal).Should().BeApproximately(PresentValue(payments), 0.005);
        periods[0].WAM.Should().Be(payments.Length - 1, "the remaining term is the schedule's");
    }

    [Fact]
    public void An_Annual_Payer_Accretes_Between_Payments_And_Pays_Only_In_January()
    {
        var payments = AnnualSchedule();
        var periods = Run(Receivable(payments));

        periods.Should().HaveCount(payments.Length);
        for (var t = 0; t < payments.Length; t++)
        {
            var p = periods[t];
            if (payments[t] == 0.0)
            {
                // Nothing is paid; the month's interest at the rate joins the balance.
                p.Interest.Should().Be(0.0);
                p.ScheduledPrincipal.Should().Be(0.0);
                Accreted(p).Should().BeApproximately(R * p.BeginBalance, 1e-6);
                p.Balance.Should().BeApproximately(p.BeginBalance * (1 + R), 1e-6);
            }
            else
            {
                p.CashflowDate.Month.Should().Be(1);
                p.Interest.Should().BeApproximately(R * p.BeginBalance, 1e-6);
                Paid(p).Should().BeApproximately(payments[t], 0.005);
                Accreted(p).Should().BeApproximately(0.0, 1e-6);
            }
        }

        periods[^1].Balance.Should().BeApproximately(0.0, 0.005);
        periods.Sum(Paid).Should().BeApproximately(payments.Sum(), 0.01);
    }

    [Fact]
    public void A_Payment_Below_The_Interest_Pays_It_As_Interest_And_Accretes_The_Rest()
    {
        var payments = new[] { 10.0, 0.0, 50_000.0 };
        var periods = Run(Receivable(payments, balance: 100_000.0));

        periods[0].Interest.Should().BeApproximately(10.0, 1e-9);
        periods[0].ScheduledPrincipal.Should().Be(0.0);
        periods[0].Balance.Should().BeApproximately(100_000.0 * (1 + R) - 10.0, 1e-6);

        // The last stated payment repays everything left: a balloon above the stated 50,000.
        periods.Should().HaveCount(3);
        var owed = periods[2].BeginBalance * (1 + R);
        Paid(periods[2]).Should().BeApproximately(owed, 1e-6);
        owed.Should().BeGreaterThan(50_000.0);
        periods[2].Balance.Should().Be(0.0);
    }

    [Fact]
    public void Prepayments_Scale_The_Remaining_Schedule_By_Survival()
    {
        var payments = AnnualSchedule();
        var balance = PresentValue(payments);
        var periods = Run(Receivable(payments), cpr: 20.0);
        var smm = 1 - Math.Pow(1 - 0.20, 1.0 / 12);

        var survival = 1.0;
        for (var t = 0; t < payments.Length; t++)
        {
            var p = periods[t];
            if (payments[t] > 0)
                Paid(p).Should().BeApproximately(payments[t] * survival, 0.005, $"row {t}: the stated payment × survival");
            else
                Paid(p).Should().Be(0.0);
            // The prepayment takes its share of the month's accretion too.
            p.UnscheduledPrincipal.Should().BeApproximately(smm * (p.BeginBalance + Accreted(p) - p.ScheduledPrincipal), 1e-6);
            survival *= 1 - smm;
        }

        // Interest accreted (accrual on the beginning balance less the interest paid) is the only
        // thing that adds to the balance: every dollar of it, and of the opening balance, is repaid.
        var accreted = periods.Sum(p => R * p.BeginBalance - p.Interest);
        accreted.Should().BeGreaterThan(0.0);
        periods.Sum(p => p.ScheduledPrincipal + p.UnscheduledPrincipal)
            .Should().BeApproximately(balance + accreted, 0.01);
        periods[^1].Balance.Should().BeApproximately(0.0, 0.005);
    }

    [Fact]
    public void Prepayments_And_Defaults_Keep_The_Balance_A_Fixed_Fraction_Of_The_Schedule()
    {
        var payments = AnnualSchedule();
        var scheduled = Run(Receivable(payments));
        var stressed = Run(Receivable(payments), cpr: 10.0, cdr: 5.0);

        // Every month, prepayments and defaults take the same fraction of the (accreted) balance, so
        // the performing balance is the scheduled one × a survival that falls geometrically.
        var monthly = stressed[1].BeginBalance / scheduled[1].BeginBalance;
        monthly.Should().BeLessThan(1.0);
        for (var t = 1; t < payments.Length; t++)
            (stressed[t].BeginBalance / scheduled[t].BeginBalance)
                .Should().BeApproximately(Math.Pow(monthly, t), 1e-9, $"row {t}");
    }

    [Fact]
    public void An_Asset_Without_A_Schedule_Keeps_Its_Level_Payment()
    {
        var level = Receivable(new double[0], balance: 100_000.0);
        level.OriginalAmortizationTerm = 60;
        var periods = Run(level);

        var pmt = 100_000.0 * R / (1 - Math.Pow(1 + R, -60));
        periods.Should().HaveCount(60);
        Paid(periods[0]).Should().BeApproximately(pmt, 0.01);
        Paid(periods[30]).Should().BeApproximately(pmt, 0.01);
    }

    public static IEnumerable<object[]> Conflicts()
    {
        yield return new object[] { "annual frequency", (Action<Asset>)(a => { a.PaymentFrequency = 1; a.NextPaymentDate = new DateTime(2027, 1, 15); }), "payment frequency" };
        yield return new object[] { "quarterly frequency", (Action<Asset>)(a => a.PaymentFrequency = 4), "payment frequency" };
        yield return new object[] { "stated payment date", (Action<Asset>)(a => a.NextPaymentDate = new DateTime(2026, 2, 15)), "payment date" };
        yield return new object[] { "maturity date", (Action<Asset>)(a => a.MaturityDate = new DateTime(2029, 1, 15)), "maturity date" };
        yield return new object[] { "IO flag", (Action<Asset>)(a => a.IsIO = true), "interest-only" };
        yield return new object[] { "IO term", (Action<Asset>)(a => a.IOTerm = 12), "interest-only" };
        yield return new object[] { "ARM", (Action<Asset>)(a => a.InterestRateType = InterestRateType.ARM), "adjustable rate" };
        yield return new object[] { "ARM reset", (Action<Asset>)(a => a.InitialAdjustmentPeriod = 12), "adjustable rate" };
        yield return new object[] { "PIK", (Action<Asset>)(a => a.AmortizationType = AmortizationType.Pik), "PIK" };
        yield return new object[] { "bullet", (Action<Asset>)(a => a.AmortizationType = AmortizationType.Bullet), "bullet" };
        yield return new object[] { "balloon schedule", (Action<Asset>)(a => a.BalloonAmortizationTerm = 360), "balloon" };
        yield return new object[] { "negative payment", (Action<Asset>)(a => a.ScheduledPayments![3] = -1.0), "not negative" };
        yield return new object[] { "NaN payment", (Action<Asset>)(a => a.ScheduledPayments![3] = double.NaN), "finite" };
    }

    [Theory]
    [MemberData(nameof(Conflicts))]
    public void A_Schedule_Is_Refused_With_A_Feature_It_Would_Only_Half_Honour(string _, Action<Asset> conflict,
        string says)
    {
        var asset = Receivable(AnnualSchedule());
        conflict(asset);

        FluentActions.Invoking(() => Run(asset))
            .Should().Throw<ArgumentException>()
            .Where(e => e.Message.Contains(says) && e.ParamName == nameof(IAsset.ScheduledPayments));
    }

    [Fact]
    public void The_Amortizer_Refuses_A_Schedule_On_Longer_Than_Monthly_Periods()
    {
        var data = new AssetDataArrays(new List<IAsset> { Receivable(AnnualSchedule()) });
        double[][] Rows(double v) => new[] { Enumerable.Repeat(v, 36).ToArray() };
        var startT = DateUtil.CalcAbsT(FirstProjDate);

        FluentActions.Invoking(() => Amortizer.GenerateCashflows(data, startT, startT + 35,
                Rows(0), Rows(0), Rows(0), Rows(0), Rows(1), Rows(1), Rows(-1), Rows(1), Rows(-1), null!,
                monthsPerPeriod: 3))
            .Should().Throw<NotSupportedException>();
    }
}
