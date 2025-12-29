using System.Diagnostics;
using System.Text.RegularExpressions;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.Util;

namespace GraamFlows.Assumptions;

/**
 * For now we just parse the PolyPaths vector string, we don't keep the resulting vector. If that is ever needed,
 * we can import the code from C# SPM.Valuation.Vector easily.
 */
public class PolyPathsVectorLanguageParser
{
    private PolyPathsVectorLanguageParser()
    {
    }

    public static float[] parse(string vectorDef, int length)
    {
        var values = new float[length];
        parse(vectorDef, values);
        return values;
    }

    public static IAnchorableVector parseAnchorableVector(string vectorDef, float prevValue)
    {
        return parseAnchorableVector(vectorDef, prevValue, null, null);
    }

    public static IAnchorableVector parseAnchorableVector(string vectorDef, double prevValue,
        IFunctionOfFloat unitConverter, int? defaultAnchorAbsT)
    {
        var vector = Vector.fromString(vectorDef, true, defaultAnchorAbsT);
        var nodes = vector.getNodes();
        if (nodes.Count == 1)
        {
            var val = unitConverter == null ? nodes[0].value : unitConverter.valueAt(nodes[0].value);
            return new ConstVector(val);
        }

        if (unitConverter != null)
            prevValue = unitConverter.valueAt(prevValue);

        var values = extractValuesArray(nodes, unitConverter);
        if (vector.isAnchored || defaultAnchorAbsT != null)
            return new AnchoredVector(vector.isAnchored ? vector.anchorAbsT : defaultAnchorAbsT.Value,
                values, prevValue);
        return new UnanchoredVector(values, prevValue);
    }

    private static double[] extractValuesArray(List<Vector.Node> nodes, IFunctionOfFloat unitConverter)
    {
        var nbValues = nodes[nodes.Count - 1].index + 1;
        var values = new double[nbValues];

        for (var i = 0; i < nodes.Count - 1; i++)
        {
            var current = nodes[i];
            var next = nodes[i + 1];
            Debug.Assert(next.index > current.index);

            for (var j = current.index; j < next.index; j++)
            {
                values[j] = current.value
                            + (next.value - current.value)
                            * (j - current.index) / (next.index - current.index);
                if (unitConverter != null)
                    values[j] = unitConverter.valueAt(values[j]);
            }
        }

        var lastNode = nodes[nodes.Count - 1];
        values[lastNode.index] = lastNode.value;
        if (unitConverter != null)
            values[lastNode.index] = unitConverter.valueAt(values[lastNode.index]);
        return values;
    }

    public static void parse(string vectorDef, float[] values)
    {
        var vector = Vector.fromString(vectorDef, false, null);
        var nodes = vector.getNodes();

        for (var i = 0; i < nodes.Count - 1; i++)
        {
            var current = nodes[i];
            var next = nodes[i + 1];
            var maxIdx = Math.Min(values.Length, next.index);
            for (var j = current.index; j < maxIdx; j++)
                values[j] = current.value
                            + (next.value - current.value)
                            * (j - current.index) / (next.index - current.index);
        }

        var lastNode = nodes[nodes.Count - 1];
        for (var j = lastNode.index; j < values.Length; j++) values[j] = lastNode.value;
    }

    private class Vector
    {
        private static readonly string FLOATING_NUMBER_PATTERN_STRING = "[-+]?(?:[0-9]*\\.[0-9]+|[0-9]+\\.?)";
        private static readonly Regex YYYYMM = new("^(?:19|20)\\d\\d(?:0[1-9]|1[0-2])$");

        private static readonly Regex
            RAMP_PATTERN = new("^\\s*(" + FLOATING_NUMBER_PATTERN_STRING + ")[rR](\\d+)\\s*$");

        private static readonly Regex PLATEAU_PATTERN =
            new("^\\s*(" + FLOATING_NUMBER_PATTERN_STRING + ")\\/(\\d+)\\s*$");

        private static readonly Regex TRAILING_VALUE_PATTERN =
            new("^\\s*(" + FLOATING_NUMBER_PATTERN_STRING + ")\\s*$");

        internal readonly int anchorAbsT;
        internal readonly bool isAnchored;

        private readonly List<Node> nodes;

        public Vector(List<Node> nodes, int anchorAbsT, bool isAnchored)
        {
            this.nodes = new List<Node>(nodes);
            this.anchorAbsT = anchorAbsT;
            this.isAnchored = isAnchored;
        }

        public List<Node> getNodes()
        {
            return nodes;
        }

        public static Vector fromString(string vectorDef, bool anchorable, int? defaultAnchorAbsT)
        {
            if (vectorDef == null || vectorDef.Length == 0)
                throw new VectorFormatException("cannot parse null or empty vector definition");

            var nodes = new List<Node>();

            var pieces = vectorDef.Split(',');

            var istart = 0;


            //201301,1.08/12,1.08R12,1.0
            var iStart = 0; // index where the vector starts
            var anchorAbsT = int.MinValue;
            if (anchorable)
            {
                if (pieces.Length > 0 && YYYYMM.IsMatch(pieces[0]))
                {
                    anchorAbsT = DateUtil.CalcAbsTFromYearMonth(int.Parse(pieces[0]));
                    iStart = 1;
                    if (pieces.Length < 2)
                        throw new VectorFormatException(
                            "empty vector definition (empty after removing anchor date)");
                }
                else if (defaultAnchorAbsT.HasValue)
                {
                    anchorAbsT = defaultAnchorAbsT.Value;
                }
            }

            for (var i = iStart; i < pieces.Length; i++)
            {
                MatchCollection matcher;
                try
                {
                    if ((matcher = RAMP_PATTERN.Matches(pieces[i])).Count > 0)
                    {
                        if (i == pieces.Length - 1)
                            throw new VectorFormatException(
                                "A ramp ('" + pieces[i] + "') must be followed by something!");
                        nodes.Add(new Node(istart, float.Parse(matcher[0].Groups[1].Value)));
                        istart += int.Parse(matcher[0].Groups[2].Value);
                        continue;
                    }

                    if ((matcher = PLATEAU_PATTERN.Matches(pieces[i])).Count > 0)
                    {
                        var value = float.Parse(matcher[0].Groups[1].Value);
                        var length = int.Parse(matcher[0].Groups[2].Value);
                        nodes.Add(new Node(istart, value));
                        if (length > 1)
                            nodes.Add(new Node(istart + length - 1, value));
                        istart += length;
                        continue;
                    }

                    if ((matcher = TRAILING_VALUE_PATTERN.Matches(pieces[i])).Count > 0)
                    {
                        nodes.Add(new Node(istart, float.Parse(matcher[0].Groups[1].Value)));
                        if (i != pieces.Length - 1) // this is not the end of the vector
                            istart += 1;
                        continue;
                    }

                    if (pieces.Length == 1)
                    {
                        var userVec = vectorDef.Split(' ');
                        for (var iVec = 0; iVec != userVec.Length; ++iVec)
                            nodes.Add(new Node(iStart++, float.Parse(userVec[iVec])));
                        continue;
                    }
                }
                catch (Exception e)
                {
                    throw new VectorFormatException($"{pieces[i]} in {vectorDef} has invalid numerical values {e}");
                }

                throw new VectorFormatException("does not know what to do with '" + pieces[i] + "'");
            }

            // sanity check
            if (anchorable && nodes[0].value > 100000)
                throw new VectorFormatException(
                    $"value of {nodes[0].value} is too big but does not match a reasonable date YYYYMM");
            return new Vector(nodes, anchorAbsT, anchorAbsT != int.MinValue);
        }

        public class Node
        {
            public readonly int index;
            public readonly float value;

            public Node(int index, float value)
            {
                this.index = index;
                this.value = value;
            }
        }
    }

    public class VectorFormatException : Exception
    {
        public VectorFormatException(string message) : base(message)
        {
        }
    }
}