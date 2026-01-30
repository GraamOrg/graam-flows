namespace GraamFlows.Cli.Models;

public class RunOptions
{
    public required FileInfo DealModelFile { get; set; }
    public FileInfo? OutputFile { get; set; }
    public double Cpr { get; set; }
    public double Cdr { get; set; }
    public double Sev { get; set; }
    public double Dq { get; set; }
    public DateTime? ProjectionDate { get; set; }
    public FileInfo? FactorsFile { get; set; }
    public bool Verbose { get; set; }

    // Collateral overrides
    public double? CollatBal { get; set; }
    public double? Wac { get; set; }
    public int? Term { get; set; }

    public string GetOutputPath(string dealName)
    {
        if (OutputFile != null)
        {
            var path = OutputFile.FullName;
            // Ensure .xlsx extension
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                path += ".xlsx";
            return path;
        }

        var sanitizedName = string.Join("_", dealName.Split(Path.GetInvalidFileNameChars()));
        return $"{sanitizedName}_results.xlsx";
    }
}

public class WalTestsOptions
{
    public required FileInfo DealModelFile { get; set; }
    public FileInfo? OutputFile { get; set; }
    public double Threshold { get; set; } = 0.10;
    public double? AbsPct { get; set; }  // Single ABS% to run (null = run all)
    public bool Verbose { get; set; }

    public string GetOutputPath(string dealName)
    {
        if (OutputFile != null)
        {
            var path = OutputFile.FullName;
            // Ensure .xlsx extension
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                path += ".xlsx";
            return path;
        }

        var sanitizedName = string.Join("_", dealName.Split(Path.GetInvalidFileNameChars()));

        // If running single scenario, use different naming
        if (AbsPct.HasValue)
            return $"{sanitizedName}_abs{AbsPct.Value:F1}.xlsx";

        return $"{sanitizedName}_wal_report.xlsx";
    }
}
