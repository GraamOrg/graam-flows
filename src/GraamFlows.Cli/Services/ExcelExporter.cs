using ClosedXML.Excel;
using GraamFlows.Api.Models;

namespace GraamFlows.Cli.Services;

public class ExcelExporter
{
    public void Export(
        WaterfallResult result,
        DealModelFile dealModel,
        string outputPath,
        double cpr,
        double cdr,
        double sev,
        double dq)
    {
        using var workbook = new XLWorkbook();

        // Sheet 1: Combined Cashflow (most useful view)
        CreateCashflowSheet(workbook, dealModel, result);

        // Sheet 2: Summary
        CreateSummarySheet(workbook, dealModel, result, cpr, cdr, sev, dq);

        // Sheet 3: Collateral
        CreateCollateralSheet(workbook, result);

        // Sheets 4-N: Tranche Cashflows
        foreach (var tranche in result.TrancheCashflows.OrderBy(t => GetTrancheOrder(t.Key, dealModel)))
        {
            CreateTrancheSheet(workbook, tranche.Key, tranche.Value);
        }

        // Sheet N+1: WAL Summary
        CreateWalSummarySheet(workbook, dealModel, result);

        workbook.SaveAs(outputPath);
    }

    public void ExportWalReport(
        List<WalValidationResult> results,
        string dealName,
        string outputPath,
        double threshold)
    {
        using var workbook = new XLWorkbook();

        // Summary sheet
        var summarySheet = workbook.Worksheets.Add("Summary");
        var row = 1;

        summarySheet.Cell(row, 1).Value = "WAL Validation Report";
        summarySheet.Cell(row, 1).Style.Font.Bold = true;
        summarySheet.Cell(row, 1).Style.Font.FontSize = 14;
        row += 2;

        summarySheet.Cell(row, 1).Value = "Deal Name:";
        summarySheet.Cell(row, 2).Value = dealName;
        row++;

        summarySheet.Cell(row, 1).Value = "Threshold:";
        summarySheet.Cell(row, 2).Value = $"{threshold:F2} years";
        row++;

        var passCount = results.Count(r => r.Passed);
        var failCount = results.Count(r => !r.Passed);
        var rmse = Math.Sqrt(results.Sum(r => r.Error * r.Error) / results.Count);

        summarySheet.Cell(row, 1).Value = "Passed:";
        summarySheet.Cell(row, 2).Value = passCount;
        row++;

        summarySheet.Cell(row, 1).Value = "Failed:";
        summarySheet.Cell(row, 2).Value = failCount;
        row++;

        summarySheet.Cell(row, 1).Value = "RMSE:";
        summarySheet.Cell(row, 2).Value = rmse;
        summarySheet.Cell(row, 2).Style.NumberFormat.Format = "0.0000";
        row += 2;

        // Results table
        summarySheet.Cell(row, 1).Value = "Tranche";
        summarySheet.Cell(row, 2).Value = "ABS %";
        summarySheet.Cell(row, 3).Value = "CPR";
        summarySheet.Cell(row, 4).Value = "Expected WAL";
        summarySheet.Cell(row, 5).Value = "Computed WAL";
        summarySheet.Cell(row, 6).Value = "Error";
        summarySheet.Cell(row, 7).Value = "Status";

        var headerRange = summarySheet.Range(row, 1, row, 7);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
        row++;

        foreach (var r in results.OrderBy(r => r.TrancheName).ThenBy(r => r.AbsPct))
        {
            summarySheet.Cell(row, 1).Value = r.TrancheName;
            summarySheet.Cell(row, 2).Value = r.AbsPct;
            summarySheet.Cell(row, 2).Style.NumberFormat.Format = "0.0%";
            summarySheet.Cell(row, 3).Value = r.Cpr;
            summarySheet.Cell(row, 3).Style.NumberFormat.Format = "0.00%";
            summarySheet.Cell(row, 4).Value = r.ExpectedWal;
            summarySheet.Cell(row, 4).Style.NumberFormat.Format = "0.00";
            summarySheet.Cell(row, 5).Value = r.ComputedWal;
            summarySheet.Cell(row, 5).Style.NumberFormat.Format = "0.00";
            summarySheet.Cell(row, 6).Value = r.Error;
            summarySheet.Cell(row, 6).Style.NumberFormat.Format = "0.0000";
            summarySheet.Cell(row, 7).Value = r.Passed ? "PASS" : "FAIL";

            if (!r.Passed)
                summarySheet.Cell(row, 7).Style.Font.FontColor = XLColor.Red;
            else
                summarySheet.Cell(row, 7).Style.Font.FontColor = XLColor.Green;

            row++;
        }

        summarySheet.Columns().AdjustToContents();

        workbook.SaveAs(outputPath);
    }

    private void CreateSummarySheet(XLWorkbook workbook, DealModelFile dealModel, WaterfallResult result, double cpr, double cdr, double sev, double dq)
    {
        var sheet = workbook.Worksheets.Add("Summary");
        var row = 1;

        // Title
        sheet.Cell(row, 1).Value = "Waterfall Execution Summary";
        sheet.Cell(row, 1).Style.Font.Bold = true;
        sheet.Cell(row, 1).Style.Font.FontSize = 14;
        row += 2;

        // Deal Info
        sheet.Cell(row, 1).Value = "Deal Name:";
        sheet.Cell(row, 2).Value = dealModel.Deal.DealName;
        row++;

        sheet.Cell(row, 1).Value = "Waterfall Type:";
        sheet.Cell(row, 2).Value = dealModel.Deal.WaterfallType;
        row++;

        sheet.Cell(row, 1).Value = "Total Periods:";
        sheet.Cell(row, 2).Value = result.Summary.TotalPeriods;
        row += 2;

        // Assumptions
        sheet.Cell(row, 1).Value = "Assumptions";
        sheet.Cell(row, 1).Style.Font.Bold = true;
        row++;

        sheet.Cell(row, 1).Value = "CPR:";
        sheet.Cell(row, 2).Value = $"{cpr:F2}%";
        row++;

        sheet.Cell(row, 1).Value = "CDR:";
        sheet.Cell(row, 2).Value = $"{cdr:F2}%";
        row++;

        sheet.Cell(row, 1).Value = "Severity:";
        sheet.Cell(row, 2).Value = $"{sev:F2}%";
        row++;

        sheet.Cell(row, 1).Value = "Delinquency:";
        sheet.Cell(row, 2).Value = $"{dq:F2}%";
        row += 2;

        // Tranche Summary
        sheet.Cell(row, 1).Value = "Tranche Summary";
        sheet.Cell(row, 1).Style.Font.Bold = true;
        row++;

        sheet.Cell(row, 1).Value = "Tranche";
        sheet.Cell(row, 2).Value = "Original Balance";
        sheet.Cell(row, 3).Value = "Total Principal";
        sheet.Cell(row, 4).Value = "Total Interest";
        sheet.Cell(row, 5).Value = "Total Writedown";
        sheet.Cell(row, 6).Value = "Final Balance";
        sheet.Cell(row, 7).Value = "Final Factor";

        var headerRange = sheet.Range(row, 1, row, 7);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
        row++;

        foreach (var tranche in dealModel.Deal.Tranches.OrderBy(t => t.SubordinationOrder))
        {
            if (!result.Summary.TranchesSummary.TryGetValue(tranche.TrancheName, out var summary))
                continue;

            sheet.Cell(row, 1).Value = tranche.TrancheName;
            sheet.Cell(row, 2).Value = tranche.OriginalBalance;
            sheet.Cell(row, 2).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, 3).Value = summary.TotalPrincipal;
            sheet.Cell(row, 3).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, 4).Value = summary.TotalInterest;
            sheet.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, 5).Value = summary.TotalWritedown;
            sheet.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, 6).Value = summary.FinalBalance;
            sheet.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
            // Handle NaN/Infinity for zero-balance tranches
            var factor = double.IsNaN(summary.FinalFactor) || double.IsInfinity(summary.FinalFactor) ? 0 : summary.FinalFactor;
            sheet.Cell(row, 7).Value = factor;
            sheet.Cell(row, 7).Style.NumberFormat.Format = "0.000000";
            row++;
        }

        sheet.Columns().AdjustToContents();
    }

    private void CreateCashflowSheet(XLWorkbook workbook, DealModelFile dealModel, WaterfallResult result)
    {
        var sheet = workbook.Worksheets.Add("Cashflow");

        if (result.CollateralCashflows == null)
            return;

        var collateralCfs = result.CollateralCashflows.PeriodCashflows.OrderBy(c => c.CashflowDate).ToList();
        if (collateralCfs.Count == 0)
            return;

        // Define tranche order (bond tranches, then reserve, then certificate)
        var trancheOrder = new[] { "A-1", "A-2", "A-3", "A1", "A2", "A3", "B", "C", "D", "E", "RESERVE", "R", "CERTIFICATE" };

        // Find which tranches exist (excluding expenses)
        var expenseNames = result.TrancheCashflows
            .Where(t => t.Value.Any() && t.Value[0].Expense > 0 && t.Value[0].Interest == 0)
            .Select(t => t.Key)
            .ToHashSet();

        var existingTranches = trancheOrder
            .Where(t => result.TrancheCashflows.ContainsKey(t) && !expenseNames.Contains(t))
            .ToList();

        // Add any remaining tranches not in order
        foreach (var t in result.TrancheCashflows.Keys.OrderBy(k => k))
        {
            if (!existingTranches.Contains(t) && !expenseNames.Contains(t))
                existingTranches.Add(t);
        }

        var row = 1;
        var col = 1;

        // Headers
        sheet.Cell(row, col++).Value = "Period";
        sheet.Cell(row, col++).Value = "Date";
        sheet.Cell(row, col++).Value = "Collat Bal";
        sheet.Cell(row, col++).Value = "Debt Bal";
        sheet.Cell(row, col++).Value = "Collat Sched";
        sheet.Cell(row, col++).Value = "Collat Prepay";
        sheet.Cell(row, col++).Value = "Collat Recov";
        sheet.Cell(row, col++).Value = "Collat Prin";
        sheet.Cell(row, col++).Value = "Collat Net Int";
        sheet.Cell(row, col++).Value = "Service Fee";
        sheet.Cell(row, col++).Value = "Expenses";

        // Add columns for each tranche (Prin and Int)
        foreach (var trancheName in existingTranches)
        {
            sheet.Cell(row, col++).Value = $"{trancheName} Prin";
            sheet.Cell(row, col++).Value = $"{trancheName} Int";
        }

        sheet.Cell(row, col++).Value = "Collat Avail";
        sheet.Cell(row, col++).Value = "Debt Total";
        sheet.Cell(row, col++).Value = "Diff";

        var headerRange = sheet.Range(row, 1, row, col - 1);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
        row++;

        // Data rows
        for (var i = 0; i < collateralCfs.Count; i++)
        {
            var cf = collateralCfs[i];
            col = 1;

            // Period and Date
            sheet.Cell(row, col++).Value = i + 1;
            sheet.Cell(row, col).Value = cf.CashflowDate;
            sheet.Cell(row, col++).Style.NumberFormat.Format = "yyyy-mm-dd";

            // Collateral balance
            sheet.Cell(row, col).Value = Sanitize(cf.Balance);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";

            // Debt balance (sum of tranche balances excluding CERTIFICATE, R, RESERVE)
            var debtBal = 0.0;
            foreach (var trancheName in existingTranches)
            {
                if (trancheName is "CERTIFICATE" or "R" or "RESERVE")
                    continue;
                if (result.TrancheCashflows.TryGetValue(trancheName, out var tcfs) && i < tcfs.Count)
                    debtBal += tcfs[i].Balance;
            }
            sheet.Cell(row, col).Value = Sanitize(debtBal);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";

            // Collateral principal components
            var collatSched = cf.ScheduledPrincipal;
            var collatPrepay = cf.UnscheduledPrincipal;
            var collatRecov = cf.RecoveryPrincipal;
            var collatPrin = collatSched + collatPrepay + collatRecov;
            var collatNetInt = cf.NetInterest > 0 ? cf.NetInterest : cf.Interest;
            var collatServiceFee = cf.ServiceFee;

            sheet.Cell(row, col).Value = Sanitize(collatSched);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(collatPrepay);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(collatRecov);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(collatPrin);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(collatNetInt);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(collatServiceFee);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";

            // Expenses (sum from expense tranches)
            var totalExpense = 0.0;
            foreach (var expName in expenseNames)
            {
                if (result.TrancheCashflows.TryGetValue(expName, out var ecfs) && i < ecfs.Count)
                    totalExpense += ecfs[i].Expense;
            }
            sheet.Cell(row, col).Value = Sanitize(totalExpense);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";

            // Tranche cashflows
            var debtTotal = 0.0;
            foreach (var trancheName in existingTranches)
            {
                var prin = 0.0;
                var intr = 0.0;
                if (result.TrancheCashflows.TryGetValue(trancheName, out var tcfs) && i < tcfs.Count)
                {
                    prin = tcfs[i].ScheduledPrincipal + tcfs[i].UnscheduledPrincipal;
                    intr = tcfs[i].Interest;
                }
                sheet.Cell(row, col).Value = Sanitize(prin);
                sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
                sheet.Cell(row, col).Value = Sanitize(intr);
                sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
                debtTotal += prin + intr;
            }

            // Totals and reconciliation
            var collatAvail = collatPrin + collatNetInt;
            sheet.Cell(row, col).Value = Sanitize(collatAvail);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(debtTotal + totalExpense);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize((debtTotal + totalExpense) - collatAvail);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";

            row++;
        }

        sheet.Columns().AdjustToContents();
    }

    private void CreateCollateralSheet(XLWorkbook workbook, WaterfallResult result)
    {
        var sheet = workbook.Worksheets.Add("Collateral");

        if (result.CollateralCashflows == null)
            return;

        var row = 1;
        var col = 1;

        // Headers - all available fields from PeriodCashflows
        sheet.Cell(row, col++).Value = "Period";
        sheet.Cell(row, col++).Value = "Date";
        sheet.Cell(row, col++).Value = "Group";
        sheet.Cell(row, col++).Value = "Begin Balance";
        sheet.Cell(row, col++).Value = "End Balance";
        sheet.Cell(row, col++).Value = "Sched Principal";
        sheet.Cell(row, col++).Value = "Unsched Principal";
        sheet.Cell(row, col++).Value = "Default";
        sheet.Cell(row, col++).Value = "Recovery";
        sheet.Cell(row, col++).Value = "Loss";
        sheet.Cell(row, col++).Value = "Cum Default";
        sheet.Cell(row, col++).Value = "Cum Default %";
        sheet.Cell(row, col++).Value = "Cum Loss";
        sheet.Cell(row, col++).Value = "Cum Loss %";
        sheet.Cell(row, col++).Value = "Interest";
        sheet.Cell(row, col++).Value = "Net Interest";
        sheet.Cell(row, col++).Value = "Service Fee";
        sheet.Cell(row, col++).Value = "Expenses";
        sheet.Cell(row, col++).Value = "WAC";
        sheet.Cell(row, col++).Value = "Net WAC";
        sheet.Cell(row, col++).Value = "Effective WAC";
        sheet.Cell(row, col++).Value = "WAM";
        sheet.Cell(row, col++).Value = "WALA";
        sheet.Cell(row, col++).Value = "VPR";
        sheet.Cell(row, col++).Value = "CDR";
        sheet.Cell(row, col++).Value = "SEV";
        sheet.Cell(row, col++).Value = "DQ";
        sheet.Cell(row, col++).Value = "Delinq Balance";
        sheet.Cell(row, col++).Value = "Unadvanced Prin";
        sheet.Cell(row, col++).Value = "Unadvanced Int";
        sheet.Cell(row, col++).Value = "Advanced Prin";
        sheet.Cell(row, col++).Value = "Advanced Int";
        sheet.Cell(row, col++).Value = "Accum Forbearance";
        sheet.Cell(row, col++).Value = "Forbear Recovery";
        sheet.Cell(row, col++).Value = "Forbear Liquidated";
        sheet.Cell(row, col++).Value = "Forbear Unsched";

        var headerRange = sheet.Range(row, 1, row, col - 1);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
        row++;

        var period = 0;
        foreach (var cf in result.CollateralCashflows.PeriodCashflows.OrderBy(c => c.CashflowDate))
        {
            period++;
            col = 1;

            sheet.Cell(row, col++).Value = period;
            sheet.Cell(row, col).Value = cf.CashflowDate;
            sheet.Cell(row, col++).Style.NumberFormat.Format = "yyyy-mm-dd";
            sheet.Cell(row, col++).Value = cf.GroupNum ?? "1";

            // Balances
            sheet.Cell(row, col).Value = Sanitize(cf.BeginBalance);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.Balance);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";

            // Principal flows
            sheet.Cell(row, col).Value = Sanitize(cf.ScheduledPrincipal);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.UnscheduledPrincipal);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.DefaultedPrincipal);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.RecoveryPrincipal);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.CollateralLoss);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";

            // Cumulative metrics
            sheet.Cell(row, col).Value = Sanitize(cf.CumDefaultedPrincipal);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.CumDefaultedPrincipalPct);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00%";
            sheet.Cell(row, col).Value = Sanitize(cf.CumCollateralLoss);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.CumCollateralLossPct);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00%";

            // Interest
            sheet.Cell(row, col).Value = Sanitize(cf.Interest);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.NetInterest);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.ServiceFee);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.Expenses);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";

            // Weighted averages (already in percentage form, e.g. 5.0 = 5%)
            sheet.Cell(row, col).Value = Sanitize(cf.WAC);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.NetWac);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.EffectiveWac);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.WAM);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.WALA);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00";

            // Rates (annualized, already in percentage form e.g. 8.0 = 8%)
            sheet.Cell(row, col).Value = Sanitize(cf.VPR);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.CDR);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.SEV);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.DQ);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.DelinqBalance);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";

            // Advances
            sheet.Cell(row, col).Value = Sanitize(cf.UnAdvancedPrincipal);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.UnAdvancedInterest);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.AdvancedPrincipal);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.AdvancedInterest);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";

            // Forbearance
            sheet.Cell(row, col).Value = Sanitize(cf.AccumForbearance);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.ForbearanceRecovery);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.ForbearanceLiquidated);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.ForbearanceUnscheduled);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";

            row++;
        }

        sheet.Columns().AdjustToContents();
    }

    private void CreateTrancheSheet(XLWorkbook workbook, string trancheName, List<TrancheCashflowDto> cashflows)
    {
        // Sanitize sheet name (Excel has restrictions)
        var sheetName = trancheName.Length > 31 ? trancheName[..31] : trancheName;
        sheetName = string.Join("", sheetName.Split(Path.GetInvalidFileNameChars()));
        sheetName = sheetName.Replace("[", "").Replace("]", "").Replace(":", "").Replace("*", "").Replace("?", "").Replace("/", "").Replace("\\", "");

        var sheet = workbook.Worksheets.Add(sheetName);
        var row = 1;
        var col = 1;

        // Headers - all available columns from TrancheCashflowDto
        sheet.Cell(row, col++).Value = "Period";
        sheet.Cell(row, col++).Value = "Date";
        sheet.Cell(row, col++).Value = "Begin Balance";
        sheet.Cell(row, col++).Value = "End Balance";
        sheet.Cell(row, col++).Value = "Factor";
        sheet.Cell(row, col++).Value = "Sched Principal";
        sheet.Cell(row, col++).Value = "Unsched Principal";
        sheet.Cell(row, col++).Value = "Interest";
        sheet.Cell(row, col++).Value = "Coupon";
        sheet.Cell(row, col++).Value = "Effective Coupon";
        sheet.Cell(row, col++).Value = "Expense";
        sheet.Cell(row, col++).Value = "Expense Shortfall";
        sheet.Cell(row, col++).Value = "Writedown";
        sheet.Cell(row, col++).Value = "Cum Writedown";
        sheet.Cell(row, col++).Value = "Begin Credit Support";
        sheet.Cell(row, col++).Value = "Credit Support";
        sheet.Cell(row, col++).Value = "Interest Shortfall";
        sheet.Cell(row, col++).Value = "Accum Int Shortfall";
        sheet.Cell(row, col++).Value = "Int Shortfall Payback";
        sheet.Cell(row, col++).Value = "Excess Interest";
        sheet.Cell(row, col++).Value = "Index Value";
        sheet.Cell(row, col++).Value = "Floater Margin";
        sheet.Cell(row, col++).Value = "Accrual Days";
        sheet.Cell(row, col++).Value = "Is Locked Out";

        var headerRange = sheet.Range(row, 1, row, col - 1);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
        row++;

        foreach (var cf in cashflows)
        {
            col = 1;
            sheet.Cell(row, col++).Value = cf.Period;
            sheet.Cell(row, col).Value = cf.CashflowDate;
            sheet.Cell(row, col++).Style.NumberFormat.Format = "yyyy-mm-dd";
            sheet.Cell(row, col).Value = Sanitize(cf.BeginBalance);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.Balance);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.Factor);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.000000";
            sheet.Cell(row, col).Value = Sanitize(cf.ScheduledPrincipal);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.UnscheduledPrincipal);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.Interest);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.Coupon);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00%";
            sheet.Cell(row, col).Value = Sanitize(cf.EffectiveCoupon);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00%";
            sheet.Cell(row, col).Value = Sanitize(cf.Expense);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.ExpenseShortfall);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.Writedown);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.CumWritedown);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.BeginCreditSupport);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00%";
            sheet.Cell(row, col).Value = Sanitize(cf.CreditSupport);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00%";
            sheet.Cell(row, col).Value = Sanitize(cf.InterestShortfall);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.AccumInterestShortfall);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.InterestShortfallPayback);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = Sanitize(cf.ExcessInterest);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, col).Value = cf.IndexValue.HasValue ? Sanitize(cf.IndexValue.Value) : 0;
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00%";
            sheet.Cell(row, col).Value = Sanitize(cf.FloaterMargin);
            sheet.Cell(row, col++).Style.NumberFormat.Format = "0.00%";
            sheet.Cell(row, col++).Value = cf.AccrualDays;
            sheet.Cell(row, col++).Value = cf.IsLockedOut ? "Yes" : "No";
            row++;
        }

        sheet.Columns().AdjustToContents();
    }

    private void CreateWalSummarySheet(XLWorkbook workbook, DealModelFile dealModel, WaterfallResult result)
    {
        var sheet = workbook.Worksheets.Add("WAL Summary");
        var row = 1;

        // Headers
        sheet.Cell(row, 1).Value = "Tranche";
        sheet.Cell(row, 2).Value = "Original Balance";
        sheet.Cell(row, 3).Value = "Total Principal";
        sheet.Cell(row, 4).Value = "WAL (years)";

        var headerRange = sheet.Range(row, 1, row, 4);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
        row++;

        foreach (var tranche in dealModel.Deal.Tranches.OrderBy(t => t.SubordinationOrder))
        {
            if (!result.TrancheCashflows.TryGetValue(tranche.TrancheName, out var cashflows))
                continue;

            var originalBalance = tranche.OriginalBalance;
            var totalPrincipal = cashflows.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal);

            // Calculate WAL
            var firstPayDate = cashflows.FirstOrDefault()?.CashflowDate ?? DateTime.Today;
            var walNumerator = cashflows.Sum(c =>
            {
                var principal = c.ScheduledPrincipal + c.UnscheduledPrincipal;
                var yearsFromStart = (c.CashflowDate - firstPayDate).TotalDays / 365.25;
                return principal * yearsFromStart;
            });
            var wal = totalPrincipal > 0 ? walNumerator / totalPrincipal : 0;

            sheet.Cell(row, 1).Value = tranche.TrancheName;
            sheet.Cell(row, 2).Value = Sanitize(originalBalance);
            sheet.Cell(row, 2).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, 3).Value = Sanitize(totalPrincipal);
            sheet.Cell(row, 3).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(row, 4).Value = Sanitize(wal);
            sheet.Cell(row, 4).Style.NumberFormat.Format = "0.00";
            row++;
        }

        sheet.Columns().AdjustToContents();
    }

    private static int GetTrancheOrder(string trancheName, DealModelFile dealModel)
    {
        var tranche = dealModel.Deal.Tranches.FirstOrDefault(t => t.TrancheName == trancheName);
        return tranche?.SubordinationOrder ?? 999;
    }

    /// <summary>
    /// Sanitizes numeric values - replaces NaN/Infinity with 0 for Excel compatibility
    /// </summary>
    private static double Sanitize(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;
    }
}
