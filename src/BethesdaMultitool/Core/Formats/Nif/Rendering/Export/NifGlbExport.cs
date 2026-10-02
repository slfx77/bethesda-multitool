using BethesdaMultitool.Core.Diagnostics;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Media.Models;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     The NIF GLB export entry point that chooses between the native <see cref="GlbWriter" /> and the normalized
///     shared writer over the same assembled <see cref="GlbScene" />.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="GlbWriter" /> is not routed internally; it stays byte-identical and remains both the decline
///         route and the parity oracle. Callers plan first and write second: <see cref="Plan(GlbScene, NifTextureResolver, NifGlbExportRequest, CancellationToken)" />
///         runs the neutral adapter, which works on clones, before any native write normalizes winding on the source
///         in place.
///     </para>
///     <para>
///         Planning checks, in order: the preference; family admission; scope; <see cref="NifNeutralSceneAdapter" />;
///         the shared build under <see cref="GltfExportIntent.Interchange" />. A <see cref="NotSupportedException" />
///         from the build is stage <see cref="NifGlbDeclineStage.SharedBuild" />. An
///         <see cref="InvalidDataException" /> thrown by the shared validation or build (identified by the throwing
///         assembly) is stage <see cref="NifGlbDeclineStage.SharedValidation" /> and is logged as a warning; the
///         same exception type from anywhere else, such as a texture resolver, is not caught.
///     </para>
/// </remarks>
internal static class NifGlbExport
{
    /// <summary>The simple names of the shared assemblies whose validation faults are routed, not thrown.</summary>
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.Ordinal)
    {
        typeof(SceneValidation).Assembly.GetName().Name!,
        typeof(SceneGltfBuilder).Assembly.GetName().Name!
    };

    /// <summary>Plans an export against the production admission table.</summary>
    /// <param name="scene">The assembled scene; planning never modifies it.</param>
    /// <param name="textureResolver">The resolver both writers use.</param>
    /// <param name="request">The label, family, conversion state and preference.</param>
    /// <param name="cancellationToken">Cancels planning, including the adapter and the shared build.</param>
    /// <returns>The decision and, for the normalized route, its built documents.</returns>
    internal static NifGlbExportPlan Plan(
        GlbScene scene,
        NifTextureResolver textureResolver,
        NifGlbExportRequest request,
        CancellationToken cancellationToken)
    {
        return Plan(scene, textureResolver, request, NifGlbNormalizedAdmission.IsAdmitted, SceneGltfBuilder.Build,
            cancellationToken);
    }

    /// <summary>Plans an export with a caller-supplied admission predicate instead of the production table.</summary>
    /// <param name="scene">The assembled scene; planning never modifies it.</param>
    /// <param name="textureResolver">The resolver both writers use.</param>
    /// <param name="request">The label, family, conversion state and preference.</param>
    /// <param name="isAdmitted">Admission by (family, converted-from-big-endian); tests use it to admit a family.</param>
    /// <param name="cancellationToken">Cancels planning, including the adapter and the shared build.</param>
    /// <returns>The decision and, for the normalized route, its built documents.</returns>
    internal static NifGlbExportPlan Plan(
        GlbScene scene,
        NifTextureResolver textureResolver,
        NifGlbExportRequest request,
        Func<NifExportFamily, bool, bool> isAdmitted,
        CancellationToken cancellationToken)
    {
        return Plan(scene, textureResolver, request, isAdmitted, SceneGltfBuilder.Build, cancellationToken);
    }

    /// <summary>Plans an export with caller-supplied admission and shared-build seams.</summary>
    /// <param name="scene">The assembled scene; planning never modifies it.</param>
    /// <param name="textureResolver">The resolver both writers use.</param>
    /// <param name="request">The label, family, conversion state and preference.</param>
    /// <param name="isAdmitted">Admission by (family, converted-from-big-endian).</param>
    /// <param name="build">The shared build; production passes <see cref="SceneGltfBuilder.Build" />.</param>
    /// <param name="cancellationToken">Cancels planning, including the adapter and the shared build.</param>
    /// <returns>The decision and, for the normalized route, its built documents.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was observed; it is never reported as a decline.</exception>
    internal static NifGlbExportPlan Plan(
        GlbScene scene,
        NifTextureResolver textureResolver,
        NifGlbExportRequest request,
        Func<NifExportFamily, bool, bool> isAdmitted,
        Func<ModelDocument, GltfExportIntent, CancellationToken, GltfDocument> build,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Name);
        ArgumentNullException.ThrowIfNull(isAdmitted);
        ArgumentNullException.ThrowIfNull(build);
        cancellationToken.ThrowIfCancellationRequested();

        var preference = request.Preference;
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(request), preference, "Unknown GLB writer preference.");
        }

        if (preference == NifGlbWriterPreference.Native)
        {
            return WithoutDocuments(request, NifGlbEligibility.Eligible);
        }

        if (!isAdmitted(request.Family, request.ConvertedFromBigEndian))
        {
            return WithoutDocuments(request, NifGlbEligibility.Declined(NifGlbDeclineStage.FamilyNotAdmitted,
                FamilyNotAdmittedReason(request.Family, request.ConvertedFromBigEndian)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (NifGlbNormalizedAdmission.ScopeReason(scene) is { } scopeReason)
        {
            return WithoutDocuments(request, NifGlbEligibility.Declined(NifGlbDeclineStage.Scope, scopeReason));
        }

        ModelDocument sceneDocument;
        try
        {
            if (!NifNeutralSceneAdapter.TryAdapt(scene, textureResolver, request.Name, out var adapted,
                    out var adapterReason, cancellationToken))
            {
                return WithoutDocuments(request, NifGlbEligibility.Declined(NifGlbDeclineStage.Adapter,
                    adapterReason ?? "The neutral scene adapter declined without a reason."));
            }

            sceneDocument = adapted;
        }
        catch (InvalidDataException exception)
        {
            // Checked in the handler rather than a filter, so the throwing frame is read from a stack trace
            // that is complete; anything not raised by the shared assemblies is rethrown unchanged.
            if (!IsSharedFault(exception))
            {
                throw;
            }

            return SharedValidationFault(request, exception);
        }

        GltfDocument gltfDocument;
        try
        {
            gltfDocument = build(sceneDocument, GltfExportIntent.Interchange, cancellationToken);
        }
        catch (NotSupportedException exception)
        {
            return WithoutDocuments(request,
                NifGlbEligibility.Declined(NifGlbDeclineStage.SharedBuild, ReasonOf(exception)));
        }
        catch (InvalidDataException exception)
        {
            if (!IsSharedFault(exception))
            {
                throw;
            }

            return SharedValidationFault(request, exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new NifGlbExportPlan(
            NifGlbExportRouting.Decide(preference, NifGlbEligibility.Eligible),
            sceneDocument,
            gltfDocument);
    }

    /// <summary>Produces the GLB bytes of a planned export from the writer its decision chose.</summary>
    /// <param name="plan">A plan made for this scene and resolver.</param>
    /// <param name="scene">The same scene the plan was made for; the native writer normalizes its winding in place.</param>
    /// <param name="textureResolver">The same resolver the plan was made with.</param>
    /// <param name="cancellationToken">Cancels the shared encoder.</param>
    /// <returns><see cref="GlbWriter" /> bytes for the native route, <see cref="GltfExporter.Encode" /> bytes otherwise.</returns>
    /// <exception cref="InvalidOperationException">The plan was refused; nothing may be written.</exception>
    internal static byte[] WriteToBytes(
        NifGlbExportPlan plan,
        GlbScene scene,
        NifTextureResolver textureResolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(textureResolver);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfRefused(plan);

        return plan.Decision.IsNormalized
            ? GltfExporter.Encode(plan.GltfDocument!, cancellationToken)
            : GlbWriter.WriteToBytes(scene, textureResolver);
    }

    /// <summary>Writes a planned export to a file, publishing exactly as the native writer does.</summary>
    /// <param name="plan">A plan made for this scene and resolver.</param>
    /// <param name="scene">The same scene the plan was made for; the native writer normalizes its winding in place.</param>
    /// <param name="textureResolver">The same resolver the plan was made with.</param>
    /// <param name="outputPath">The GLB file to create or replace; its directory is created when missing.</param>
    /// <param name="cancellationToken">Cancels the shared encoder before anything is written.</param>
    /// <exception cref="InvalidOperationException">The plan was refused; nothing is written.</exception>
    internal static void Write(
        NifGlbExportPlan plan,
        GlbScene scene,
        NifTextureResolver textureResolver,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfRefused(plan);

        if (!plan.Decision.IsNormalized)
        {
            GlbWriter.Write(scene, textureResolver, outputPath);
            return;
        }

        var bytes = GltfExporter.Encode(plan.GltfDocument!, cancellationToken);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        File.WriteAllBytes(outputPath, bytes);
    }

    /// <summary>The decline reason for a family that is not on the admission table.</summary>
    /// <param name="family">The source family.</param>
    /// <param name="convertedFromBigEndian">Whether the source was converted from big-endian.</param>
    /// <returns>The reason text.</returns>
    internal static string FamilyNotAdmittedReason(NifExportFamily family, bool convertedFromBigEndian)
    {
        return convertedFromBigEndian
            ? $"The {family} family, converted from big-endian, is not admitted to the normalized GLB writer."
            : $"The {family} family is not admitted to the normalized GLB writer.";
    }

    /// <summary>Builds the plan for a native request or a declined eligibility result.</summary>
    private static NifGlbExportPlan WithoutDocuments(NifGlbExportRequest request, NifGlbEligibility eligibility)
    {
        return new NifGlbExportPlan(NifGlbExportRouting.Decide(request.Preference, eligibility), null, null);
    }

    /// <summary>Logs a shared validation fault and returns its <see cref="NifGlbDeclineStage.SharedValidation" /> plan.</summary>
    private static NifGlbExportPlan SharedValidationFault(NifGlbExportRequest request, InvalidDataException exception)
    {
        var reason = ReasonOf(exception);
        var plan = WithoutDocuments(request, NifGlbEligibility.Declined(NifGlbDeclineStage.SharedValidation, reason));
        var outcome = plan.Decision.Refused ? "the export is refused" : "the native writer is used";
        Logger.Instance.Warn(
            $"NIF GLB export '{request.Name}': stage {NifGlbDeclineStage.SharedValidation}: {reason} ({outcome}).");
        return plan;
    }

    /// <summary>The exception's message as a decline reason, or its type name when the message is blank.</summary>
    private static string ReasonOf(Exception exception)
    {
        return string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
    }

    /// <summary>Whether an invalid-data fault was thrown by the shared validation or build assemblies.</summary>
    /// <remarks>
    ///     <see cref="Exception.Source" /> names the assembly of the throwing frame. A fault raised by Bethesda code
    ///     (for example a malformed texture behind the resolver) is not a shared validation result and propagates.
    /// </remarks>
    private static bool IsSharedFault(InvalidDataException exception)
    {
        return exception.Source is { } source && SharedAssemblyNames.Contains(source);
    }

    /// <summary>Rejects a refused plan before any output is produced.</summary>
    private static void ThrowIfRefused(NifGlbExportPlan plan)
    {
        if (plan.Decision.Refused)
        {
            throw new InvalidOperationException(
                $"The normalized GLB writer was required but declined at {plan.Decision.Stage}: {plan.Decision.Reason}");
        }
    }
}
