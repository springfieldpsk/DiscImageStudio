using System.IO;
using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

/// <summary>A CD audio or data-image export, shared by graphical and command-line hosts.</summary>
public sealed record CdImageRequest(
    string SourceImage,
    string OutputPath,
    CdDiscParameters Parameters,
    ToneProfile Tone,
    bool Interleave = true,
    bool WriteCue = true,
    string? DataDirectory = null,
    string VolumeLabel = "CD_IMAGE",
    bool FilesAtOuterRing = false,
    /// <summary>Playable CD-DA lead-in audio files (WAV/MP3/FLAC), inner-ring order.
    /// Audio mode only; the artwork track is appended after them.</summary>
    IReadOnlyList<string>? AudioTracks = null,
    /// <summary>Decoder used for AudioTracks; defaults to the shared factory (cache-backed).</summary>
    ICdAudioTranscoder? AudioTranscoder = null);

public static class CdImageGenerator
{
    /// <summary>Writes the image and recovery sidecars, preserving exceptions for the host.</summary>
    public static DiscImageResult Generate(CdImageRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        cancellationToken.ThrowIfCancellationRequested();
        request.Tone.Validate();
        string? dataDirectory = request.DataDirectory?.Trim();
        if (!string.IsNullOrEmpty(dataDirectory))
        {
            CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(dataDirectory, request.Parameters,
                request.VolumeLabel, request.FilesAtOuterRing);
            CdGenerationSummary dataSummary = CdDataImageGenerator.Generate(request.SourceImage,
                request.OutputPath, request.Parameters, plan,
                value => progress?.Report(new DiscJobProgress(CdDiscModule.ModuleId, "generate",
                    $"CD data progress {value.Fraction:P1}", value.Fraction, value.CompletedBytes,
                    value.TotalBytes, value.Elapsed)), cancellationToken, request.Tone);
            return new DiscImageResult(dataSummary.OutputTrack,
                $"CD data image complete: {plan.FileCount} files, {plan.TotalSectors} sectors, "
                + $"files={(request.FilesAtOuterRing ? "outer" : "inner")}, output={dataSummary.OutputTrack}");
        }

        if (request.AudioTracks is { Count: > 0 } audioTracks)
        {
            return GenerateMultiTrackAudio(request, audioTracks, progress, cancellationToken);
        }

        CdGenerationSummary summary = CdTrackGenerator.Generate(request.SourceImage, request.OutputPath,
            request.Parameters, request.Interleave, request.WriteCue,
            value => progress?.Report(new DiscJobProgress(CdDiscModule.ModuleId, "generate",
                $"CD progress {value.Fraction:P1}", value.Fraction, value.CompletedBytes,
                value.TotalBytes, value.Elapsed)), cancellationToken, request.Tone);
        string message =
            $"CD track complete: {summary.Sectors} sectors, {summary.BytesWritten} bytes, "
            + $"interleave={summary.Interleaved}, elapsed={summary.Elapsed.TotalSeconds:F1}s, "
            + $"output={summary.OutputTrack}"
            + (summary.CueSheetPath is null ? string.Empty : $", cue={summary.CueSheetPath}");
        return new DiscImageResult(summary.OutputTrack, message);
    }

    /// <summary>Audio-mode export with playable lead-in tracks: one WAV holding every
    /// padded music track followed by the artwork track, plus a matching multi-track CUE.</summary>
    private static DiscImageResult GenerateMultiTrackAudio(
        CdImageRequest request,
        IReadOnlyList<string> audioTracks,
        IProgress<DiscJobProgress>? progress,
        CancellationToken cancellationToken)
    {
        string outputPath = Path.GetFullPath(request.OutputPath);
        if (!Path.GetExtension(outputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "多音轨导出仅支持 WAV（含 CUE）。", nameof(request));
        }

        ICdAudioTranscoder transcoder = request.AudioTranscoder ?? CdAudioTranscoderFactory.Create();
        List<CdAudioPcmFile> prepared = [];
        foreach (string path in audioTracks)
        {
            prepared.Add(transcoder.Prepare(path, cancellationToken));
        }

        CdAudioTrackPlan plan = CdAudioTrackLayout.Plan(
            prepared.Select(pcm => (pcm.SourcePath, pcm.PcmBytes)).ToArray());
        List<(CdAudioPcmFile File, CdAudioTrackPlanEntry Entry)> paddedTracks = [];
        for (int index = 0; index < plan.Tracks.Count; index++)
        {
            paddedTracks.Add((transcoder.MaterializePadded(prepared[index],
                plan.Tracks[index].PaddedPcmBytes, cancellationToken), plan.Tracks[index]));
        }

        request.Parameters.Validate();
        long totalBytes = checked(plan.MusicPcmBytes + request.Parameters.TotalBytes);
        CdWaveFile.ValidateLength(totalBytes);
        EnsureParentDirectory(outputPath);
        using FileStream file = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        CdWaveFile.WriteHeader(file, totalBytes);
        byte[] buffer = new byte[1024 * 1024];
        long musicBytes = 0;
        foreach ((CdAudioPcmFile pcm, _) in paddedTracks)
        {
            using FileStream source = new(pcm.PcmFilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 1024 * 1024, FileOptions.SequentialScan);
            long remaining = pcm.PcmBytes;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count <= 0) throw new IOException("缓存的音轨 PCM 文件意外结束。");
                file.Write(buffer, 0, count);
                remaining -= count;
                musicBytes += count;
                progress?.Report(new DiscJobProgress(CdDiscModule.ModuleId, "generate",
                    $"CD audio progress {musicBytes / (double)totalBytes:P1}",
                    musicBytes / (double)totalBytes, musicBytes, totalBytes, TimeSpan.Zero));
            }
        }

        CdGenerationSummary artwork = CdTrackGenerator.GenerateToStream(
            request.SourceImage, file, request.Parameters, request.Interleave,
            progress: null, cancellationToken, outputPath,
            CdAudioByteOrder.LittleEndian, request.Tone);
        file.Dispose();
        CdTrackMetadata.Save(outputPath, new CdTrackMetadata(
            Path.GetFullPath(request.SourceImage),
            outputPath,
            request.Parameters.InnerRadiusMm,
            request.Parameters.OuterRadiusMm,
            request.Parameters.Sectors,
            request.Parameters.LinearVelocityMmPerSecond,
            request.Parameters.StartAngleRadians * 180.0 / Math.PI,
            request.Parameters.ImageOuterRadiusMm,
            request.Interleave,
            request.Tone.DitherStrength,
            request.Tone.Levels,
            request.Tone.Brightness,
            request.Tone.SrgbLinearization,
            request.Parameters.PitchLinear,
            request.Parameters.PitchQuadratic,
            request.Parameters.PitchCubic,
            request.Parameters.UsableSectors,
            request.Parameters.SourceImageOuterRadiusMm));

        string? cuePath = null;
        if (request.WriteCue)
        {
            List<(long StartSector, long Sectors)> cueTracks = paddedTracks
                .Select(track => (track.Entry.StartSector, track.Entry.Sectors))
                .ToList();
            cueTracks.Add((plan.ArtworkStartSector, request.Parameters.OutputSectors));
            cuePath = CdWaveFile.WriteCue(outputPath, cueTracks);
        }

        return new DiscImageResult(outputPath,
            $"CD mixed-track complete: {paddedTracks.Count} lead-in tracks ({plan.MusicSectorCount} sectors) "
            + $"+ artwork ({request.Parameters.OutputSectors} sectors), "
            + $"total {totalBytes} bytes, output={outputPath}"
            + (cuePath is null ? string.Empty : $", cue={cuePath}"));
    }

    private static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
