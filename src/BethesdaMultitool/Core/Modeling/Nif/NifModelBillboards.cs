using System.Globalization;
using System.Numerics;
using System.Text;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Maps an NiBillboardNode's stored Billboard Mode to Shared's <see cref="SceneBillboard" /> as the FNV/Fallout 3
///     engine faces it (RE-25, docs/formats/nif-animation-engine-behavior-20260925.md; the verifier's corrections and the
///     2026-09-27 runtime re-check apply). Pure.
/// </summary>
/// <remarks>
///     <para>
///         The engine switches on <c>value &amp; 7</c> (RotateToCamera, GECK <c>and eax,7</c> at 0x82F1CF, byte-identical
///         in FalloutNV.exe 1.4.0.525). Bit 3 is the update-controllers flag LoadBinary sets on every load, so stored
///         values 8 to 15 are bit-identical to 0 to 7 (2,400 of 2,400 scenes), and 6 and 7 do not face. Every encoding
///         has a zero pivot and anchor (the rotation pivots at the node's world origin; translation and scale are
///         unchanged), keeps <see cref="SceneBillboard.RawMode" /> = the stored value, and the node-local front and up of
///         the Z-up NIF basis:
///     </para>
///     <list type="table">
///         <item>
///             <term>0 ALWAYS_FACE_CAMERA</term>
///             <description>
///                 CameraPlane, Rigid true, Roll NodeUp, Front +Z, Up +Y: Z = -f, Y = the node's own animated +Y
///                 projected, X = Y x Z.
///             </description>
///         </item>
///         <item>
///             <term>1 ROTATE_ABOUT_UP</term>
///             <description>
///                 CameraPosition, LockedAxis +Y in the Node frame, Rigid false, Front +Z, Up +Y: the pre-facing world
///                 turned about its own animated +Y until +Z points at the projected camera position.
///             </description>
///         </item>
///         <item>
///             <term>2 RIGID_FACE_CAMERA</term>
///             <description>CameraPlane, Rigid true, Roll Camera, Front +Z, Up +Y: [r, u, -f].</description>
///         </item>
///         <item>
///             <term>3 ALWAYS_FACE_CENTER</term>
///             <description>
///                 CameraPosition, Rigid true, Roll NodeUp, Front +Z, Up +Y, with the plane-fallback cosine and the
///                 unfaced distance: Z = t, Y = the node's own +Y projected; mode 0 in the cone; unfaced in the ball.
///             </description>
///         </item>
///         <item>
///             <term>4 RIGID_FACE_CENTER</term>
///             <description>
///                 CameraPosition, Rigid true, Roll CameraSwung, Front +Z, Up +Y, with the same two thresholds:
///                 minarc(-f to t) [r, u, -f]; [r, u, -f] in the cone; unfaced in the ball.
///             </description>
///         </item>
///         <item>
///             <term>5 BSROTATE_ABOUT_UP</term>
///             <description>
///                 CameraPosition, LockedAxis +Z in the Document frame, Rigid true, Front +Y, Up +Z, Reflection +X: the
///                 pre-facing rotation is discarded, +Y is horizontal toward the camera, +Z is document (world) up,
///                 X = Z x Y (determinant -1).
///             </description>
///         </item>
///         <item>
///             <term>6, 7</term>
///             <description>No billboard (null): the node keeps its pre-facing world.</description>
///         </item>
///     </list>
///     <para>
///         Modes 3 and 4 carry <see cref="PlaneFallbackCosine" /> and <see cref="UnfacedDistanceSquared" />, and the
///         reader-established <see cref="SceneBillboard.PlaneFallbackEdge" /> where the source key bounds it
///         (<see cref="NifModelBillboardSource.PlaneFallbackEdge" />). Provenance and evidence come from the source key.
///     </para>
///     <para>
///         Not expressible in the contract, and left to the engine's measure-zero cases (RE-25 item 4): mode 0's fixed
///         frame [-r, -u, -f] within 1e-6 of +-Y0, mode 1's and mode 5's exact-zero projection (W0, and I for mode 5),
///         and the mirror of modes 3 and 4 when the camera looks exactly away from the node.
///     </para>
///     <para>
///         Each occurrence also declares its own <see cref="SceneBillboard.SourceScale" /> (<see cref="SourceScale" />):
///         the signed product of the stored NiAVObject Scale fields from the document root down to and including the
///         billboard node, which the engine keeps through the facing (RE-25 item 1) and which no matrix of the document
///         establishes. It does not change the encoding.
///     </para>
/// </remarks>
internal static class NifModelBillboards
{
    /// <summary>The bits of the stored value the engine switches on.</summary>
    public const ulong ModeMask = 7;

    /// <summary>Bit 3 of the stored value: the update-controllers flag, never part of the mode.</summary>
    public const ulong UpdateControllersBit = 8;

    /// <summary>
    ///     The plane-fallback cosine of modes 3 and 4: float32(0.999999) - 2^-25 = 0.9999989569187164. The runtime keeps
    ///     the camera plane while its float32 dot is at least float32(0.999999) (0xA7DD7D-0xA7DD90); this is the real
    ///     cosine at which that float32 compare switches (the midpoint below float32(0.999999)).
    /// </summary>
    public const double PlaneFallbackCosine = 0.9999989569187164;

    /// <summary>
    ///     The unfaced squared distance of modes 3 and 4: float32(0.001) = 0.0010000000474974513, compared strictly
    ///     (RotateToCenter returns false and the node is not faced while |c - p|^2 is below it).
    /// </summary>
    public const double UnfacedDistanceSquared = 0.0010000000474974513;

    /// <summary>The recorded rule for the pivot and anchor.</summary>
    public const string PivotRule =
        "pivot and anchor (0, 0, 0): the node's world origin (RE-25: the rotation pivots at the node origin; translation " +
        "and scale are unchanged)";

    /// <summary>The opening of every <see cref="SceneBillboardSourceScale.Evidence" /> the reader writes.</summary>
    public const string SourceScaleEvidencePrefix = "NiAVObject Scale, root to node: ";

    /// <summary>The closing of the evidence of a product no step of which rounded.</summary>
    public const string ExactProductNote = "(exact double product of the stored float32 values)";

    /// <summary>The closing of the evidence of a product at least one step of which rounded.</summary>
    public const string RoundedProductNote = "(double product of the stored float32 values, rounded)";

    /// <summary>
    ///     The most chain links the evidence lists; a longer chain lists its first and last half of this many and counts
    ///     the links between them.
    /// </summary>
    public const int MaximumEvidenceLinks = 16;

    /// <summary>
    ///     The smallest product magnitude, 2^-968, at which a fused multiply-add still proves a step exact: below it the
    ///     step's rounding error may itself be too small for a double, so the step is counted as rounded.
    /// </summary>
    private static readonly double ExactResidualFloor = Math.ScaleB(1.0, -968);

    /// <summary>The recorded rule for effective modes 6 and 7.</summary>
    public const string NoFacingRule =
        "none: effective modes 6 and 7 take RotateToCamera's default branch, whose face matrix is the identity, so the " +
        "node keeps its pre-facing world (RE-25)";

    /// <summary>The nif.xml option name of a value, or null for a value nif.xml does not define.</summary>
    public static string? ModeName(ulong rawMode)
    {
        return rawMode switch
        {
            0 => "ALWAYS_FACE_CAMERA",
            1 => "ROTATE_ABOUT_UP",
            2 => "RIGID_FACE_CAMERA",
            3 => "ALWAYS_FACE_CENTER",
            4 => "RIGID_FACE_CENTER",
            5 => "BSROTATE_ABOUT_UP",
            8 => "UNKNOWN_8",
            9 => "ROTATE_ABOUT_UP2",
            10 => "UNKNOWN_10",
            11 => "UNKNOWN_11",
            12 => "UNKNOWN_12",
            _ => null
        };
    }

    /// <summary>
    ///     The mode the engine applies to a stored value: <c>value &amp; 7</c>. It holds for every stored u16: the runtime
    ///     LoadBinary also keeps values above 15 (it stores them <c>| 8</c>, for example 16 as 0x18 and 65535 as 0xFFFF;
    ///     RE-25 runtime re-check, item 5(e)) and RotateToCamera reads only the low three bits.
    /// </summary>
    public static int EffectiveMode(ulong rawMode)
    {
        return (int)(rawMode & ModeMask);
    }

    /// <summary>Maps one stored mode under one source key.</summary>
    /// <param name="rawMode">The stored Billboard Mode, kept as <see cref="SceneBillboard.RawMode" />.</param>
    /// <param name="source">The read's source key (provenance, evidence and the plane-fallback edge).</param>
    /// <param name="sourceScale">
    ///     The occurrence's rest world scalar (<see cref="SourceScale" />), or null to leave it unstated. It does not change
    ///     any other field.
    /// </param>
    /// <returns>The billboard, or null for effective modes 6 and 7, which do not face.</returns>
    public static SceneBillboard? Map(ulong rawMode, NifModelBillboardSource source,
        SceneBillboardSourceScale? sourceScale = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var zero = Vector3.Zero;
        var facing = source.FacingProvenance;
        var facingEvidence = source.FacingEvidence;
        var schedule = source.ScheduleProvenance;
        var scheduleEvidence = source.ScheduleEvidence;
        return EffectiveMode(rawMode) switch
        {
            0 => new SceneBillboard(SceneBillboardAim.CameraPlane, null, true, zero, zero, rawMode, Vector3.UnitZ,
                Vector3.UnitY, SceneBillboardRoll.NodeUp, lockedAxisFrame: null, facingProvenance: facing,
                facingEvidence: facingEvidence, scheduleProvenance: schedule, scheduleEvidence: scheduleEvidence)
            {
                SourceScale = sourceScale
            },
            1 => new SceneBillboard(SceneBillboardAim.CameraPosition, Vector3.UnitY, false, zero, zero, rawMode,
                Vector3.UnitZ, Vector3.UnitY, null, SceneBillboardAxisFrame.Node, facingProvenance: facing,
                facingEvidence: facingEvidence, scheduleProvenance: schedule, scheduleEvidence: scheduleEvidence)
            {
                SourceScale = sourceScale
            },
            2 => new SceneBillboard(SceneBillboardAim.CameraPlane, null, true, zero, zero, rawMode, Vector3.UnitZ,
                Vector3.UnitY, SceneBillboardRoll.Camera, lockedAxisFrame: null, facingProvenance: facing,
                facingEvidence: facingEvidence, scheduleProvenance: schedule, scheduleEvidence: scheduleEvidence)
            {
                SourceScale = sourceScale
            },
            3 => new SceneBillboard(SceneBillboardAim.CameraPosition, null, true, zero, zero, rawMode, Vector3.UnitZ,
                Vector3.UnitY, SceneBillboardRoll.NodeUp, lockedAxisFrame: null,
                planeFallbackCosine: PlaneFallbackCosine, unfacedDistanceSquared: UnfacedDistanceSquared,
                planeFallbackEdge: source.PlaneFallbackEdge, facingProvenance: facing, facingEvidence: facingEvidence,
                scheduleProvenance: schedule, scheduleEvidence: scheduleEvidence)
            {
                SourceScale = sourceScale
            },
            4 => new SceneBillboard(SceneBillboardAim.CameraPosition, null, true, zero, zero, rawMode, Vector3.UnitZ,
                Vector3.UnitY, SceneBillboardRoll.CameraSwung, lockedAxisFrame: null,
                planeFallbackCosine: PlaneFallbackCosine, unfacedDistanceSquared: UnfacedDistanceSquared,
                planeFallbackEdge: source.PlaneFallbackEdge, facingProvenance: facing, facingEvidence: facingEvidence,
                scheduleProvenance: schedule, scheduleEvidence: scheduleEvidence)
            {
                SourceScale = sourceScale
            },
            5 => new SceneBillboard(SceneBillboardAim.CameraPosition, Vector3.UnitZ, true, zero, zero, rawMode,
                Vector3.UnitY, Vector3.UnitZ, null, SceneBillboardAxisFrame.Document, reflection: Vector3.UnitX,
                facingProvenance: facing, facingEvidence: facingEvidence, scheduleProvenance: schedule,
                scheduleEvidence: scheduleEvidence)
            {
                SourceScale = sourceScale
            },
            _ => null
        };
    }

    /// <summary>
    ///     The occurrence's <see cref="SceneBillboard.SourceScale" />: the signed product of the stored NiAVObject Scale
    ///     fields from the document root down to and including the billboard node, multiplied in double in that order.
    /// </summary>
    /// <param name="chain">The occurrence's chain, root first: each link's block index and stored Scale.</param>
    /// <returns>
    ///     The declaration, or null (unstated) when the double product overflows, or underflows to zero although no
    ///     stored Scale is zero, so that it could state neither the scalar nor its sign class.
    /// </returns>
    /// <exception cref="ArgumentException">The chain is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A stored Scale is not finite.</exception>
    /// <remarks>
    ///     <para>
    ///         The value is read from the fields, never from a matrix. The engine keeps the uniform world scale, sign
    ///         included, as a scalar of its own and replaces only the rotation (RE-25 item 1), while a negative stored
    ///         Scale over an improper stored rotation reads to the same matrix as the positive Scale over the proper
    ///         rotation: <see cref="NifModelTransform" /> writes the first as a positive TRS scale, and the composed
    ///         matrix's determinant is positive. Neither recovers the engine's scalar.
    ///     </para>
    ///     <para>
    ///         Provenance is <see cref="SceneValueProvenance.Authored" /> for every source key: each factor is a Scale field
    ///         stored in the stream and read verbatim, the contract defines the value as their product, and no engine
    ///         behavior is inferred (the facing provenance qualifies the facing on its own). A zero factor gives the zero
    ///         whose sign is the product of the factors' signs, as IEEE multiplication gives it, exactly. Otherwise each
    ///         step is checked with a fused multiply-add, and the evidence says the product is exact only when no step
    ///         rounded (a product of two float32 values always fits a double; a longer chain of general values can round).
    ///     </para>
    /// </remarks>
    public static SceneBillboardSourceScale? SourceScale(IReadOnlyList<(int Block, float Scale)> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0)
        {
            throw new ArgumentException("A billboard occurrence's chain holds at least its own node.", nameof(chain));
        }

        var zero = false;
        var negative = false;
        foreach (var (_, scale) in chain)
        {
            if (!float.IsFinite(scale))
            {
                throw new ArgumentOutOfRangeException(nameof(chain), scale, "Every stored Scale is finite.");
            }

            zero |= scale == 0f;
            negative ^= float.IsNegative(scale);
        }

        double product;
        var exact = true;
        if (zero)
        {
            product = negative ? double.NegativeZero : 0.0;
        }
        else
        {
            product = 1.0;
            foreach (var (_, scale) in chain)
            {
                var next = product * scale;
                if (!double.IsFinite(next) || next == 0)
                {
                    return null;
                }

                exact &= Math.Abs(next) >= ExactResidualFloor && Math.FusedMultiplyAdd(product, scale, -next) == 0;
                product = next;
            }
        }

        return new SceneBillboardSourceScale(product, SceneValueProvenance.Authored, Evidence(chain, product, exact));
    }

    /// <summary>
    ///     The evidence of one product: every link as <c>block {index} ({Scale})</c> from the root, the first and last
    ///     <see cref="MaximumEvidenceLinks" /> / 2 of a longer chain with the count between them, the product, and whether
    ///     it is exact.
    /// </summary>
    private static string Evidence(IReadOnlyList<(int Block, float Scale)> chain, double product, bool exact)
    {
        var elided = chain.Count > MaximumEvidenceLinks;
        var head = elided ? MaximumEvidenceLinks / 2 : chain.Count;
        var tail = elided ? chain.Count - MaximumEvidenceLinks / 2 : chain.Count;
        var text = new StringBuilder(SourceScaleEvidencePrefix);
        for (var i = 0; i < head; i++)
        {
            if (i > 0)
            {
                text.Append(" x ");
            }

            text.Append(CultureInfo.InvariantCulture, $"block {chain[i].Block} ({chain[i].Scale:R})");
        }

        if (elided)
        {
            text.Append(CultureInfo.InvariantCulture, $" x ({tail - head} links not listed)");
        }

        for (var i = tail; i < chain.Count; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $" x block {chain[i].Block} ({chain[i].Scale:R})");
        }

        text.Append(CultureInfo.InvariantCulture, $" = {product:R} ");
        return text.Append(exact ? ExactProductNote : RoundedProductNote).ToString();
    }
}
