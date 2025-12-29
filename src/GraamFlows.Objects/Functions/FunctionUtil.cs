using System.Diagnostics;

namespace GraamFlows.Objects.Functions;

public class FunctionUtil
{
    public static double getExtendedValueFromArray(int idx, double[] values,
        ExtrapolationBehavior lowerBoundBehavior, ExtrapolationBehavior upperBoundBehavior)
    {
        return getExtendedValueFromArray(idx, values, values.Length - 1,
            lowerBoundBehavior, upperBoundBehavior);
    }

    public static double getExtendedValueFromArray(int idx, double[] values, int lastValidIndex,
        ExtrapolationBehavior lowerBoundBehavior, ExtrapolationBehavior upperBoundBehavior)
    {
        if (idx < 0)
            switch (lowerBoundBehavior)
            {
                case ExtrapolationBehavior.EXTRAPOLATE:
                    Debug.Assert(lastValidIndex > 0);
                    return values[0] + idx * (values[1] - values[0]);

                case ExtrapolationBehavior.CONSTANT:
                    return values[0];

                case ExtrapolationBehavior.NONE:
                default:
                    throw new Exception("invalid input integer (below minimum): " + idx);
            }

        if (idx > lastValidIndex)
            switch (upperBoundBehavior)
            {
                case ExtrapolationBehavior.EXTRAPOLATE:
                    Debug.Assert(lastValidIndex > 0);
                    return values[lastValidIndex] + (idx - lastValidIndex) *
                        (values[lastValidIndex] - values[lastValidIndex - 1]);

                case ExtrapolationBehavior.CONSTANT:
                    return values[lastValidIndex];

                case ExtrapolationBehavior.NONE:
                default:
                    throw new Exception(
                        "invalid input integer (beyond last valid index): " + idx + ">" + lastValidIndex);
            }

        return values[idx];
    }
}