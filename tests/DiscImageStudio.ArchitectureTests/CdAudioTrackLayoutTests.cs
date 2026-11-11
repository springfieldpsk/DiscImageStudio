using DiscImageStudio.Cd;

internal static class CdAudioTrackLayoutTests
{
    internal static void Run()
    {
        // Measured against the native IMAPI2.MsftRawCDImageCreator on this code path:
        // 300 sectors is the accepted minimum single track, and tracks are placed back
        // to back with no inserted pregap (see CdMultiTrackImageTests).
        Require(CdAudioTrackLayout.MinimumTrackSectors == 300, "IMAPI minimum track is four seconds");
        Require(CdAudioTrackLayout.TrackTailPauseSectors == 150, "the tail pause is the standard two seconds");

        Require(CdAudioTrackLayout.PaddedBytesFor(0) == (300 + 150) * 2352L,
            "an empty source pads to the IMAPI minimum plus the tail pause");
        Require(CdAudioTrackLayout.PaddedBytesFor(2352) == (300 + 150) * 2352L,
            "a one-sector source still pads to the minimum");
        Require(CdAudioTrackLayout.PaddedBytesFor(299 * 2352L) == (300 + 150) * 2352L,
            "a sub-minimum source rounds up to the minimum");
        Require(CdAudioTrackLayout.PaddedBytesFor(400 * 2352L) == (400 + 150) * 2352L,
            "a full-length source keeps its sectors and gains the tail pause");
        Require(CdAudioTrackLayout.PaddedBytesFor(400 * 2352L + 1) == (401 + 150) * 2352L,
            "a partial tail sector rounds up before the pause is added");
        Require(CdAudioTrackLayout.PaddedBytesFor(123 * 2352L + 4) % 2352 == 0,
            "padded lengths are always whole sectors");

        CdAudioTrackPlan plan = CdAudioTrackLayout.Plan(
        [
            ("first.wav", 400 * 2352L),
            ("second.mp3", 299 * 2352L),
            ("third.flac", 6 * 2352L),
        ]);
        Require(plan.Tracks.Count == 3, "every source becomes one track");
        Require(plan.Tracks[0].TrackNumber == 1 && plan.Tracks[2].TrackNumber == 3,
            "tracks are numbered from one in list order");
        Require(plan.Tracks[0].Sectors == 550, "track one spans audio plus tail pause");
        Require(plan.Tracks[1].Sectors == 450, "the sub-minimum track pads to 300+150 sectors");
        Require(plan.Tracks[2].Sectors == 450, "a six-sector source pads to the minimum too");
        Require(plan.Tracks[0].StartSector == 0, "the first track starts at the program start");
        Require(plan.Tracks[1].StartSector == 550 && plan.Tracks[2].StartSector == 1000,
            "tracks follow back to back with no inserted pregap");
        Require(plan.MusicSectorCount == 1450 && plan.ArtworkStartSector == 1450,
            "the artwork track starts after the last music track");
        Require(plan.MusicPcmBytes == 1450 * 2352L, "the byte total matches the sector total");
        Require(plan.Tracks[1].Duration.TotalSeconds > 6.0 - 1e-9 && plan.Tracks[1].Duration.TotalSeconds < 6.1,
            "the duration includes the pause and uses 75 sectors per second");

        Console.WriteLine("cd-audio-track-layout: passed (IMAPI minimum, tail pause, sector rounding, back-to-back starts)");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD audio track layout: " + message);
    }
}
