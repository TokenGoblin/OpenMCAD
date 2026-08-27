using System.Collections.Immutable;

namespace OpenMCAD.Solver.Fake;

/// <summary>
/// The two pieces of dense linear algebra a small solver needs.
/// </summary>
/// <remarks>
/// <para>
/// Written here rather than taken from a library because <c>Directory.Packages.props</c> requires
/// an ADR for a new dependency and these are forty lines each. A linear-algebra package would be
/// the right answer for planegcs's job and is not the right answer for a fake's.
/// </para>
/// <para>
/// Both use partial pivoting. Without it a zero on the diagonal — which happens constantly here,
/// because a sketch being drawn has whole parameters no constraint touches — divides by zero on the
/// very first step.
/// </para>
/// </remarks>
internal static class Dense
{
    /// <summary>Solves a square system.</summary>
    /// <param name="matrix">The matrix, consumed.</param>
    /// <param name="right">The right-hand side.</param>
    /// <returns>The solution, or null if the matrix is singular.</returns>
    /// <remarks>
    /// Null rather than an exception. A singular system is the ordinary case for an
    /// under-constrained sketch, and the caller's response is to raise the damping and try again
    /// rather than to stop.
    /// </remarks>
    public static double[]? Solve(double[,] matrix, double[] right)
    {
        int n = right.Length;
        double[] solution = [.. right];

        for (int column = 0; column < n; ++column)
        {
            int pivot = column;

            for (int row = column + 1; row < n; ++row)
            {
                if (System.Math.Abs(matrix[row, column]) > System.Math.Abs(matrix[pivot, column]))
                {
                    pivot = row;
                }
            }

            if (System.Math.Abs(matrix[pivot, column]) < 1e-14)
            {
                return null;
            }

            if (pivot != column)
            {
                for (int k = 0; k < n; ++k)
                {
                    (matrix[column, k], matrix[pivot, k]) = (matrix[pivot, k], matrix[column, k]);
                }

                (solution[column], solution[pivot]) = (solution[pivot], solution[column]);
            }

            for (int row = column + 1; row < n; ++row)
            {
                double factor = matrix[row, column] / matrix[column, column];

                if (factor == 0)
                {
                    continue;
                }

                for (int k = column; k < n; ++k)
                {
                    matrix[row, k] -= factor * matrix[column, k];
                }

                solution[row] -= factor * solution[column];
            }
        }

        for (int row = n - 1; row >= 0; --row)
        {
            double sum = solution[row];

            for (int k = row + 1; k < n; ++k)
            {
                sum -= matrix[row, k] * solution[k];
            }

            solution[row] = sum / matrix[row, row];
        }

        return solution.All(double.IsFinite) ? solution : null;
    }

    /// <summary>Finds the rank of a matrix, and which rows added nothing.</summary>
    /// <param name="matrix">The matrix, copied.</param>
    /// <param name="tolerance">How small a pivot counts as zero.</param>
    /// <returns>The rank, and the rows that turned out to depend on earlier ones.</returns>
    /// <remarks>
    /// <para>
    /// The rows are what makes a diagnosis worth reading. §5.6 requires the conflicting constraints
    /// to be named, and elimination in the order the user made them means the row reported is the
    /// later of a dependent pair — the one they most recently added, which is nearly always the one
    /// they meant to be told about.
    /// </para>
    /// <para>
    /// Row pivoting is deliberately not used. It would find the same rank and report whichever row
    /// happened to have the largest entry, which is an accident of scale rather than a fact about
    /// what the user did.
    /// </para>
    /// </remarks>
    public static (int Rank, ImmutableArray<int> Dependent) Rank(
        double[,] matrix, double tolerance)
    {
        int rows = matrix.GetLength(0);
        int columns = matrix.GetLength(1);

        double[,] work = (double[,])matrix.Clone();

        ImmutableArray<int>.Builder dependent = ImmutableArray.CreateBuilder<int>();

        bool[] used = new bool[columns];
        int rank = 0;

        for (int row = 0; row < rows; ++row)
        {
            // Reduce this row against the pivots already found, then see whether anything is left.
            int best = -1;
            double largest = tolerance;

            for (int column = 0; column < columns; ++column)
            {
                if (used[column])
                {
                    continue;
                }

                double magnitude = System.Math.Abs(work[row, column]);

                if (magnitude > largest)
                {
                    largest = magnitude;
                    best = column;
                }
            }

            if (best < 0)
            {
                dependent.Add(row);
                continue;
            }

            used[best] = true;
            ++rank;

            double pivot = work[row, best];

            for (int other = row + 1; other < rows; ++other)
            {
                double factor = work[other, best] / pivot;

                if (factor == 0)
                {
                    continue;
                }

                for (int column = 0; column < columns; ++column)
                {
                    work[other, column] -= factor * work[row, column];
                }
            }
        }

        return (rank, dependent.ToImmutable());
    }
}
