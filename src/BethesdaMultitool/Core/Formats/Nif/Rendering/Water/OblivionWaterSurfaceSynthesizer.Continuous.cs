using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

internal static partial class OblivionWaterSurfaceSynthesizer
{
    /// <summary>
    ///     Creates an owned continuous-time producer for a signed R32_FLOAT height upload. The
    ///     existing legacy normal sequence remains cached independently. Each owner serializes
    ///     evaluation because its FFT scratch arrays are reused between frames.
    /// </summary>
    internal static ContinuousSurface CreateContinuousSurface(WaterSurfaceParams? surface, bool useHighResolution)
    {
        return new ContinuousSurface(surface, useHighResolution);
    }

    internal sealed class ContinuousSurface
    {
        private readonly float[] _angularFrequencies;
        private readonly float[] _imaginary;
        private readonly float _maximumAngularFrequency;
        private readonly float[] _real;
        private readonly SpectrumSeed _seed;

        internal ContinuousSurface(WaterSurfaceParams? surface, bool useHighResolution)
        {
            var settings = ResolveSettings(surface, useHighResolution);
            _seed = BuildSpectrumSeed(settings);
            Size = settings.GridResolution;
            _real = new float[Size * Size];
            _imaginary = new float[_real.Length];
            _angularFrequencies = new float[_real.Length];
            for (var y = 0; y < Size; y++)
            {
                var latticeY = y - Size / 2;
                for (var x = 0; x < Size; x++)
                {
                    var latticeX = x - Size / 2;
                    var kMagnitude = WaveNumberStep * MathF.Sqrt(latticeX * latticeX + latticeY * latticeY);
                    // 007E0840 writes the destination coordinate's own omega even where the
                    // high-resolution seed amplitude is copied from another lattice coordinate.
                    _angularFrequencies[y * Size + x] = MathF.Sqrt(Gravity * kMagnitude) * settings.WaveFrequency;
                }
            }

            if (_seed.Amplitudes.Any(value => !float.IsFinite(value)) ||
                _angularFrequencies.Any(value => !float.IsFinite(value)))
                throw new ArgumentException("The authored surface settings overflow the finite spectrum.",
                    nameof(surface));
            _maximumAngularFrequency = _angularFrequencies.Max();
        }

        internal int Size { get; }

        /// <summary>
        ///     WATERHMAP000 evolution followed by the unnormalized two-dimensional inverse FFT.
        ///     No cycle rounding, frame wrapping, absolute value, or normal encoding occurs here.
        ///     The receiving HMAP005/006 pass owns absolute-height sampling and normal conversion.
        /// </summary>
        internal void Evaluate(float elapsedSeconds, Span<float> destination)
        {
            if (!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0f ||
                !float.IsFinite(_maximumAngularFrequency * elapsedSeconds))
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
            if (destination.Length != _real.Length)
                throw new ArgumentException("The destination must contain exactly one height per grid texel.",
                    nameof(destination));

            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var index = y * Size + x;
                    var seedIndex = y * _seed.SeedStride + x;
                    var opposite = GetOppositeSeedCoordinate(x, y, Size);
                    var oppositeIndex = opposite.Y * _seed.SeedStride + opposite.X;
                    var phase = _angularFrequencies[index] * elapsedSeconds;
                    var cosine = MathF.Cos(phase);
                    var sine = MathF.Sin(phase);
                    var h0 = _seed.Amplitudes[seedIndex];
                    var h0Opposite = _seed.Amplitudes[oppositeIndex];
                    _real[index] = (h0Opposite + h0) * (cosine - sine);
                    _imaginary[index] = (h0Opposite - h0) * (sine + cosine);
                }
            }

            InverseFft2D(_real, _imaginary, Size);
            if (_real.Any(value => !float.IsFinite(value)))
                throw new InvalidOperationException("The evolved height field contains non-finite values.");
            _real.CopyTo(destination);
        }
    }
}
