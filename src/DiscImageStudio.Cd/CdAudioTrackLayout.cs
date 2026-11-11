namespace DiscImageStudio.Cd;

/// <summary>One planned playable lead-in track. PaddedPcmBytes is the exact track
/// length on disc: the decoded audio, silence-padded to whole sectors, the IMAPI
/// minimum track length, and the standard two-second tail pause.</summary>
public sealed record CdAudioTrackPlanEntry(
    int TrackNumber,
    string SourcePath,
    long SourcePcmBytes,
    long PaddedPcmBytes,
    long StartSector)
{
    /// <summary>Sectors the track occupies in the program area.</summary>
    public long Sectors => PaddedPcmBytes / CdDiscParameters.BytesPerSector;

    /// <summary>Playing time of the track as the CD player sees it, tail pause included.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Sectors / CdAudioTrackLayout.SectorsPerSecond);
}

/// <summary>The complete lead-in plan: every music track plus the sector where the
/// artwork track begins. IMAPI places DAO tracks back to back with no inserted
/// pregap, so the artwork start is simply the sum of the padded music tracks.</summary>
public sealed record CdAudioTrackPlan(
    IReadOnlyList<CdAudioTrackPlanEntry> Tracks,
    long MusicPcmBytes,
    long MusicSectorCount)
{
    public long ArtworkStartSector => MusicSectorCount;
}

/// <summary>
/// Sector planning for playable CD-DA lead-in tracks. The rules were measured
/// against the native <c>IMAPI2.MsftRawCDImageCreator</c>: StartOfLeadout equals
/// the sum of the track sectors exactly, no pregap is inserted between tracks,
/// and a track shorter than 300 sectors (four seconds) is rejected.
/// </summary>
public static class CdAudioTrackLayout
{
    /// <summary>CD-DA sectors per second (ECMA-130).</summary>
    public const int SectorsPerSecond = 75;

    /// <summary>IMAPI rejects tracks shorter than four seconds.</summary>
    public const int MinimumTrackSectors = 4 * SectorsPerSecond;

    /// <summary>Standard two-second pause appended to every music track, both as an
    /// audible gap before the next track and as headroom for player seek behaviour.</summary>
    public const int TrackTailPauseSectors = 2 * SectorsPerSecond;

    /// <summary>Padded on-disc length for one track, in bytes.</summary>
    public static long PaddedBytesFor(long sourcePcmBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourcePcmBytes);
        long sectors = Math.Max(
            (sourcePcmBytes + CdDiscParameters.BytesPerSector - 1) / CdDiscParameters.BytesPerSector,
            MinimumTrackSectors);
        return checked((sectors + TrackTailPauseSectors) * CdDiscParameters.BytesPerSector);
    }

    /// <summary>Builds the track list. Tracks are numbered from 1; the artwork track
    /// follows the last entry at <see cref="CdAudioTrackPlan.ArtworkStartSector"/>.</summary>
    public static CdAudioTrackPlan Plan(IReadOnlyList<(string SourcePath, long SourcePcmBytes)> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        List<CdAudioTrackPlanEntry> entries = [];
        long startSector = 0;
        long totalBytes = 0;
        for (int index = 0; index < tracks.Count; index++)
        {
            (string path, long bytes) = tracks[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            long padded = PaddedBytesFor(bytes);
            entries.Add(new CdAudioTrackPlanEntry(index + 1, path, bytes, padded, startSector));
            startSector += padded / CdDiscParameters.BytesPerSector;
            totalBytes += padded;
        }

        return new CdAudioTrackPlan(entries, totalBytes, startSector);
    }
}
