using System.Xml.Linq;

namespace GraamFlows.Waterfall;

public enum PrincipalTypeEnum
{
    Sched,
    Ppay,
    Recov
}

public enum ResidualHandlingEnum
{
    Sequential,
    Prorata
}

public interface IPayable
{
    bool IsLeaf { get; }
    void PaySp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec);
    void PayUsp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec);
    void PayRp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec);
    void PayWritedown(IPayable caller, DateTime cfDate, double amount, Action payRuleExec);
    double BeginBalance(DateTime cfDate);
    double CurrentBalance(DateTime cfDate);
    bool IsLockedOut(DateTime cfDate);
    double LockedOutBalance(DateTime cfDate);
    string Describe(int level);
    XElement DescribeXml();
    HashSet<IPayable> Leafs();
    List<IPayable> GetChildren();
}

public interface IPayablesHost
{
    IPayable ScheduledPayable { get; set; }
    IPayable PrepayPayable { get; set; }
    IPayable RecoveryPayable { get; set; }
    IPayable ReservePayable { get; set; }

    // Unified waterfall properties
    IPayable InterestPayable { get; set; }
    IPayable WritedownPayable { get; set; }
    IPayable ExcessPayable { get; set; }
}