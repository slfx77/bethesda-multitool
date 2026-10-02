namespace BethesdaMultitool.Core.Formats.DiscImage.Chd;

/// <summary>
///     The 2048-byte-logical-sector view over a CD-shaped CHD, with the raw physical sectors of
///     every track underneath it — the same seam <see cref="RawSectorSource" /> gives a
///     <c>.cue</c>/<c>.bin</c> set. Tracks stack in LBA space in the order they appear (a redump
///     layout: the first data track at LBA 0, audio tracks after it), each spanning exactly the
///     frames its metadata declares; the four-frame padding chdman leaves between tracks in the
///     file is skipped, never addressed.
///     <para>
///         ⚠⚠ AN AUDIO TRACK IS STORED BYTE-SWAPPED. A CHD holds CD-DA as big-endian 16-bit
///         samples while a <c>.bin</c> holds them little-endian, so this source swaps every word
///         of an audio frame on the way out and a caller sees the same bytes either container
///         gives. Measured 2026-09-09 against chdman 0.289: on a synthetic 400-frame audio disc
///         <c>extractraw</c> matches the source verbatim 0/400 and byte-swapped 400/400, while
///         <c>extractcd</c> returns the source exactly; on the Battlespire disc, data-track frames
///         match verbatim 4/4 and audio-track frames only swapped 7/7. Handing the stored order
///         through would write WAV files of noise — and the per-track pregap sectors carry a
///         swapped sync pattern (<c>ff 00 ff ff …</c>), so even "it looks like a sync" does not
///         catch it. Data tracks are stored verbatim and must NOT be swapped.
///     </para>
/// </summary>
internal sealed class ChdSectorSource : IDiscSectorSource, IRawTrackSource
{
    private const int Mode2TailSize = 2336;

    private readonly ChdStream _stream;
    private readonly byte[] _frame = new byte[ChdFile.CdFrameSize];
    private readonly List<(DiscTrackRegion Region, ChdCdTrack Track)> _tracks = [];

    public ChdSectorSource(ChdFile chd)
    {
        ArgumentNullException.ThrowIfNull(chd);
        if (!chd.IsCdShaped)
        {
            throw new InvalidDataException($"'{Path.GetFileName(chd.Path)}' carries no CD track metadata.");
        }

        _stream = new ChdStream(chd, ownsFile: true);
        long lba = 0;
        foreach (var track in chd.Tracks)
        {
            var physical = track.DataSize;
            if (physical is not (IsoSectorSource.UserDataSize or Mode2TailSize or ChdFile.CdSectorSize))
            {
                throw new InvalidDataException($"CHD track {track.Number} ({track.Type}) has a {physical}-byte sector form this reader does not serve.");
            }

            var region = new DiscTrackRegion(lba, track.Frames, chd.Path, track.ChdFrameOffset * ChdFile.CdFrameSize, physical, track.IsAudio);
            _tracks.Add((region, track));
            lba += track.Frames;
        }

        SectorCount = lba;
        Tracks = _tracks.Select(t => t.Region).ToList();
    }

    public IReadOnlyList<DiscTrackRegion> Tracks { get; }

    public long SectorCount { get; }

    public bool HasRawSectors => true;

    public byte ReadSector(long lba, Span<byte> buffer)
    {
        var target = buffer[..IsoSectorSource.UserDataSize];
        var (region, track) = FindTrack(lba);
        ReadFrame(region, track, lba);
        switch (region.PhysicalSectorSize)
        {
            case IsoSectorSource.UserDataSize:
                _frame.AsSpan(0, 2048).CopyTo(target);
                return 0;

            case Mode2TailSize:
                _frame.AsSpan(8, 2048).CopyTo(target);
                return _frame[2];

            default:
                if (_frame[15] == 2)
                {
                    _frame.AsSpan(24, 2048).CopyTo(target);
                    return _frame[18];
                }

                _frame.AsSpan(16, 2048).CopyTo(target);
                return 0;
        }
    }

    public byte ReadSectorTail(long lba, Span<byte> buffer)
    {
        var target = buffer[..Mode2TailSize];
        var (region, track) = FindTrack(lba);
        ReadFrame(region, track, lba);
        switch (region.PhysicalSectorSize)
        {
            case IsoSectorSource.UserDataSize:
                target.Clear();
                _frame.AsSpan(0, 2048).CopyTo(target[8..]);
                return 0;

            case Mode2TailSize:
                _frame.AsSpan(0, Mode2TailSize).CopyTo(target);
                return _frame[2];

            default:
                _frame.AsSpan(16, Mode2TailSize).CopyTo(target);
                return _frame[15] == 2 ? _frame[18] : (byte)0;
        }
    }

    /// <summary>The physical bytes of one sector (2352 for a raw track), for CD-DA extraction.</summary>
    public int ReadRawSector(long lba, Span<byte> buffer)
    {
        var (region, track) = FindTrack(lba);
        ReadFrame(region, track, lba);
        var size = region.PhysicalSectorSize;
        _frame.AsSpan(0, size).CopyTo(buffer[..size]);
        return size;
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    private (DiscTrackRegion Region, ChdCdTrack Track) FindTrack(long lba)
    {
        foreach (var track in _tracks)
        {
            if (lba >= track.Region.StartLba && lba < track.Region.EndLba)
            {
                return track;
            }
        }

        throw new InvalidDataException($"LBA {lba} is outside every track of the CHD.");
    }

    private void ReadFrame(DiscTrackRegion region, ChdCdTrack track, long lba)
    {
        var frame = track.ChdFrameOffset + (lba - region.StartLba);
        var read = _stream.ReadAt(frame * ChdFile.CdFrameSize, _frame);
        if (read != _frame.Length)
        {
            throw new InvalidDataException($"CHD frame {frame} (LBA {lba}) lies past the end of the image.");
        }

        if (track.IsAudio)
        {
            SwapSamples(_frame.AsSpan(0, ChdFile.CdSectorSize));
        }
    }

    /// <summary>Big-endian stored samples to the little-endian order a <c>.bin</c> and a WAV both use.</summary>
    private static void SwapSamples(Span<byte> sector)
    {
        for (var i = 0; i + 1 < sector.Length; i += 2)
        {
            (sector[i], sector[i + 1]) = (sector[i + 1], sector[i]);
        }
    }
}
