namespace EgtAnalyzer.Verification;

internal static class LinearAlgebraUtils
{
    internal static double[] CreatePrincipalComponentSeed(int size, int component)
    {
        var seed = new double[size];
        for (var index = 0; index < size; index++)
        {
            seed[index] = ((index + 1) * (component + 3) % 17) + 1;
        }

        return seed;
    }

    internal static double[] MultiplyMatrixVector(double[,] matrix, double[] vector)
    {
        var size = vector.Length;
        var result = new double[size];
        for (var row = 0; row < size; row++)
        {
            var sum = 0d;
            for (var col = 0; col < size; col++)
            {
                sum += matrix[row, col] * vector[col];
            }

            result[row] = sum;
        }

        return result;
    }

    internal static void Orthogonalize(double[] vector, IReadOnlyList<double[]> basis)
    {
        foreach (var prior in basis)
        {
            var dot = DotProduct(vector, prior);
            for (var index = 0; index < vector.Length; index++)
            {
                vector[index] -= dot * prior[index];
            }
        }
    }

    internal static void ScaleVector(double[] vector, double scale)
    {
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] *= scale;
        }
    }

    internal static double VectorNorm(double[] vector)
    {
        return Math.Sqrt(DotProduct(vector, vector));
    }

    internal static double VectorDifferenceNormSquared(double[] left, double[] right)
    {
        var sum = 0d;
        for (var index = 0; index < left.Length; index++)
        {
            var delta = left[index] - right[index];
            sum += delta * delta;
        }

        return sum;
    }

    internal static double VectorSumNormSquared(double[] left, double[] right)
    {
        var sum = 0d;
        for (var index = 0; index < left.Length; index++)
        {
            var delta = left[index] + right[index];
            sum += delta * delta;
        }

        return sum;
    }

    internal static double RayleighQuotient(double[,] matrix, double[] vector)
    {
        var multiplied = MultiplyMatrixVector(matrix, vector);
        return DotProduct(vector, multiplied);
    }

    internal static double DotProduct(float[] left, float[] right)
    {
        double sum = 0;
        for (var index = 0; index < left.Length; index++)
        {
            sum += (double)left[index] * right[index];
        }

        return sum;
    }

    internal static double DotProduct(double[] left, double[] right)
    {
        double sum = 0;
        for (var index = 0; index < left.Length; index++)
        {
            sum += left[index] * right[index];
        }

        return sum;
    }

    internal static double[]? SolveLinearSystem(double[,] matrix, double[] rhs)
    {
        var size = rhs.Length;
        var augmented = new double[size, size + 1];
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
            {
                augmented[row, col] = matrix[row, col];
            }

            augmented[row, size] = rhs[row];
        }

        for (var pivot = 0; pivot < size; pivot++)
        {
            var pivotRow = pivot;
            var pivotMagnitude = Math.Abs(augmented[pivot, pivot]);
            for (var row = pivot + 1; row < size; row++)
            {
                var candidateMagnitude = Math.Abs(augmented[row, pivot]);
                if (candidateMagnitude > pivotMagnitude)
                {
                    pivotMagnitude = candidateMagnitude;
                    pivotRow = row;
                }
            }

            if (pivotMagnitude < 1e-12)
            {
                return null;
            }

            if (pivotRow != pivot)
            {
                for (var col = pivot; col <= size; col++)
                {
                    (augmented[pivot, col], augmented[pivotRow, col]) =
                        (augmented[pivotRow, col], augmented[pivot, col]);
                }
            }

            var divisor = augmented[pivot, pivot];
            for (var col = pivot; col <= size; col++)
            {
                augmented[pivot, col] /= divisor;
            }

            for (var row = 0; row < size; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                var factor = augmented[row, pivot];
                if (Math.Abs(factor) < 1e-18)
                {
                    continue;
                }

                for (var col = pivot; col <= size; col++)
                {
                    augmented[row, col] -= factor * augmented[pivot, col];
                }
            }
        }

        var solution = new double[size];
        for (var row = 0; row < size; row++)
        {
            solution[row] = augmented[row, size];
        }

        return solution;
    }
}
