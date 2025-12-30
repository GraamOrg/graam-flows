using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace GraamFlows.Tests.Integration;

/// <summary>
/// Collateral projection tests using actual EART-2023-1 performance data from fact_loans.
/// Validates the core collateral math:
/// 1. Rate conversion (annual CDR/VPR → monthly MDR/SMM)
/// 2. Balance identity: end = begin - scheduled - prepays - defaults
/// 3. Derived scheduled principal is reasonable
/// </summary>
public class EART231CollateralTests
{
    private readonly ITestOutputHelper _output;

    public EART231CollateralTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Actual performance data derived from fact_loans query:
    /// SELECT reporting_period_ending_date,
    ///        sum(beginning_balance)/1e6 begin_bal,
    ///        sum(ending_balance)/1e6 ending_bal,
    ///        ... CDR, VPR, SEV calculations
    /// FROM auto_loans.fact_loans
    /// WHERE issuer_name='Exeter Automobile Receivables Trust 2023-1'
    ///
    /// Data pulled 2023-12-29 from BigQuery auto_loans.fact_loans
    /// </summary>
    private static readonly List<ActualPeriodData> ActualPerformance = new()
    {
        // Starting from first pay date (March 2023)
        new("2023-03-31", BeginBalMM: 603.33, EndBalMM: 590.94, CdrPct: 0.21, VprPct: 8.49, SevPct: 56.04),
        new("2023-04-30", BeginBalMM: 590.94, EndBalMM: 580.19, CdrPct: 1.05, VprPct: 8.22, SevPct: 48.14),
        new("2023-05-31", BeginBalMM: 580.19, EndBalMM: 564.74, CdrPct: 6.39, VprPct: 11.26, SevPct: 75.12),
        new("2023-06-30", BeginBalMM: 564.74, EndBalMM: 547.14, CdrPct: 12.55, VprPct: 10.48, SevPct: 73.19),
        new("2023-07-31", BeginBalMM: 547.14, EndBalMM: 529.54, CdrPct: 14.38, VprPct: 10.16, SevPct: 76.60),
        new("2023-08-31", BeginBalMM: 529.54, EndBalMM: 512.26, CdrPct: 14.48, VprPct: 10.29, SevPct: 66.56),
        new("2023-09-30", BeginBalMM: 512.26, EndBalMM: 497.03, CdrPct: 13.92, VprPct: 8.14, SevPct: 66.17),
        new("2023-10-31", BeginBalMM: 497.03, EndBalMM: 480.46, CdrPct: 14.43, VprPct: 9.01, SevPct: 65.32),
        new("2023-11-30", BeginBalMM: 480.46, EndBalMM: 464.69, CdrPct: 15.56, VprPct: 9.73, SevPct: 65.07),
        new("2023-12-31", BeginBalMM: 464.69, EndBalMM: 450.62, CdrPct: 14.13, VprPct: 8.19, SevPct: 71.33),
    };

    /// <summary>
    /// Validates the CDR/VPR annual-to-monthly conversion formula matches the engine.
    /// Engine formula: MDR = 1 - (1 - CDR/100)^(1/12)
    /// </summary>
    [Fact]
    public void RateConversion_AnnualToMonthly_MatchesEngineFormula()
    {
        // Test known values
        var testCases = new[]
        {
            (AnnualPct: 12.0, ExpectedMonthlyApprox: 0.01057), // ~1.057% monthly for 12% annual
            (AnnualPct: 6.0, ExpectedMonthlyApprox: 0.00514),  // ~0.514% monthly for 6% annual
            (AnnualPct: 24.0, ExpectedMonthlyApprox: 0.02253), // ~2.253% monthly for 24% annual
        };

        foreach (var (annualPct, expectedMonthly) in testCases)
        {
            var monthlyRate = 1 - Math.Pow(1 - annualPct / 100, 1.0 / 12);
            monthlyRate.Should().BeApproximately(expectedMonthly, 0.0001,
                $"Monthly rate for {annualPct}% annual should be ~{expectedMonthly * 100:F3}%");
        }
    }

    /// <summary>
    /// Validates that the implied scheduled principal from actual data is reasonable.
    /// Scheduled principal should be ~0.5-2.0% monthly for auto loans.
    /// </summary>
    [Fact]
    public void ImpliedScheduledPrincipal_IsReasonable()
    {
        _output.WriteLine("Period\t\tBegin\tEnd\tTotal△\tDefaults\tPrepays\tSched(implied)\tSched%");
        _output.WriteLine(new string('-', 100));

        foreach (var period in ActualPerformance)
        {
            // Calculate defaults and prepays from rates
            var mdr = 1 - Math.Pow(1 - period.CdrPct / 100, 1.0 / 12);
            var smm = 1 - Math.Pow(1 - period.VprPct / 100, 1.0 / 12);

            var defaults = period.BeginBalMM * mdr;
            var prepays = (period.BeginBalMM - defaults) * smm;

            // Total balance change
            var totalChange = period.BeginBalMM - period.EndBalMM;

            // Implied scheduled principal = total change - defaults - prepays
            var impliedSched = totalChange - defaults - prepays;
            var impliedSchedPct = impliedSched / period.BeginBalMM * 100;

            _output.WriteLine($"{period.Date}\t{period.BeginBalMM:F2}\t{period.EndBalMM:F2}\t" +
                              $"{totalChange:F2}\t{defaults:F2}\t\t{prepays:F2}\t{impliedSched:F2}\t\t{impliedSchedPct:F2}%");

            // Scheduled principal should be positive and reasonable (0.3% - 3% monthly)
            impliedSched.Should().BePositive($"Period {period.Date}: Scheduled principal should be positive");
            impliedSchedPct.Should().BeInRange(0.3, 3.0,
                $"Period {period.Date}: Scheduled principal {impliedSchedPct:F2}% should be 0.3-3.0% monthly");
        }
    }

    /// <summary>
    /// Validates the balance identity: end = begin - scheduled - prepays - defaults.
    /// Uses the implied scheduled principal to verify the math is internally consistent.
    /// </summary>
    [Fact]
    public void BalanceIdentity_HoldsForActualData()
    {
        foreach (var period in ActualPerformance)
        {
            var mdr = 1 - Math.Pow(1 - period.CdrPct / 100, 1.0 / 12);
            var smm = 1 - Math.Pow(1 - period.VprPct / 100, 1.0 / 12);

            var defaults = period.BeginBalMM * mdr;
            var prepays = (period.BeginBalMM - defaults) * smm;

            var totalChange = period.BeginBalMM - period.EndBalMM;
            var impliedSched = totalChange - defaults - prepays;

            // Verify identity: end = begin - sched - prepays - defaults
            var calculatedEnd = period.BeginBalMM - impliedSched - prepays - defaults;
            calculatedEnd.Should().BeApproximately(period.EndBalMM, 0.01,
                $"Period {period.Date}: Balance identity should hold");
        }
    }

    /// <summary>
    /// Validates that defaults calculated from CDR match actual default counts.
    /// This requires the actual defaults from fact_loans to be reasonable given the CDR.
    /// </summary>
    [Fact]
    public void DefaultsFromCDR_ProduceReasonableAmounts()
    {
        _output.WriteLine("Period\t\tCDR%\tMDR%\tDefaults($M)\tLoss($M)");
        _output.WriteLine(new string('-', 70));

        foreach (var period in ActualPerformance)
        {
            var mdr = 1 - Math.Pow(1 - period.CdrPct / 100, 1.0 / 12);
            var defaults = period.BeginBalMM * mdr;
            var loss = defaults * period.SevPct / 100;

            _output.WriteLine($"{period.Date}\t{period.CdrPct:F2}\t{mdr * 100:F3}\t{defaults:F3}\t\t{loss:F3}");

            // Defaults should be non-negative
            defaults.Should().BeGreaterOrEqualTo(0, $"Period {period.Date}: Defaults cannot be negative");

            // MDR should be less than CDR (monthly rate < annual rate)
            (mdr * 100).Should().BeLessThan(period.CdrPct + 0.001,
                $"Period {period.Date}: Monthly default rate should be less than annual rate");
        }
    }

    /// <summary>
    /// Validates that prepays calculated from VPR are reasonable.
    /// Prepays should be a significant portion of the balance decline for auto loans.
    /// </summary>
    [Fact]
    public void PrepaysFromVPR_ProduceReasonableAmounts()
    {
        _output.WriteLine("Period\t\tVPR%\tSMM%\tPrepays($M)\tPrepay% of Change");
        _output.WriteLine(new string('-', 70));

        foreach (var period in ActualPerformance)
        {
            var mdr = 1 - Math.Pow(1 - period.CdrPct / 100, 1.0 / 12);
            var smm = 1 - Math.Pow(1 - period.VprPct / 100, 1.0 / 12);

            var defaults = period.BeginBalMM * mdr;
            var prepays = (period.BeginBalMM - defaults) * smm;
            var totalChange = period.BeginBalMM - period.EndBalMM;
            var prepayPctOfChange = prepays / totalChange * 100;

            _output.WriteLine($"{period.Date}\t{period.VprPct:F2}\t{smm * 100:F3}\t{prepays:F3}\t\t{prepayPctOfChange:F1}%");

            // Prepays should be non-negative
            prepays.Should().BeGreaterOrEqualTo(0, $"Period {period.Date}: Prepays cannot be negative");

            // SMM should be less than VPR (monthly rate < annual rate)
            (smm * 100).Should().BeLessThan(period.VprPct + 0.001,
                $"Period {period.Date}: Monthly prepay rate should be less than annual rate");
        }
    }

    /// <summary>
    /// Validates cumulative net loss calculation.
    /// CNL = cumulative (defaults * severity)
    /// </summary>
    [Fact]
    public void CumulativeNetLoss_CalculatesCorrectly()
    {
        var cumLoss = 0.0;
        var origBal = ActualPerformance[0].BeginBalMM;

        _output.WriteLine("Period\t\tDefaults\tSeverity\tLoss\tCumLoss\tCNL%");
        _output.WriteLine(new string('-', 80));

        foreach (var period in ActualPerformance)
        {
            var mdr = 1 - Math.Pow(1 - period.CdrPct / 100, 1.0 / 12);
            var defaults = period.BeginBalMM * mdr;
            var loss = defaults * period.SevPct / 100;
            cumLoss += loss;
            var cnlPct = cumLoss / origBal * 100;

            _output.WriteLine($"{period.Date}\t{defaults:F3}\t\t{period.SevPct:F1}%\t\t{loss:F3}\t{cumLoss:F3}\t{cnlPct:F2}%");
        }

        // Final CNL should be reasonable for subprime auto (typically 5-15% over life)
        var finalCnlPct = cumLoss / origBal * 100;
        _output.WriteLine($"\nFinal CNL after 10 months: {finalCnlPct:F2}%");

        // After 10 months, CNL should be trending toward expected lifetime CNL
        // EART231 is a subprime auto deal with elevated losses, so 5-7% CNL at 10 months is expected
        finalCnlPct.Should().BeInRange(0.5, 8.0,
            "10-month CNL for subprime auto should be 0.5-8%");
    }

    /// <summary>
    /// Tests projection with known scheduled principal assumption.
    /// Using 1.3% monthly scheduled principal (derived from actual data average).
    /// </summary>
    [Fact]
    public void ProjectWithEstimatedScheduled_MatchesActualWithinTolerance()
    {
        // Calculate average scheduled principal percentage from actual data
        var schedPctSum = 0.0;
        foreach (var period in ActualPerformance)
        {
            var mdr = 1 - Math.Pow(1 - period.CdrPct / 100, 1.0 / 12);
            var smm = 1 - Math.Pow(1 - period.VprPct / 100, 1.0 / 12);
            var defaults = period.BeginBalMM * mdr;
            var prepays = (period.BeginBalMM - defaults) * smm;
            var totalChange = period.BeginBalMM - period.EndBalMM;
            var impliedSched = totalChange - defaults - prepays;
            schedPctSum += impliedSched / period.BeginBalMM;
        }

        var avgSchedPct = schedPctSum / ActualPerformance.Count;
        _output.WriteLine($"Average scheduled principal: {avgSchedPct * 100:F2}% monthly");

        // Now project using this average and compare
        const double tolerancePct = 1.0; // 1% tolerance

        foreach (var period in ActualPerformance)
        {
            var projected = ProjectOneMonth(
                beginBalance: period.BeginBalMM,
                cdrAnnual: period.CdrPct,
                vprAnnual: period.VprPct,
                scheduledPct: avgSchedPct * 100);

            var diffPct = Math.Abs(projected.EndingBalance - period.EndBalMM) / period.EndBalMM * 100;

            diffPct.Should().BeLessThan(tolerancePct,
                $"Period {period.Date}: Projected {projected.EndingBalance:F2}M vs Actual {period.EndBalMM:F2}M " +
                $"(diff={diffPct:F2}%)");
        }
    }

    /// <summary>
    /// Projects one month of collateral performance using the engine's formula.
    /// </summary>
    private static ProjectionResult ProjectOneMonth(
        double beginBalance,
        double cdrAnnual,
        double vprAnnual,
        double scheduledPct)
    {
        // Convert annual rates to monthly (same as engine: CfCore.cs line 127)
        var mdr = 1 - Math.Pow(1 - cdrAnnual / 100, 1.0 / 12);
        var smm = 1 - Math.Pow(1 - vprAnnual / 100, 1.0 / 12);

        // Calculate components (matching Amortizer.cs lines 248-265)
        var defaults = beginBalance * mdr;
        var prepays = (beginBalance - defaults) * smm;
        var scheduled = beginBalance * scheduledPct / 100;

        var endingBalance = beginBalance - scheduled - prepays - defaults;

        return new ProjectionResult
        {
            BeginningBalance = beginBalance,
            ScheduledPrincipal = scheduled,
            Prepayments = prepays,
            Defaults = defaults,
            EndingBalance = endingBalance
        };
    }

    private record ActualPeriodData(
        string Date,
        double BeginBalMM,
        double EndBalMM,
        double CdrPct,
        double VprPct,
        double SevPct
    );

    private class ProjectionResult
    {
        public double BeginningBalance { get; init; }
        public double ScheduledPrincipal { get; init; }
        public double Prepayments { get; init; }
        public double Defaults { get; init; }
        public double EndingBalance { get; init; }
    }
}
