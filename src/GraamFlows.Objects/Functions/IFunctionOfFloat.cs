namespace GraamFlows.Assumptions;

public interface IFunctionOfFloat
{
    /**
     * minimum valid argument to the function
     * @return
     */
    double getMinArgument();

    /**
     * maximum valid argument to the function.
     * @return
     */
    double getMaxArgument();

    /**
     * test if i is a valid argument to the function.
     * @param x
     * @return true if function is defined at x
     */
    bool isValidArgument(double x);

    /**
     * @param x argument to the function
     * @return the value of the function at x
     * @throws IllegalArgumentException
     */
    double valueAt(double x);

    /**
     * like valueAt, but returns a boxed float, which is set to null if the
     * function is not defined on the input
     * @param x argument to the function
     * @return boxed float, set to null if the function is not defined on
     * the input.
     */
    double? tryValueAt(double x);
}