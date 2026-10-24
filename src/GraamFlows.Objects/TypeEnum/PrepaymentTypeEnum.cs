namespace GraamFlows.Objects.TypeEnum;

public enum PrepaymentTypeEnum
{
    CPR,
    PercentCPR,
    SMM,
    PSA,
    ABS, // Auto ABS - annual prepay rate as percentage of original balance

    // CMBS: an annual CPR that each asset pays only from the month its call protection ends - the
    // latest of its lockout, defeasance, yield-maintenance and prepayment-premium end dates
    // (IAsset). An asset that states none of them prepays from the first period.
    CPY
}

public enum DefaultTypeEnum
{
    CDR,
    MDR,

    // Monthly default as a percentage of the ORIGINAL balance. Unlike MDR
    // (a hazard on the current performing balance), each period's default
    // dollars are rate * originalBalance, capped at the remaining performing
    // balance. Standard consumer-ABS loss convention.
    ORIGMDR
}

public enum DelinqRateTypeEnum
{
    PctCurrBal,
    PctOrigBal
}