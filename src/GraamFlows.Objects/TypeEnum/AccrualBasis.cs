namespace GraamFlows.Objects.TypeEnum;

/// <summary>
///     How an asset's coupon accrues over a projection period.
/// </summary>
public enum AccrualBasis
{
    /// <summary>
    ///     Every month is 1/12 of a year (annual rate / 12 per monthly period). The default, and
    ///     the only basis the engine modelled before <see cref="Actual360" /> was added.
    /// </summary>
    Thirty360,

    /// <summary>
    ///     A period accrues its actual calendar days over 360 — the convention of SOFR-based
    ///     floating-rate loans. Over a year this earns 365/360 of the 30/360 coupon (about 1.4%
    ///     more interest), and a 31-day month earns more than a 28-day one.
    /// </summary>
    Actual360
}
