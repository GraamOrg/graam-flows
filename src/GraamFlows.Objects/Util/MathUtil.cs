namespace GraamFlows.Objects.Util;

public static class MathUtil
{
    public static double ConvertToCpr(double smm)
    {
        var result = 100 * (1.0 - Math.Pow(1.0 - smm, 12.0));
        return result;
    }

    public static double ConvertToSmm(double cpr)
    {
        if (cpr > 100)
            return 1;
        var result = 1.0 - Math.Pow(1.0 - cpr * .01, 1.0 / 12.0);
        return result;
    }

    public static double AmortizingPayment(double balance, double monthlyCpn, int wam)
    {
        var expValue = Math.Pow(1 + monthlyCpn, wam);
        return expValue > 1 ? monthlyCpn * expValue / (expValue - 1) * balance : 1.0 / wam * balance;
    }

    public static double AmortizingPayment(double balance, double monthlyCpn, double wam)
    {
        var expValue = Math.Pow(1 + monthlyCpn, wam);
        return expValue > 1 ? monthlyCpn * expValue / (expValue - 1) * balance : 1.0 / wam * balance;
    }

    public static double NormalDist(double x, double mean, double stddev)
    {
        var fact = stddev * Math.Sqrt(2.0 * Math.PI);
        var expo = (x - mean) * (x - mean) / (2.0 * stddev * stddev);
        return Math.Exp(-expo) / fact;
    }

    public static double ConvertPsaToSmm(int psa, int age)
    {
        var cpr = ConvertPsaToCpr(psa, age);
        var smm = ConvertToSmm(cpr);
        return smm;
    }

    public static double ConvertPsaToCpr(int psa, int age)
    {
        if (age <= 0)
            return 0;

        double cpr;

        if (age <= 30)
            cpr = .06 * age / 30;
        else
            cpr = .06;

        cpr *= psa;
        return cpr;
    }
}