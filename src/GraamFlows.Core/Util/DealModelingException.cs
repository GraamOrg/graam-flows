using GraamFlows.Waterfall;

namespace GraamFlows.Util;

public class DealModelingException : Exception
{
    public DealModelingException(string dealName, string error) : base($"Deal {dealName} has modeling error - {error}")
    {
    }
}

public class PrincipalDistributionException : Exception
{
    public PrincipalDistributionException(IPayable payable, DateTime payDate, string error) : base(
        $"Error distributing principal on {payDate:MM/dd/yyyy}\n{error}\n{payable.Describe(0)}")
    {
    }
}

public class CollateralAndTrancheBalanceMistmatchException : Exception
{
    public CollateralAndTrancheBalanceMistmatchException(string dealName, double collatBal, double trancheBal,
        IList<DynamicClass> dealClasses) :
        base(
            $"Deal {dealName} has mismatch tranches vs. collateral. Collat {collatBal:#,###.##}, tranche {trancheBal:#,###.##}. Deal classes are {string.Join(",", dealClasses.Select(d => d.Tranche.TrancheName))}")
    {
    }
}

public class PaydownException : Exception
{
    public PaydownException(string dealName, string groupNum, DateTime cfDate, double collatBal, double trancheBal,
        double beginDiff) :
        base(
            $"Deal {dealName} Group {groupNum} collateral and tranches are not paying down at the same rate. Collat {collatBal:#,###.##}, tranche {trancheBal:#,###.##}. Proj date {cfDate:yyyy-MM-dd}. Difference is {collatBal - trancheBal:#,###}. Starting difference is {beginDiff:#,###.##}")
    {
    }
}

public class InterestNotProperlyDistributedException : Exception
{
    public InterestNotProperlyDistributedException(DateTime cfDate, double interestPaid, double interestAvailable) :
        base(
            $"Interest not properly distributed. Interest available {interestAvailable:#,###} but paid {interestPaid:#,###} for date {cfDate}. Difference is {interestAvailable - interestPaid:#,###}")
    {
    }
}

public static class Exceptions
{
    public static void DealModelingException(string dealName, string error)
    {
        throw new DealModelingException(dealName, error);
    }

    public static void PrincipalDistributionException(IPayable payable, DateTime payDate, string error)
    {
        throw new PrincipalDistributionException(payable, payDate, error);
    }

    public static void CollateralAndTrancheBalanceMistmatchException(string dealName, double collatBal,
        double trancheBal, IList<DynamicClass> dealClasses)
    {
        throw new CollateralAndTrancheBalanceMistmatchException(dealName, collatBal, trancheBal, dealClasses);
    }

    public static void PaydownException(string dealName, string groupNum, DateTime cfDate, double collatBal,
        double trancheBal, double beginDiff)
    {
        throw new PaydownException(dealName, groupNum, cfDate, collatBal, trancheBal, beginDiff);
    }

    public static void InterestUnproperlyDistributedException(DateTime cfDate, double interestAvailable,
        double interestPaid)
    {
        throw new InterestNotProperlyDistributedException(cfDate, interestPaid, interestAvailable);
    }
}