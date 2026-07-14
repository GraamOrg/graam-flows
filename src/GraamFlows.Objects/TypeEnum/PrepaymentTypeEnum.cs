namespace GraamFlows.Objects.TypeEnum;

public enum PrepaymentTypeEnum
{
    CPR,
    PercentCPR,
    SMM,
    PSA,
    ABS  // Auto ABS - annual prepay rate as percentage of original balance
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