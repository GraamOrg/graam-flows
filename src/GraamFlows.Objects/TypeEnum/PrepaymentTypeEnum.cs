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
    MDR
}

public enum DelinqRateTypeEnum
{
    PctCurrBal,
    PctOrigBal
}