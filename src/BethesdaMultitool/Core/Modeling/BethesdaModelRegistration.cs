using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Core.Modeling.Redguard;
using BethesdaMultitool.Core.Modeling.Shadowkey;
using BethesdaMultitool.Core.Modeling.Starfield;
using BethesdaMultitool.Core.Modeling.Xngine;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling;

/// <summary>
///     BMT's model composition root (design section 2.3; plan section 2): the explicit reader registrations and the app
///     option keys BMT's readers interpret. Writers, cache and memory estimates, and the workflow arrive with the CLI
///     slice (plan section 6, slice 4).
/// </summary>
public static class BethesdaModelRegistration
{
    /// <summary>
    ///     App option naming the game whose units apply (<c>fnv</c>, <c>fo3</c>, a game name, or <c>auto</c> for not
    ///     established). Set by the shell from <c>--game</c> or from the ESM detected under the data root. Cut 1c extends
    ///     the accepted values with <c>daggerfall</c>, <c>battlespire</c> and <c>redguard</c> for the XnGine readers; a
    ///     NIF item read under one of those values throws, and an XnGine item read under a NIF-era value throws, because
    ///     the user asserted a game the file cannot belong to.
    /// </summary>
    public const string GameOption = "bmt.game";

    /// <summary>App option stating how the shell established <see cref="GameOption" /> (quoted in the unit evidence).</summary>
    public const string GameEvidenceOption = "bmt.game-evidence";

    /// <summary>
    ///     App option naming the console a big-endian NIF was shipped for (<c>x360</c> or <c>ps3</c>), set by the shell
    ///     from <c>--platform</c>. It selects the byte order of packed vertex colors, the one measured difference between
    ///     the two consoles; absent, X360 is assumed and the reader says so with a diagnostic on every packed color
    ///     stream it decodes. No BMT format, profile or converter carries a console-platform notion the reader could
    ///     reuse (the converter tells the two byte orders of a NIF apart, not the two consoles), so this is the first.
    /// </summary>
    public const string PlatformOption = "bmt.platform";

    /// <summary>
    ///     App option naming the skeleton a <c>.kf</c> animation stream binds to (cut-1b slice 10, plan section 1.7), set by
    ///     the shell from <c>--skeleton</c>. The value names a file and is handed to the read context's companion resolver
    ///     exactly as given (the shell serves the file it names through that resolver); it wins over the nearest ancestor
    ///     <c>skeleton.nif</c> of the <c>.kf</c>'s virtual path, and when it names nothing the read is not supported rather
    ///     than falling back to the walk-up. It names a file and switches no behavior: a <c>.nif</c> read ignores it.
    /// </summary>
    public const string SkeletonOption = "bmt.skeleton";

    /// <summary>
    ///     App option naming the classic (pre-plugin-era) install an input sits in, as a <see cref="Games.BethesdaGame" />
    ///     name (<c>Daggerfall</c>, <c>Battlespire</c>, <c>Redguard</c>, or another classic title the locator knows).
    ///     Cut-1c plan section 6.2, step 3: the shell sets it once per input path from
    ///     <see cref="Games.ClassicGameLocator.DetectRootForFile" /> unless <c>--game</c> was given, and the XnGine game
    ///     identity consults it after the container facts and before the content rules. It is a key SEPARATE from
    ///     <see cref="GameOption" /> on purpose: a detected classic install must never reach
    ///     <see cref="NifModelUnits" />, which refuses every value that names no Gamebryo game, so a NIF read under a
    ///     detected Daggerfall install keeps working. A value the XnGine identity does not recognize as an XnGine game
    ///     (an Arena or Fallout install) answers nothing and is recorded as a diagnostic.
    /// </summary>
    public const string ClassicGameOption = "bmt.classic-game";

    /// <summary>
    ///     App option stating how the shell established <see cref="ClassicGameOption" /> (the detected install root),
    ///     quoted in the identity evidence.
    /// </summary>
    public const string ClassicGameEvidenceOption = "bmt.classic-game-evidence";

    /// <summary>
    ///     Creates the registry of BMT model readers: the NIF reader, the Starfield <c>.mesh</c> reader
    ///     (<see cref="StarfieldMeshModelReader" />, cut 2), since cut-1c slice 5 the XnGine <c>.3D</c> reader
    ///     (<see cref="XnGineModelReader" />) and since slice 6 the Redguard <c>.3DC</c> reader
    ///     (<see cref="Redguard3DcModelReader" />), and since cut 2 the Shadowkey mesh-record and zone readers
    ///     (<see cref="ShadowkeyMeshModelReader" />, <see cref="ShadowkeyZoneModelReader" />: a record opens with the u16
    ///     tag 7 and a <c>.zmp</c> with its zlib envelope, so neither is any other reader's content). The two XnGine probes recognize disjoint content (a v2.5 to v2.7 tag
    ///     with a plane walk that fails the <c>.3DC</c> shape test, or a 3dfx tag; a mesh tag that satisfies it), and an
    ///     XnGine tag is neither a NIF header line nor a Starfield leading dword, so no file is recognized twice.
    /// </summary>
    public static ModelSourceRegistry CreateReaders()
    {
        return new ModelSourceRegistry([
            new NifModelReader(), new StarfieldMeshModelReader(), new XnGineModelReader(), new Redguard3DcModelReader(),
            new ShadowkeyMeshModelReader(), new ShadowkeyZoneModelReader()
        ]);
    }

    /// <summary>
    ///     Creates a fresh per-item cache scope for <see cref="NifModelReader" />. The XnGine <c>.3D</c> and Redguard
    ///     <c>.3DC</c> readers resolve no companion yet and accept any scope; slice 8 of the cut-1c plan replaces this with
    ///     the composite cache the readers share.
    /// </summary>
    public static IModelReadCacheScope CreateCache()
    {
        return new NifModelReadCache();
    }
}
