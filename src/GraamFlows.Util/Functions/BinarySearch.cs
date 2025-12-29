namespace GraamFlows.Util.Functions;

public class BinarySearch
{
    public static int Position(int[] a, int b)
    {
        if (a.Length == 0)
            return -1;

        var low = 0;
        var high = a.Length - 1;

        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (b > a[middle])
                low = middle + 1;
            else if (b < a[middle])
                high = middle - 1;
            else
                return middle;
        }

        return -1;
    }

    public static bool Contains(int[] a, int b)
    {
        return Position(a, b) >= 0;
    }

    public static int InterpolationPosition(double[] a, double b)
    {
        if (a.Length == 0)
            return -1;
        if (b < a[0])
            return -1;
        if (b >= a[a.Length - 1])
            return a.Length - 1;

        var low = 0;
        var high = a.Length - 1;

        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (b > a[middle])
            {
                low = middle + 1;
                if (b < a[low])
                    return middle;
            }
            else if (b < a[middle])
            {
                high = middle - 1;
                if (b >= a[high])
                    return high;
            }
            else
            {
                // The element has been found
                return middle;
            }
        }

        return -1;
    }
}