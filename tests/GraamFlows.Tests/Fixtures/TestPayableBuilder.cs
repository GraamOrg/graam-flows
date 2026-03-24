using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall;
using GraamFlows.Waterfall.MarketTranche;
using GraamFlows.Waterfall.Structures.PayableStructures;

namespace GraamFlows.Tests.Fixtures;

/// <summary>
/// Helper class for building payable structures for testing.
/// </summary>
public static class TestPayableBuilder
{
    /// <summary>
    /// Create a sequential structure from a list of payables.
    /// </summary>
    public static SequentialStructure Sequential(params IPayable[] payables)
        => new(payables.ToList());

    /// <summary>
    /// Create a pro rata structure from a list of payables.
    /// </summary>
    public static ProrataStructure Prorata(params IPayable[] payables)
        => new(payables.ToList());

    /// <summary>
    /// Create a mock payable that tracks payments and returns controlled values.
    /// </summary>
    public static MockPayable Mock(
        double balance = TestConstants.DefaultTrancheBalance,
        double interestDue = 0,
        bool isLockedOut = false,
        string name = "MOCK")
    {
        return new MockPayable(name, balance, interestDue, isLockedOut);
    }
}

/// <summary>
/// A mock IPayable implementation for unit testing payable structures.
/// Tracks all payments and returns controlled values.
/// </summary>
public class MockPayable : IPayable
{
    public string Name { get; }
    public double Balance { get; private set; }
    public double InterestDueAmount { get; set; }
    public bool LockedOut { get; set; }

    // Tracking
    public double TotalSpPaid { get; private set; }
    public double TotalUspPaid { get; private set; }
    public double TotalRpPaid { get; private set; }
    public double TotalInterestPaid { get; private set; }
    public double TotalWritedownPaid { get; private set; }
    public List<(DateTime Date, double Amount, string Type)> PaymentHistory { get; } = new();

    public MockPayable(string name, double balance, double interestDue, bool isLockedOut)
    {
        Name = name;
        Balance = balance;
        InterestDueAmount = interestDue;
        LockedOut = isLockedOut;
    }

    public bool IsLeaf => true;

    public void PaySp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec)
    {
        var toPay = Math.Min(prin, Balance);
        Balance -= toPay;
        TotalSpPaid += toPay;
        PaymentHistory.Add((cfDate, toPay, "SP"));
    }

    public void PayUsp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec)
    {
        var toPay = Math.Min(prin, Balance);
        Balance -= toPay;
        TotalUspPaid += toPay;
        PaymentHistory.Add((cfDate, toPay, "USP"));
    }

    public void PayRp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec)
    {
        var toPay = Math.Min(prin, Balance);
        Balance -= toPay;
        TotalRpPaid += toPay;
        PaymentHistory.Add((cfDate, toPay, "RP"));
    }

    public void PayWritedown(IPayable caller, DateTime cfDate, double amount, Action payRuleExec)
    {
        var toPay = Math.Min(amount, Balance);
        Balance -= toPay;
        TotalWritedownPaid += toPay;
        PaymentHistory.Add((cfDate, toPay, "WD"));
    }

    public double PayInterest(IPayable caller, DateTime cfDate, double availableFunds,
        IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches)
    {
        var toPay = Math.Min(availableFunds, InterestDueAmount);
        TotalInterestPaid += toPay;
        InterestDueAmount -= toPay;
        PaymentHistory.Add((cfDate, toPay, "INT"));
        return toPay;
    }

    public double InterestDue(DateTime cfDate, IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches)
    {
        return InterestDueAmount;
    }

    public double BeginBalance(DateTime cfDate) => Balance;
    public double CurrentBalance(DateTime cfDate) => Balance;
    public bool IsLockedOut(DateTime cfDate) => LockedOut;
    public double LockedOutBalance(DateTime cfDate) => LockedOut ? Balance : 0;

    public string Describe(int level) => $"MOCK('{Name}')";

    public System.Xml.Linq.XElement DescribeXml()
    {
        var element = new System.Xml.Linq.XElement("Mock");
        element.Add(new System.Xml.Linq.XAttribute("Name", Name));
        return element;
    }

    public double PayInterestShortfall(DateTime cfDate, double availableFunds) => 0;

    public HashSet<IPayable> Leafs() => new() { this };
    public List<IPayable> GetChildren() => new();
}

/// <summary>
/// A simple constant rate provider for testing.
/// </summary>
public class ConstantTestRateProvider : IRateProvider
{
    private readonly double _rate;

    public ConstantTestRateProvider(double rate = TestConstants.DefaultRate)
    {
        _rate = rate;
    }

    public double GetRate(MarketDataInstEnum inst, DateTime value) => _rate;
    public double GetRate(MarketDataInstEnum inst, int absT) => _rate;
    public double[] GetRates(MarketDataInstEnum inst, int startAbsT, int length)
        => Enumerable.Repeat(_rate, length).ToArray();
}
