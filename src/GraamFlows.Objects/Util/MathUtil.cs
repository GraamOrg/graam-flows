namespace GraamFlows.Objects.Util;

public static class MathUtil
{
    public static double ConvertToCpr(double smm)
    {
        var result = 100 * (1.0 - Math.Pow(1.0 - smm, 12.0));
        return result;
    }

    /// <summary>
    ///     De-annualize an annual percentage rate (CPR/CDR — e.g. 6 for 6%) into the
    ///     monthly hazard (SMM/MDR) the amortizer consumes, as a fraction in [0, 1].
    ///
    ///     This is the canonical de-annualization for the engine's assumption arrays:
    ///     <c>CfCore.BuildAssumptionArray</c> and the reinvestment-cohort path both
    ///     call it. It exists to remove a NaN factory (graam-harmony #4476): the bare
    ///     expression evaluates <c>Math.Pow(negative, 1.0 / 12.0)</c> for any input
    ///     above 100 and yields NaN, and the amortizer's <c>Math.Clamp(smm, 0, 1)</c>
    ///     cannot clamp NaN — every comparison against NaN is false — so one
    ///     out-of-range assumption used to poison every emitted period.
    ///
    ///     NaN is deliberately NOT special-cased. The API boundary validator
    ///     (<c>GraamFlows.Api.Validation.AssumptionValidation</c>) rejects a non-finite
    ///     assumption before it can reach the engine, so a NaN arriving here is an
    ///     engine bug, not user input. Letting it propagate keeps that bug loud;
    ///     silently mapping it to 0 would turn it into a plausible-looking cashflow
    ///     that no one would ever question.
    ///
    ///     Tie-out note: the in-range expression is byte-for-byte the one the engine
    ///     has always used. Do not rewrite <c>/ 100.0</c> as <c>* .01</c> — those are
    ///     not bit-identical in IEEE-754 and this engine has WAL/price tie-out tests.
    ///     The clamps only affect inputs the old expression could not evaluate
    ///     meaningfully anyway: at exactly 0 and exactly 100 they return the same
    ///     values the expression does (<c>Pow(1, 1/12) == 1</c>, <c>Pow(0, 1/12) == 0</c>),
    ///     and a negative input used to produce a negative hazard that the amortizer
    ///     clamped to 0 regardless.
    /// </summary>
    /// <param name="annualPercent">Annual rate in percent (6 means 6%).</param>
    /// <returns>The equivalent monthly hazard as a fraction in [0, 1].</returns>
    public static double AnnualPercentToMonthlyHazard(double annualPercent)
    {
        if (annualPercent >= 100.0)
            return 1.0;
        if (annualPercent <= 0.0)
            return 0.0;
        return 1.0 - Math.Pow(1.0 - annualPercent / 100.0, 1.0 / 12.0);
    }

    /// <summary>
    ///     CPR-to-SMM conversion used by the PSA helpers below.
    ///
    ///     This is the SAME formula as
    ///     <see cref="AnnualPercentToMonthlyHazard" /> but scales with <c>cpr * .01</c>
    ///     rather than <c>cpr / 100.0</c>, and the two are NOT bit-identical: over a
    ///     15,000,001-point sweep of [0, 100] (a 10M-point grid plus 5M random draws),
    ///     603,710 values disagreed, the widest by 115,223 ulps just below 100 where
    ///     <c>1 - x/100</c> cancels hardest. They are therefore kept as separate
    ///     functions on purpose — deduping them would move tie-out numbers.
    ///
    ///     The engine's assumption path (<c>CfCore.BuildAssumptionArray</c>) uses
    ///     <see cref="AnnualPercentToMonthlyHazard" />. This one is reached only via
    ///     <see cref="ConvertPsaToSmm" />. See graam-harmony #4476.
    /// </summary>
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