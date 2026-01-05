namespace GraamFlows.Api.Models;

// ============== Request Models ==============

public class PricingRequest
{
    public List<CashflowEntryDto> Cashflows { get; set; } = new();
    public PricingParamsDto Params { get; set; } = new();
    public List<double[]>? Rates { get; set; } // [[term, rate], ...]
}

public class CashflowEntryDto
{
    public DateTime Date { get; set; }
    public double Interest { get; set; }
    public double Principal { get; set; }
    public double Balance { get; set; }
    public double? IndexValue { get; set; } // for DM calc
}

public class PricingParamsDto
{
    public string InputType { get; set; } = "price"; // "price" | "yield" | "spread"
    public double InputValue { get; set; }
    public DateTime SettleDate { get; set; }
    public double Balance { get; set; }
    public string DayCount { get; set; } = "Actual360";
    public string Compounding { get; set; } = "SemiAnnual";
    public DateTime? StartAccrualPeriod { get; set; } // for accrued interest calc
    public int PayDelay { get; set; } = 0;
}

// ============== Response Models ==============

public class PricingResponse
{
    public double? Price { get; set; }
    public double? Yield { get; set; }
    public double? Spread { get; set; }
    public double? Dm { get; set; }
    public double? ModifiedDuration { get; set; }
    public double? Wal { get; set; }
    public double? AccruedInterest { get; set; }
    public double? DirtyPrice { get; set; }
}
