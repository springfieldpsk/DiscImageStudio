using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.ViewModels;

internal static class CdMultiTrackBurnTests
{
    internal static void Run()
    {
        TestRequestValidation();
        TestMultiTrackDaoFlow();
        TestRequestConstruction();
        TestMultiTrackCue();
        TestMultiTrackExport();
        TestProjectOutputsRoundtrip();
        Console.WriteLine("cd-multi-track-burn: passed (request rules, DAO track list, capacity boundary, export assembly, CUE indexes, project round-trip; no device access)");
    }

    private static void TestRequestValidation()
    {
        OpticalBurnRequest audio = new("device", OpticalBurnMediaKind.CdAudio, 512 * 2352L, (_, _) => { });
        audio.Validate();
        OpticalBurnRequest withTracks = audio with
        {
            LeadInAudioTracks =
            [
                new OpticalBurnAudioTrack(@"C:\cache\a.pcm", 512 * 2352L),
                new OpticalBurnAudioTrack(@"C:\cache\b.pcm", 300 * 2352L),
            ],
        };
        withTracks.Validate();
        Require(withTracks.EffectiveTotalBytes == 512 * 2352L + 512 * 2352L + 300 * 2352L,
            "the effective total sums the artwork and every lead-in track");
        Require(audio.EffectiveTotalBytes == audio.ContentLength,
            "a request without tracks keeps its own content length");

        ((Action)(() => (audio with
        {
            MediaKind = OpticalBurnMediaKind.CdData,
            LeadInAudioTracks = [new OpticalBurnAudioTrack(@"C:\cache\a.pcm", 512 * 2352L)],
        }).Validate())).Catch<ArgumentException>("lead-in tracks require CD audio mode");
        ((Action)(() => (audio with
        {
            LeadInAudioTracks = [new OpticalBurnAudioTrack(@"C:\cache\a.pcm", 1000)],
        }).Validate())).Catch<ArgumentOutOfRangeException>("track lengths must be whole audio sectors");
        ((Action)(() => (audio with
        {
            LeadInAudioTracks = [new OpticalBurnAudioTrack(" ", 512 * 2352L)],
        }).Validate())).Catch<ArgumentException>("a track without a path is rejected");
    }

    private static void TestMultiTrackDaoFlow()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-cd-multi-burn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] first = new byte[400 * 2352];
            byte[] second = new byte[300 * 2352];
            new Random(41).NextBytes(first);
            new Random(42).NextBytes(second);
            string firstPath = Path.Combine(directory, "first.pcm");
            string secondPath = Path.Combine(directory, "second.pcm");
            File.WriteAllBytes(firstPath, first);
            File.WriteAllBytes(secondPath, second);
            long artworkSectors = 512;
            OpticalBurnRequest request = new("device", OpticalBurnMediaKind.CdAudio,
                artworkSectors * 2352L,
                (output, _) => output.Write(new byte[artworkSectors * 2352]),
                LeadInAudioTracks:
                [
                    new OpticalBurnAudioTrack(firstPath, first.LongLength),
                    new OpticalBurnAudioTrack(secondPath, second.LongLength),
                ]);

            using GeneratedContentComStream content = new(request.ContentLength, request.ProduceContent, null,
                CancellationToken.None);
            content.WaitUntilPrebuffered(CancellationToken.None);
            FakeRawSession session = new() { LastLeadoutSector = 400 + 300 + (int)artworkSectors };
            using RecordingImageSession image = new(400 + 300 + artworkSectors);
            List<string> operations = [];
            WindowsImapiBurner.BurnCdDao(session, image, content, request, CancellationToken.None,
                reportOperation: operations.Add);
            Require(image.Tracks.Count == 3, "the DAO image receives every lead-in track and the artwork");
            Require(image.Tracks[0] == first.LongLength && image.Tracks[1] == second.LongLength
                && image.Tracks[2] == request.ContentLength,
                "track order is lead-in music first, artwork last");
            Require(operations.Contains("打开 2 条内圈音轨"),
                "the multi-track pass reports its own operation");

            // The lead-out boundary covers the whole multi-track program area.
            FakeRawSession tight = new() { LastLeadoutSector = 400 + 300 + (int)artworkSectors - 1 };
            using GeneratedContentComStream again = new(request.ContentLength, request.ProduceContent, null,
                CancellationToken.None);
            again.WaitUntilPrebuffered(CancellationToken.None);
            using RecordingImageSession tightImage = new(400 + 300 + artworkSectors);
            ((Action)(() => WindowsImapiBurner.BurnCdDao(tight, tightImage, again, request, CancellationToken.None)))
                .Catch<InvalidOperationException>("one sector over the DAO boundary is rejected without overburn");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestRequestConstruction()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-cd-multi-req-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string imagePath = Path.Combine(directory, "source.png");
            byte[] pixels = Enumerable.Range(0, 32 * 32).Select(i => (byte)(i % 256)).ToArray();
            System.Windows.Media.Imaging.BitmapSource bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                32, 32, 96, 96, System.Windows.Media.PixelFormats.Gray8, null, pixels, 32);
            using (FileStream image = File.Create(imagePath))
            {
                System.Windows.Media.Imaging.PngBitmapEncoder encoder = new();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                encoder.Save(image);
            }

            CdDiscParameters parameters = new(24.5, 25.5, 512);
            List<OpticalBurnAudioTrack> tracks =
            [
                new(@"C:\cache\song.pcm", 400 * 2352L),
            ];
            OpticalBurnRequest request = BurnViewModel.CreateCdBurnRequest(imagePath, parameters,
                null, "CD_IMAGE", filesAtOuterRing: false, interleave: false, ToneProfile.LegacyBinary,
                "memory-only-device", null, tracks);
            request.Validate();
            Require(request.MediaKind == OpticalBurnMediaKind.CdAudio
                && request.ContentLength == parameters.TotalBytes
                && ReferenceEquals(request.LeadInAudioTracks, tracks),
                "the lead-in list rides on an audio request without touching its artwork length");

            ((Action)(() => BurnViewModel.CreateCdBurnRequest(imagePath, parameters,
                @"C:\data", "CD_IMAGE", filesAtOuterRing: false, interleave: false, ToneProfile.LegacyBinary,
                "memory-only-device", null, tracks)))
                .Catch<ArgumentException>("data mode rejects lead-in tracks");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestMultiTrackCue()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-cd-cue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string wavePath = Path.Combine(directory, "mixed.wav");
            File.WriteAllBytes(wavePath, new byte[100]);
            string cuePath = CdWaveFile.WriteCue(wavePath,
            [
                (0, 450),
                (450, 550),
                (1000, 359_349),
            ]);
            string[] lines = File.ReadAllLines(cuePath);
            Require(lines[0] == "FILE \"mixed.wav\" WAVE", "the CUE references the wave file");
            Require(lines[1].Trim() == "TRACK 01 AUDIO" && lines[2].Trim() == "INDEX 01 00:00:00",
                "track one starts at the program start");
            Require(lines[3].Trim() == "TRACK 02 AUDIO" && lines[4].Trim() == "INDEX 01 00:06:00",
                "450 sectors is six seconds into the disc");
            Require(lines[5].Trim() == "TRACK 03 AUDIO" && lines[6].Trim() == "INDEX 01 00:13:25",
                "1000 sectors is thirteen seconds and twenty-five frames");
            string single = CdWaveFile.WriteCue(wavePath);
            string[] singleLines = File.ReadAllLines(single);
            Require(singleLines.Length == 3 && singleLines[1].Trim() == "TRACK 01 AUDIO",
                "the single-track CUE keeps its historical shape");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestMultiTrackExport()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-cd-multi-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string imagePath = Path.Combine(directory, "source.png");
            byte[] pixels = Enumerable.Range(0, 32 * 32).Select(i => (byte)(i % 256)).ToArray();
            System.Windows.Media.Imaging.BitmapSource bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                32, 32, 96, 96, System.Windows.Media.PixelFormats.Gray8, null, pixels, 32);
            using (FileStream image = File.Create(imagePath))
            {
                System.Windows.Media.Imaging.PngBitmapEncoder encoder = new();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                encoder.Save(image);
            }

            // Two "decoded" lead-in tracks: a full-length one and a sub-minimum one.
            byte[] first = new byte[400 * 2352];
            byte[] second = new byte[100 * 2352];
            new Random(7).NextBytes(first);
            new Random(8).NextBytes(second);
            string firstPcm = Path.Combine(directory, "first.pcm");
            string secondPcm = Path.Combine(directory, "second.pcm");
            File.WriteAllBytes(firstPcm, first);
            File.WriteAllBytes(secondPcm, second);
            FakeTranscoder transcoder = new(
            [
                (@"C:\music\one.mp3", firstPcm, first.LongLength),
                (@"C:\music\two.flac", secondPcm, second.LongLength),
            ]);
            CdDiscParameters parameters = new(24.5, 25.5, 512);
            string wavePath = Path.Combine(directory, "mixed.wav");
            CdImageRequest request = new(imagePath, wavePath, parameters, ToneProfile.LegacyBinary,
                Interleave: false, WriteCue: true, AudioTracks:
                [
                    @"C:\music\one.mp3", @"C:\music\two.flac",
                ],
                AudioTranscoder: transcoder);
            DiscImageResult result = CdImageGenerator.Generate(request);
            Require(File.Exists(result.OutputPath) && File.Exists(Path.ChangeExtension(wavePath, ".cue")),
                "the multi-track export writes the WAV and CUE pair");

            CdAudioTrackPlan plan = CdAudioTrackLayout.Plan(
            [
                (@"C:\music\one.mp3", first.LongLength),
                (@"C:\music\two.flac", second.LongLength),
            ]);
            long expectedLength = plan.MusicPcmBytes + parameters.TotalBytes;
            byte[] wave = File.ReadAllBytes(wavePath);
            Require(wave.Length == 44 + expectedLength, "the WAV header counts music and artwork");
            Require(wave.AsSpan(44, first.Length).SequenceEqual(first), "track one keeps its decoded bytes");
            Require(wave.AsSpan(44 + (int)plan.Tracks[0].PaddedPcmBytes, second.Length).SequenceEqual(second),
                "track two follows track one's padded length");
            Require(wave.AsSpan(44 + (int)plan.Tracks[0].PaddedPcmBytes - 4, 4).SequenceEqual(new byte[4])
                && wave.AsSpan(44 + (int)plan.MusicPcmBytes - 4, 4).SequenceEqual(new byte[4]),
                "each track ends with its two-second silence pause");
            (long offset, long length) = CdWaveFile.ReadAudioRange(new MemoryStream(wave));
            Require(offset == 44 && length == expectedLength, "the header describes valid CD-DA PCM");
            string[] cue = File.ReadAllLines(Path.ChangeExtension(wavePath, ".cue"));
            Require(cue.Count(line => line.TrimStart().StartsWith("TRACK ")) == 3
                && cue[1].Trim() == "TRACK 01 AUDIO"
                && cue[^2].Trim() == $"TRACK 03 AUDIO"
                && cue[^1].Trim() == $"INDEX 01 {CueMsf(plan.ArtworkStartSector)}",
                "the CUE ends with the artwork track after the music");

            string rawPath = Path.Combine(directory, "mixed.raw");
            ((Action)(() => CdImageGenerator.Generate(request with { OutputPath = rawPath })))
                .Catch<ArgumentException>("multi-track export rejects the legacy RAW format");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CueMsf(long sectors)
        => $"{sectors / (75 * 60):00}:{sectors / 75 % 60:00}:{sectors % 75:00}";

    private sealed class FakeTranscoder((string Source, string PcmPath, long Bytes)[] entries)
        : ICdAudioTranscoder
    {
        public CdAudioPcmFile Prepare(string sourcePath, CancellationToken cancellationToken = default)
            => entries.Single(entry => entry.Source == sourcePath) switch
            {
                var found => new CdAudioPcmFile(found.Source, found.PcmPath, found.Bytes),
            };

        public CdAudioPcmFile MaterializePadded(CdAudioPcmFile prepared, long paddedBytes,
            CancellationToken cancellationToken = default)
        {
            if (paddedBytes == prepared.PcmBytes)
            {
                return prepared;
            }

            string paddedPath = Path.ChangeExtension(prepared.PcmFilePath, $".{paddedBytes}.pcm");
            using FileStream source = File.OpenRead(prepared.PcmFilePath);
            using FileStream output = File.Create(paddedPath);
            source.CopyTo(output);
            output.Write(new byte[paddedBytes - prepared.PcmBytes]);
            return prepared with { PcmFilePath = paddedPath, PcmBytes = paddedBytes };
        }

        public void DeleteCache() { }
    }

    private static void TestProjectOutputsRoundtrip()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-cd-proj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            ProjectDocument document = new(
                ProjectJsonStore.CurrentSchemaVersion,
                "音轨工程",
                ProjectDocument.CdFamily,
                new ProjectGeometry(24.5, 56.8, 359_849),
                [],
                new ProjectOutputs(@"C:\out\mixed.wav", WriteCue: true, "", "CD_IMAGE", Interleave: true,
                    AudioTracks: [@"C:\music\a.mp3", @"C:\music\b.flac"]),
                null,
                null,
                new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero));
            string path = Path.Combine(directory, "tracks.discproj");
            ProjectJsonStore.Save(path, document);
            ProjectDocument loaded = ProjectJsonStore.Load(path);
            Require(loaded.Outputs.AudioTracks is { Count: 2 } tracks
                && tracks[0] == @"C:\music\a.mp3" && tracks[1] == @"C:\music\b.flac",
                "audio tracks round-trip in inner-ring order");
            string json = File.ReadAllText(path);
            Require(json.Contains("audioTracks"), "the field name is camelCase in the file");

            // A data-mode document with tracks is rejected, keeping the modes exclusive.
            ((Action)(() => ProjectJsonStore.Validate(document with
            {
                Outputs = loaded.Outputs with { DataDirectory = @"C:\data" },
            }))).Catch<InvalidDataException>("audio tracks and a data folder cannot coexist");
            // Non-CD projects never carry the field.
            ((Action)(() => ProjectJsonStore.Validate(document with
            {
                DiscFamily = ProjectDocument.DvdFamily,
                Geometry = new ProjectGeometry(24.0, 58.0, 2_295_104),
                Outputs = loaded.Outputs with { AudioTracks = [@"C:\music\a.mp3"] },
            }))).Catch<InvalidDataException>("audio tracks are CD-only");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeRawSession : ICdRawSession
    {
        internal int LastLeadoutSector { get; init; }
        public bool MediaPhysicallyBlank => true;
        public long LastPossibleStartOfLeadout => LastLeadoutSector;
        public IReadOnlyList<int> SupportedSectorTypes => [CdRawSectorType.SubcodeIsCooked];
        public int CurrentWriteSpeed => 1200;
        public bool CurrentRotationTypeIsPureCav => false;
        public void PrepareMedia() { }
        public void SetRequestedSectorType(int sectorType) { }
        public void SetWriteSpeed(int sectorsPerSecond, bool rotationTypeIsPureCav) { }
        public void WriteMedia(IStream content) { }
        public void CancelWrite() { }
        public void ReleaseMedia() { }
    }

    private sealed class RecordingImageSession(long startOfLeadout) : ICdRawImageSession
    {
        internal List<long> Tracks { get; } = [];

        public long StartOfLeadout => startOfLeadout;

        public IStream CreateImage(IStream content, int sectorType)
            => CreateImage([content], sectorType);

        public IStream CreateImage(IReadOnlyList<IStream> tracks, int sectorType)
        {
            foreach (IStream track in tracks)
            {
                track.Stat(out STATSTG stat, 1);
                Tracks.Add(stat.cbSize);
            }

            return tracks[^1];
        }

        public void Dispose() { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD multi-track burn: " + message);
    }

    private static void Catch<TException>(this Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("CD multi-track burn: " + message);
    }
}
