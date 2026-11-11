using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace DiscImageStudio.Cd;

internal static class CdWaveFile
{
    internal const int HeaderBytes = 44;

    internal static void ValidateLength(long audioBytes)
    {
        if (audioBytes <= 0 || audioBytes > uint.MaxValue - 36L)
        {
            throw new ArgumentOutOfRangeException(nameof(audioBytes), "Audio exceeds the RIFF/WAV size limit.");
        }
    }

    internal static void WriteHeader(Stream output, long audioBytes)
    {
        ValidateLength(audioBytes);
        Span<byte> header = stackalloc byte[HeaderBytes];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(audioBytes + 36)));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 176400);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)audioBytes));
        output.Write(header);
    }

    internal static string WriteCue(string wavePath)
        => WriteCue(wavePath, [(0, 0)]);

    /// <summary>Multi-track CUE: one AUDIO track per entry, INDEX 01 at each start sector
    /// expressed in MSF frames at 75 per second. The single-track form above keeps its
    /// historical one-track output.</summary>
    internal static string WriteCue(string wavePath, IReadOnlyList<(long StartSector, long Sectors)> tracks)
    {
        ArgumentException.ThrowIfNullOrEmpty(wavePath);
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            throw new ArgumentException("At least one track is required.", nameof(tracks));
        }

        StringBuilder cue = new();
        cue.Append($"FILE \"{Path.GetFileName(wavePath)}\" WAVE\r\n");
        for (int index = 0; index < tracks.Count; index++)
        {
            long start = Math.Max(0, tracks[index].StartSector);
            cue.Append($"  TRACK {index + 1:00} AUDIO\r\n");
            cue.Append($"    INDEX 01 {Msf(start)}\r\n");
        }

        string cuePath = Path.ChangeExtension(wavePath, ".cue");
        File.WriteAllText(cuePath, cue.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return cuePath;
    }

    private static string Msf(long sectors)
    {
        long frames = sectors % 75;
        long seconds = sectors / 75 % 60;
        long minutes = sectors / (75 * 60);
        return $"{minutes:00}:{seconds:00}:{frames:00}";
    }

    internal static (long Offset, long Length) ReadAudioRange(Stream input)
    {
        using BinaryReader reader = new(input, Encoding.ASCII, leaveOpen: true);
        if (input.Length < 12 || reader.ReadUInt32() != 0x46464952)
        {
            throw new InvalidDataException("Expected a RIFF/WAV audio file.");
        }

        long riffEnd = reader.ReadUInt32() + 8L;
        if (reader.ReadUInt32() != 0x45564157 || riffEnd < 12 || riffEnd > input.Length)
        {
            throw new InvalidDataException("The WAV header or declared length is invalid.");
        }

        bool hasFormat = false;
        (long Offset, long Length)? audio = null;
        while (input.Position < riffEnd)
        {
            if (riffEnd - input.Position < 8)
            {
                throw new InvalidDataException("Truncated WAV chunk header.");
            }

            uint chunk = reader.ReadUInt32();
            uint length = reader.ReadUInt32();
            long start = input.Position;
            long next = start + length + (length & 1);
            if (next > riffEnd)
            {
                throw new InvalidDataException("Truncated WAV chunk data.");
            }

            if (chunk == 0x20746D66) // fmt
            {
                if (hasFormat || length < 16
                    || reader.ReadUInt16() != 1
                    || reader.ReadUInt16() != 2
                    || reader.ReadUInt32() != 44100
                    || reader.ReadUInt32() != 176400
                    || reader.ReadUInt16() != 4
                    || reader.ReadUInt16() != 16)
                {
                    throw new InvalidDataException("CD WAV audio must be PCM, 44100 Hz, 16-bit stereo.");
                }

                hasFormat = true;
            }
            else if (chunk == 0x61746164) // data
            {
                if (audio is not null || length == 0 || length % 4 != 0)
                {
                    throw new InvalidDataException("WAV must contain one data chunk with complete stereo samples.");
                }

                audio = (start, length);
            }

            input.Position = next;
        }

        if (!hasFormat || audio is null)
        {
            throw new InvalidDataException("WAV is missing its PCM format or audio data chunk.");
        }

        return audio.Value;
    }
}
