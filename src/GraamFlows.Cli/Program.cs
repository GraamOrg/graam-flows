using System.CommandLine;
using GraamFlows.Cli.Commands;

namespace GraamFlows.Cli;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("GraamFlows CLI - Run deal waterfall models directly");

        // Add the run command
        rootCommand.AddCommand(RunCommand.Create());

        // Add the wal-tests command
        rootCommand.AddCommand(WalTestsCommand.Create());

        return await rootCommand.InvokeAsync(args);
    }
}
