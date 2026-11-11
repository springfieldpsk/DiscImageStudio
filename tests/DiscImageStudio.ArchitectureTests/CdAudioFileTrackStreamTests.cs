using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.Burning;

internal static class CdAudioFileTrackStreamTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-cd-audio-stream-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string pcmPath = Path.Combine(directory, "track.pcm");
            byte[] pcm = new byte[3 * 2352];
            new Random(20261111).NextBytes(pcm);
            File.WriteAllBytes(pcmPath, pcm);
            using (CdAudioFileTrackStream stream = new(pcmPath, pcm.Length))
            {
                stream.Stat(out STATSTG stat, 1);
                Require(stat.cbSize == pcm.Length, "Stat reports the declared track length");
                byte[] read = new byte[pcm.Length];
                ReadExactly(stream, read, 0, read.Length);
                Require(read.AsSpan().SequenceEqual(pcm), "the PCM file passes through unchanged");
                IntPtr count = Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    stream.Read(read, 1, count);
                    Require(Marshal.ReadInt32(count) == 0, "a exhausted track reads zero bytes");
                }
                finally
                {
                    Marshal.FreeCoTaskMem(count);
                }
            }

            // A file one whole sector short: the stream completes with silence rather
            // than failing a physical write mid-track.
            string shortPath = Path.Combine(directory, "short.pcm");
            File.WriteAllBytes(shortPath, pcm[..(2 * 2352)]);
            using (CdAudioFileTrackStream completing = new(shortPath, pcm.Length))
            {
                byte[] read = new byte[pcm.Length];
                ReadExactly(completing, read, 0, 2 * 2352);
                Require(read.AsSpan(0, 2 * 2352).SequenceEqual(pcm.AsSpan(0, 2 * 2352)),
                    "the existing bytes still come from the file");
                ReadExactly(completing, read, 2 * 2352, 2352);
                Require(read.AsSpan(2 * 2352).SequenceEqual(new byte[2352]),
                    "a missing whole sector is completed with silence");
            }

            string truncatedPath = Path.Combine(directory, "truncated.pcm");
            File.WriteAllBytes(truncatedPath, pcm[..(2 * 2352 + 7)]);
            ((Action)(() => new CdAudioFileTrackStream(truncatedPath, pcm.Length)))
                .Catch<IOException>("a short-by-non-sector file is rejected at construction");

            using (CdAudioFileTrackStream seekable = new(pcmPath, pcm.Length))
            {
                seekable.Seek(0, 0, IntPtr.Zero);
                byte[] head = new byte[2352];
                ReadExactly(seekable, head, 0, head.Length);
                seekable.Seek(pcm.Length, 0, IntPtr.Zero);
                ((Action)(() => seekable.Seek(-2352, 1, IntPtr.Zero)))
                    .Catch<COMException>("backward relative seek after reading fails");
                ((Action)(() => seekable.Seek(1, 1, IntPtr.Zero)))
                    .Catch<COMException>("seek past the declared length fails");
                ((Action)(() => seekable.Seek(-1, 0, IntPtr.Zero)))
                    .Catch<COMException>("absolute seek before zero fails");
            }

            foreach (long invalid in new long[] { 0, -2352, 2351, 2353 })
            {
                ((Action)(() => new CdAudioFileTrackStream(pcmPath, invalid)))
                    .Catch<ArgumentOutOfRangeException>($"track length {invalid} is rejected");
            }

            ((Action)(() => new CdAudioFileTrackStream(Path.Combine(directory, "missing.pcm"), 2352)))
                .Catch<FileNotFoundException>("a missing PCM file is rejected");

            Console.WriteLine("cd-audio-file-track-stream: passed (passthrough, silence completion, forward-only seek, length rules)");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ReadExactly(CdAudioFileTrackStream stream, byte[] buffer, int offset, int count)
    {
        IntPtr read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            byte[] chunk = new byte[64 * 1024];
            while (count > 0)
            {
                int take = (int)Math.Min(chunk.Length, count);
                Marshal.WriteInt32(read, -1);
                stream.Read(chunk, take, read);
                int taken = Marshal.ReadInt32(read);
                Require(taken > 0 && taken <= take, "the track stream returns a bounded read count");
                Buffer.BlockCopy(chunk, 0, buffer, offset, taken);
                offset += taken;
                count -= taken;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(read);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD audio track stream: " + message);
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

        throw new InvalidOperationException("CD audio track stream: " + message);
    }
}
