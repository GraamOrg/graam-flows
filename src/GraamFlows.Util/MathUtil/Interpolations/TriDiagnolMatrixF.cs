using System.Diagnostics;
using System.Text;

namespace GraamFlows.Util.MathUtil.Interpolations;

public class TriDiagonalMatrixF
{
   /// <summary>
   ///     The values for the sub-diagonal. A[0] is never used.
   /// </summary>
   public double[] A;

   /// <summary>
   ///     The values for the main diagonal.
   /// </summary>
   public double[] B;

   /// <summary>
   ///     The values for the super-diagonal. C[C.Length-1] is never used.
   /// </summary>
   public double[] C;

   /// <summary>
   ///     Construct an NxN matrix.
   /// </summary>
   public TriDiagonalMatrixF(int n)
    {
        A = new double[n];
        B = new double[n];
        C = new double[n];
    }

   /// <summary>
   ///     The width and height of this matrix.
   /// </summary>
   public int N => A != null ? A.Length : 0;

   /// <summary>
   ///     Indexer. Setter throws an exception if you try to set any not on the super, main, or sub diagonals.
   /// </summary>
   public double this[int row, int col]
    {
        get
        {
            var di = row - col;

            if (di == 0)
                return B[row];
            if (di == -1)
            {
                Debug.Assert(row < N - 1);
                return C[row];
            }

            if (di == 1)
            {
                Debug.Assert(row > 0);
                return A[row];
            }

            return 0;
        }
        set
        {
            var di = row - col;

            if (di == 0)
            {
                B[row] = value;
            }
            else if (di == -1)
            {
                Debug.Assert(row < N - 1);
                C[row] = value;
            }
            else if (di == 1)
            {
                Debug.Assert(row > 0);
                A[row] = value;
            }
            else
            {
                throw new ArgumentException("Only the main, super, and sub diagonals can be set.");
            }
        }
    }

   /// <summary>
   ///     Produce a string representation of the contents of this matrix.
   /// </summary>
   /// <param name="fmt">Optional. For String.Format. Must include the colon. Examples are ':0.000' and ',5:0.00' </param>
   /// <param name="prefix">Optional. Per-line indentation prefix.</param>
   public string ToDisplayString(string fmt = "", string prefix = "")
    {
        if (N > 0)
        {
            var s = new StringBuilder();
            var formatString = "{0" + fmt + "}";

            for (var r = 0; r < N; r++)
            {
                s.Append(prefix);

                for (var c = 0; c < N; c++)
                {
                    s.AppendFormat(formatString, this[r, c]);
                    if (c < N - 1) s.Append(", ");
                }

                s.AppendLine();
            }

            return s.ToString();
        }

        return prefix + "0x0 Matrix";
    }

   /// <summary>
   ///     Solve the system of equations this*x=d given the specified d.
   /// </summary>
   /// <remarks>
   ///     Uses the Thomas algorithm described in the wikipedia article:
   ///     http://en.wikipedia.org/wiki/Tridiagonal_matrix_algorithm
   ///     Not optimized. Not destructive.
   /// </remarks>
   /// <param name="d">Right side of the equation.</param>
   public double[] Solve(double[] d)
    {
        var n = N;

        if (d.Length != n)
            throw new ArgumentException("The input d is not the same size as this matrix.");

        // cPrime
        var cPrime = new double[n];
        cPrime[0] = C[0] / B[0];

        for (var i = 1; i < n; i++)
            cPrime[i] = C[i] / (B[i] - cPrime[i - 1] * A[i]);

        // dPrime
        var dPrime = new double[n];
        dPrime[0] = d[0] / B[0];

        for (var i = 1; i < n; i++)
            dPrime[i] = (d[i] - dPrime[i - 1] * A[i]) / (B[i] - cPrime[i - 1] * A[i]);

        // Back substitution
        var x = new double[n];
        x[n - 1] = dPrime[n - 1];

        for (var i = n - 2; i >= 0; i--)
            x[i] = dPrime[i] - cPrime[i] * x[i + 1];

        return x;
    }
}