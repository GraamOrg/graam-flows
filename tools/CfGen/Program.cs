using System.Globalization;
using System.Text;
using GraamFlows;
using GraamFlows.Assumptions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;

// ---------------------------------------------------------------------------
// Collateral-cashflow validation-set generator (graam-harmony #3449 work).
//
// Builds a 100k / 5% / 30y loan in every asset type graam-flows supports (FRM,
// ARM, STEP, plus IO and Forbearance modifiers) and runs each through the
// amortizer (CfCore.GenerateAssetCashflows) under a battery of prepay / default
// / delinquency / severity / recovery-lag assumptions — including the 100 CPR
// and 100 CDR extremes. Every period's full cashflow is written to
// ~/work/cf_validate/graam/graam_{assetType}__{scenario}.csv, plus an index.csv.
// ---------------------------------------------------------------------------

var ci = CultureInfo.InvariantCulture;

var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var outDir = Path.Combine(home, "work", "cf_validate", "graam");
Directory.CreateDirectory(outDir);

var firstProjDate = new DateTime(2025, 1, 1);
var anchor = DateUtil.CalcAbsT(firstProjDate);
var startAbsT = anchor;
IRateProvider rateProvider = new ConstantRateProvider(3.0); // index level for ARMs

const double Bal = 100_000;
const double RatePct = 5.0;
const int Term = 360;

// ---- Asset builders (100k / 5% / 30y, one per supported type) --------------

Asset BaseAsset(string id) => new()
{
    AssetName = id,
    AssetId = id,
    InterestRateType = InterestRateType.FRM,
    OriginalDate = firstProjDate.AddMonths(-1), // ~new loan, age 0 at projection
    OriginalBalance = Bal,
    CurrentBalance = Bal,
    BalanceAtIssuance = Bal,
    OriginalInterestRate = RatePct,
    CurrentInterestRate = RatePct,
    OriginalAmortizationTerm = Term,
    ServiceFee = 0.25,
    GroupNum = "1",
    IsIO = false,
};

var assetTypes = new (string Key, string Desc, Func<Asset> Build)[]
{
    ("frm", "FRM 100k 5.0% 30y",
        () => BaseAsset("FRM")),

    ("arm", "ARM 100k 5.0% start, 5y/1y, Libor1M+3.0, caps 2/10/2",
        () =>
        {
            var a = BaseAsset("ARM");
            a.InterestRateType = InterestRateType.ARM;
            a.InitialAdjustmentPeriod = 60;   // 5y initial fixed
            a.AdjustmentPeriod = 12;          // annual reset thereafter
            a.InitialRate = RatePct;
            a.IndexName = MarketDataInstEnum.Libor1M;
            a.IndexMargin = 3.0;
            a.AdjustmentCap = 2.0;
            a.LifeAdjustmentCap = 10.0;
            a.LifeAdjustmentFloor = 2.0;
            return a;
        }),

    ("step", "STEP 100k 5.0% -> 6.0% @5y -> 7.0% @10y, 30y",
        () =>
        {
            var a = BaseAsset("STEP");
            a.InterestRateType = InterestRateType.STEP;
            // Step dates are absolute-T values (DateUtil.CalcAbsT space).
            a.StepDatesList = $"{startAbsT + 60},{startAbsT + 120}";
            a.StepRatesList = "6.0,7.0";
            return a;
        }),

    ("io", "FRM-IO 100k 5.0%, 60m interest-only then amortize, 30y",
        () =>
        {
            var a = BaseAsset("IO");
            a.IsIO = true;
            a.IOTerm = 60;
            return a;
        }),

    ("forb", "FRM 100k 5.0% 30y with 10k forbearance balance",
        () =>
        {
            var a = BaseAsset("FORB");
            a.ForbearanceAmt = 10_000;
            return a;
        }),
};

// ---- Scenario matrix -------------------------------------------------------

IAnchorableVector V(double v) => new ConstVector(anchor, v);

IAssetAssumptions Assumps(Scen s) => new AssetAssumptions(
    s.PType, V(s.PVal),
    s.DType, V(s.DVal),
    V(s.Sev),
    DelinqRateTypeEnum.PctCurrBal, V(s.Dq), V(s.Adv), V(s.Adv))
{
    RecoveryLag = s.Lag,
};

var P = PrepaymentTypeEnum.CPR;
var Smm = PrepaymentTypeEnum.SMM;
var Abs = PrepaymentTypeEnum.ABS;
var Psa = PrepaymentTypeEnum.PSA;
var Cdr = DefaultTypeEnum.CDR;
var Mdr = DefaultTypeEnum.MDR;
var OrigMdr = DefaultTypeEnum.ORIGMDR;

// Defaults: sev 40, delinquency 0, advancing 100, no lag unless noted.
var scenarios = new List<Scen>
{
    // --- pure amortization / prepay sweep (no default) ---
    new("clean",            P, 0,    Cdr, 0,   0,   0, 100, 0),
    new("cpr06",            P, 6,    Cdr, 0,   0,   0, 100, 0),
    new("cpr15",            P, 15,   Cdr, 0,   0,   0, 100, 0),
    new("cpr30",            P, 30,   Cdr, 0,   0,   0, 100, 0),
    new("cpr100",           P, 100,  Cdr, 0,   0,   0, 100, 0),  // extreme: full prepay p1
    new("smm01",            Smm, 1.0, Cdr, 0,  0,   0, 100, 0),  // direct 1%/mo
    new("abs015",           Abs, 1.5, Cdr, 0,  0,   0, 100, 0),  // auto-ABS % orig
    new("psa100",           Psa, 100, Cdr, 0,  0,   0, 100, 0),  // PSA (routes via CPR path)

    // --- default sweep (CPR 0, sev 40) ---
    new("cdr01_sev40",      P, 0,    Cdr, 1,   40,  0, 100, 0),
    new("cdr05_sev40",      P, 0,    Cdr, 5,   40,  0, 100, 0),
    new("cdr100_sev40",     P, 0,    Cdr, 100, 40,  0, 100, 0), // extreme: full default p1
    new("mdr005_sev40",     P, 0,    Mdr, 0.5, 40,  0, 100, 0), // direct 0.5%/mo
    new("origmdr005_sev40", P, 0,    OrigMdr, 0.5, 40, 0, 100, 0),

    // --- severity variants (CDR 5) ---
    new("cdr05_sev0",       P, 0,    Cdr, 5,   0,   0, 100, 0), // full recovery
    new("cdr05_sev100",     P, 0,    Cdr, 5,   100, 0, 100, 0), // zero recovery

    // --- combined prepay + default ---
    new("cpr15_cdr05_sev40",  P, 15, Cdr, 5,   40,  0, 100, 0),
    new("cpr30_cdr10_sev60",  P, 30, Cdr, 10,  60,  0, 100, 0),
    new("cpr100_cdr100",      P, 100, Cdr, 100, 40, 0, 100, 0), // both extremes together

    // --- delinquency / advancing (CDR 2, sev 40) ---
    new("dq05_adv100_cdr02", P, 0,   Cdr, 2,   40,  5, 100, 0),
    new("dq05_adv000_cdr02", P, 0,   Cdr, 2,   40,  5,   0, 0),
    new("dq10_adv050_cdr02", P, 0,   Cdr, 2,   40, 10,  50, 0),

    // --- recovery lag (CDR 5, sev 40) ---
    new("cdr05_sev40_lag06",     P, 0,  Cdr, 5,  40, 0, 100, 6),
    new("cdr05_sev40_lag12",     P, 0,  Cdr, 5,  40, 0, 100, 12),
    new("cpr10_cdr03_sev50_lag06", P, 10, Cdr, 3, 50, 0, 100, 6),

    // --- edge / stress ---
    new("cdr100_sev100",   P, 0,     Cdr, 100, 100, 0, 100, 0), // instant total loss
    new("allstress",       P, 30,    Cdr, 10,  60,  8,  75, 3),
};

// ---- Run + export ----------------------------------------------------------

var index = new StringBuilder();
index.AppendLine(string.Join(",",
    "file", "asset_type", "asset_desc", "prepay_type", "prepay_val",
    "default_type", "default_val", "severity_pct", "delinq_pct", "advancing_pct",
    "recovery_lag_m", "num_periods", "wal_years", "total_scheduled", "total_unscheduled",
    "total_defaulted", "total_recovery", "total_interest", "total_net_interest",
    "total_loss", "ending_balance"));

var fileCount = 0;
foreach (var at in assetTypes)
{
    foreach (var s in scenarios)
    {
        var assumps = Assumps(s);
        var result = CfCore.GenerateAssetCashflows(
            new List<IAsset> { at.Build() },
            firstProjDate, null, _ => assumps, rateProvider);

        var periods = result.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();

        var fileName = $"graam_{at.Key}__{s.Name}.csv";
        WriteCsv(Path.Combine(outDir, fileName), periods);
        fileCount++;

        // Summary stats for the index.
        double totSched = 0, totUnsched = 0, totDef = 0, totRec = 0, totInt = 0, totNet = 0, totLoss = 0;
        double walNum = 0, walDen = 0;
        for (var i = 0; i < periods.Count; i++)
        {
            var p = periods[i];
            totSched += p.ScheduledPrincipal;
            totUnsched += p.UnscheduledPrincipal;
            totDef += p.DefaultedPrincipal;
            totRec += p.RecoveryPrincipal;
            totInt += p.Interest;
            totNet += p.NetInterest;
            totLoss += p.CollateralLoss;
            var prin = p.ScheduledPrincipal + p.UnscheduledPrincipal + p.RecoveryPrincipal;
            var years = (i + 1) / 12.0;
            walNum += years * prin;
            walDen += prin;
        }
        var wal = walDen > 0 ? walNum / walDen : 0;
        var endBal = periods.Count > 0 ? periods[^1].Balance : 0;

        index.AppendLine(string.Join(",",
            fileName, at.Key, Quote(at.Desc), s.PType, F(s.PVal),
            s.DType, F(s.DVal), F(s.Sev), F(s.Dq), F(s.Adv),
            s.Lag, periods.Count, F(wal), F(totSched), F(totUnsched),
            F(totDef), F(totRec), F(totInt), F(totNet),
            F(totLoss), F(endBal)));
    }
}

File.WriteAllText(Path.Combine(outDir, "index.csv"), index.ToString());

Console.WriteLine($"Wrote {fileCount} cashflow files + index.csv to {outDir}");
Console.WriteLine($"  {assetTypes.Length} asset types x {scenarios.Count} scenarios");

// ---- helpers ---------------------------------------------------------------

string F(double d) => d.ToString("0.######", ci);
string Quote(string s) => s.Contains(',') ? $"\"{s}\"" : s;

void WriteCsv(string path, List<PeriodCashflows> periods)
{
    var sb = new StringBuilder();
    sb.AppendLine(string.Join(",",
        "period", "date", "begin_balance", "balance", "scheduled_principal",
        "unscheduled_principal", "interest", "net_interest", "service_fee",
        "wac", "net_wac", "effective_wac", "wam", "wala",
        "defaulted_principal", "recovery_principal", "collateral_loss",
        "delinq_balance", "unadv_principal", "unadv_interest",
        "advanced_principal", "advanced_interest", "vpr", "cdr", "sev", "dq",
        "cum_defaulted", "cum_defaulted_pct", "cum_loss", "cum_loss_pct",
        "expenses", "accum_forbearance", "forbearance_recovery",
        "forbearance_liquidated", "forbearance_unscheduled"));

    var period = 0;
    foreach (var p in periods)
    {
        period++;
        sb.AppendLine(string.Join(",",
            period, p.CashflowDate.ToString("yyyy-MM-dd", ci),
            F(p.BeginBalance), F(p.Balance), F(p.ScheduledPrincipal),
            F(p.UnscheduledPrincipal), F(p.Interest), F(p.NetInterest), F(p.ServiceFee),
            F(p.WAC), F(p.NetWac), F(p.EffectiveWac), F(p.WAM), F(p.WALA),
            F(p.DefaultedPrincipal), F(p.RecoveryPrincipal), F(p.CollateralLoss),
            F(p.DelinqBalance), F(p.UnAdvancedPrincipal), F(p.UnAdvancedInterest),
            F(p.AdvancedPrincipal), F(p.AdvancedInterest), F(p.VPR), F(p.CDR), F(p.SEV), F(p.DQ),
            F(p.CumDefaultedPrincipal), F(p.CumDefaultedPrincipalPct), F(p.CumCollateralLoss),
            F(p.CumCollateralLossPct), F(p.Expenses), F(p.AccumForbearance),
            F(p.ForbearanceRecovery), F(p.ForbearanceLiquidated), F(p.ForbearanceUnscheduled)));
    }

    File.WriteAllText(path, sb.ToString());
}

record Scen(
    string Name,
    PrepaymentTypeEnum PType, double PVal,
    DefaultTypeEnum DType, double DVal,
    double Sev, double Dq, double Adv, int Lag);
