using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscImageStudio.Burning;

/// <summary>Locks the sector accounting the multi-track DAO pipeline relies on, against
/// the native <c>IMAPI2.MsftRawCDImageCreator</c>. No recorder or recording interface is
/// created; image construction alone is observed.</summary>
internal static class CdMultiTrackImageTests
{
    private const int RawBytes = 2448;

    internal static void Run()
    {
        using DynamicCdRawImageSession single = new();
        IStream one = single.CreateImage(new MemoryContent(new byte[400 * 2352]), CdRawSectorType.SubcodeIsCooked);
        Require(single.StartOfLeadout == 400, "a single track defines the lead-out at its own length");
        Require(Length(one) > (CdDataRawImageStream.ProgramStartSector + 400) * (long)RawBytes,
            "the native DAO image still contains lead-in and lead-out around the program area");

        using DynamicCdRawImageSession duo = new();
        IStream two = duo.CreateImage(
        [
            new MemoryContent(new byte[400 * 2352]),
            new MemoryContent(new byte[700 * 2352]),
        ], CdRawSectorType.SubcodeIsCooked);
        Require(duo.StartOfLeadout == 1100,
            "two tracks are placed back to back: the lead-out equals the sector sum");

        using DynamicCdRawImageSession triple = new();
        IStream three = triple.CreateImage(
        [
            new MemoryContent(new byte[400 * 2352]),
            new MemoryContent(new byte[700 * 2352]),
            new MemoryContent(new byte[300 * 2352]),
        ], CdRawSectorType.SubcodeIsCooked);
        Require(triple.StartOfLeadout == 1400, "three tracks accumulate the same way");
        Require(Length(two) == Length(one) + 700L * RawBytes && Length(three) == Length(one) + 1000L * RawBytes,
            "the complete native image grows by exactly the added track sectors");

        // A second short track is accepted because the session total clears four seconds.
        using DynamicCdRawImageSession mixed = new();
        IStream _ = mixed.CreateImage(
        [
            new MemoryContent(new byte[400 * 2352]),
            new MemoryContent(new byte[100 * 2352]),
        ], CdRawSectorType.SubcodeIsCooked);
        Require(mixed.StartOfLeadout == 500, "a short follow-up track is accepted once the session clears four seconds");

        using DynamicCdRawImageSession tooShort = new();
        ((Action)(() => tooShort.CreateImage(new MemoryContent(new byte[299 * 2352]), CdRawSectorType.SubcodeIsCooked)))
            .Catch<ArgumentException>("a track under four seconds is rejected by native IMAPI");
        using DynamicCdRawImageSession empty = new();
        ((Action)(() => empty.CreateImage(Array.Empty<IStream>(), CdRawSectorType.SubcodeIsCooked)))
            .Catch<ArgumentException>("an empty track list is rejected by the wrapper");

        Console.WriteLine("cd-multi-track-image: passed (native lead-out = sector sum, no inter-track pregap, four-second minimum)");
    }

    private static long Length(IStream stream)
    {
        stream.Stat(out STATSTG stat, 1);
        return stat.cbSize;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CD multi-track image: " + message);
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
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "CD multi-track image: " + message + $" (got {exception.GetType().Name})");
        }

        throw new InvalidOperationException("CD multi-track image: " + message + " (no exception)");
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class MemoryContent(byte[] bytes) : IStream
    {
        private long _position;
        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            ArgumentNullException.ThrowIfNull(pv);
            int count = (int)Math.Min(cb, bytes.Length - _position);
            bytes.AsSpan((int)_position, count).CopyTo(pv);
            _position += count;
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, count);
        }
        public void Seek(long offset, int origin, IntPtr result)
        {
            long position = origin switch
            {
                0 => offset,
                1 => _position + offset,
                2 => bytes.Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (position < 0 || position > bytes.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            _position = position;
            if (result != IntPtr.Zero) Marshal.WriteInt64(result, position);
        }
        public void Stat(out STATSTG stat, int flags) => stat = new() { type = 2, cbSize = bytes.Length };
        public void Write(byte[] a, int b, IntPtr c) => throw new NotSupportedException();
        public void SetSize(long a) => throw new NotSupportedException();
        public void CopyTo(IStream a, long b, IntPtr c, IntPtr d) => throw new NotSupportedException();
        public void Commit(int a) { }
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long a, long b, int c) => throw new NotSupportedException();
        public void UnlockRegion(long a, long b, int c) => throw new NotSupportedException();
        public void Clone(out IStream stream) => throw new NotSupportedException();
    }
}
