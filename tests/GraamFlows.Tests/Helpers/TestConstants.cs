namespace GraamFlows.Tests.Helpers;

/// <summary>
/// Common test constants and values used across test classes.
/// </summary>
public static class TestConstants
{
    // Test dates
    public static readonly DateTime DefaultProjectionDate = new(2024, 1, 25);
    public static readonly DateTime DefaultFirstPayDate = new(2024, 2, 25);
    public static readonly DateTime DefaultMaturityDate = new(2030, 12, 25);

    // Default deal values
    public const string DefaultDealName = "TEST-DEAL";
    public const double DefaultTrancheBalance = 100_000_000.0;
    public const double DefaultCoupon = 5.0; // 5%
    public const double DefaultFloaterSpread = 1.5; // 150 bps
    public const double DefaultRate = 5.0; // 5% base rate

    // Collateral assumptions
    public const double DefaultWac = 8.0; // 8%
    public const double DefaultCpr = 6.0; // 6% CPR
    public const double DefaultCdr = 0.5; // 0.5% CDR
    public const double DefaultSeverity = 40.0; // 40%

    // Tolerances
    public const double BalanceTolerance = 0.01;
    public const double InterestTolerance = 0.01;
    public const double PercentTolerance = 0.0001;
}
