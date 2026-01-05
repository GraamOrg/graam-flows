namespace GraamFlows.Waterfall;

public class CashflowAllocs
{
    private static readonly CashflowAllocs _empty = new(0, 0, 0, 0, 0);

    public CashflowAllocs(double schedPrin, double prepayPrin, double recovPrin, double writedown, double interest)
    {
        SchedPrin = schedPrin;
        PrepayPrin = prepayPrin;
        RecovPrin = recovPrin;
        Writedown = writedown;
        Interest = interest;
    }

    public double SchedPrin { get; }
    public double PrepayPrin { get; }
    public double RecovPrin { get; }
    public double Writedown { get; }
    public double Interest { get; }

    public static CashflowAllocs Empty()
    {
        return _empty;
    }
}