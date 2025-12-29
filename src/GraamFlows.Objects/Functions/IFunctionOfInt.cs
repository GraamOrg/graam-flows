namespace GraamFlows.Objects.Functions;

public interface IFunctionOfInt
{
    /**
     * minimum valid argument to the function
     * @return
     */
    int getMinArgument();

    /**
     * maximum valid argument to the function.
     * @return
     */
    int getMaxArgument();

    /**
     * test if i is a valid argument to the function.
     * @param i
     * @return true if function is defined at i
     */
    bool isValidArgument(int i);

    /**
     * @param i argument to the function
     * @return the value of the function at i
     * @throws IllegalArgumentException
     */
    double valueAt(int i);

    /**
     * like valueAt, but returns a boxed float, which is set to null if the
     * function is not defined on the input
     * @param i argument to the function
     * @return boxed float, set to null if the function is not defined on
     * the input
     */
    double? tryValueAt(int i);
}