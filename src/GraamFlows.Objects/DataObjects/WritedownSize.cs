namespace GraamFlows.Objects.DataObjects;

/// <summary>
/// How the WRITEDOWN step sizes the amount it writes the notes down by.
/// </summary>
public enum WritedownSize
{
    /// <summary>The period's collateral loss: defaulted principal less recoveries. The default.</summary>
    PeriodLoss,

    /// <summary>
    /// What the funded classes still stand above the pool by after every step ahead of the
    /// write-down: an indenture's Applied Realized Loss Amount. It replaces the period-loss
    /// amount, so principal a step ahead of it paid out of excess is not written down as well.
    /// </summary>
    OverPool
}
