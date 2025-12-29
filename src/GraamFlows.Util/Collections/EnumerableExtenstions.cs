namespace GraamFlows.Util.Collections;

public static class EnumerableExtenstions
{
    public static T BinarySearch<T>(this IList<T> list, Func<T, double> keySelector, double key, double tolerance = 0,
        bool constExtrapolate = true)
    {
        if (list.Count == 0)
            throw new InvalidOperationException("Item not found");

        var min = 0;
        var max = list.Count;
        while (min < max)
        {
            var mid = min + (max - min) / 2;
            var midItem = list[mid];
            var midKey = keySelector(midItem);
            var comp = midKey.CompareTo(key);
            if (comp < 0)
                min = mid + 1;
            else if (comp > 0)
                max = mid - 1;
            else
                return midItem;
        }

        if (constExtrapolate && list.Count == min)
            return list[list.Count - 1];
        var foundKey = keySelector(list[min]);
        var diffFromKey = Math.Abs(foundKey - key);
        if (diffFromKey <= tolerance)
            return list[min];

        var closest = list.OrderBy(x => Math.Abs(keySelector(x) - key)).First();
        if (Math.Abs(keySelector(closest) - key) > tolerance)
            throw new InvalidOperationException("Item not found");
        return list[list.IndexOf(closest)];
    }
}