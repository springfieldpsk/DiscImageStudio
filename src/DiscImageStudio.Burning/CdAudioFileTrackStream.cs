using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

/// <summary>
/// A playable lead-in audio track backed by a headerless PCM file. Forward-only,
/// like the generated content stream: IMAPI pulls the DAO image strictly in order.
/// Reads past the file end return silence so a truncated file cannot end a burn
/// inside a declared sector.
/// </summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class CdAudioFileTrackStream : IStream, IDisposable
{
    private readonly long _length;
    private readonly FileStream _file;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private long _position;
    private bool _hasRead;
    private bool _disposed;

    internal CdAudioFileTrackStream(string pcmFilePath, long pcmBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pcmFilePath);
        if (pcmBytes <= 0 || pcmBytes % OpticalBurnMedia.CdAudioSectorBytes != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pcmBytes),
                "Lead-in audio track length must be a positive multiple of "
                    + $"{OpticalBurnMedia.CdAudioSectorBytes} bytes.");
        }

        _file = new FileStream(
            pcmFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        long available = _file.Length - _file.Position;
        if (available < pcmBytes)
        {
            long missing = pcmBytes - available;
            if (missing % OpticalBurnMedia.CdAudioSectorBytes != 0)
            {
                _file.Dispose();
                throw new IOException(
                    $"The lead-in audio file is short by {missing} bytes, which is not a whole sector.");
            }
        }

        _length = pcmBytes;
    }

    internal long Length => _length;

    internal long Position => _position;

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pv);
        if ((uint)cb > pv.Length) throw new ArgumentOutOfRangeException(nameof(cb));
        int copied = 0;
        while (copied < cb && _position < _length)
        {
            int take = (int)Math.Min(cb - copied, _length - _position);
            int count = _file.Read(_buffer, 0, (int)Math.Min(take, _buffer.Length));
            Buffer.BlockCopy(_buffer, 0, pv, copied, count);
            copied += count;
            _position += count;
            if (count == 0)
            {
                // The file ended inside the declared length: complete the request
                // with silence rather than failing a physical write mid-sector.
                Array.Clear(pv, copied, cb - copied);
                _position += cb - copied;
                copied = cb;
                break;
            }
        }

        _hasRead |= copied > 0;
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, copied);
    }

    public void Seek(long offset, int origin, IntPtr newPosition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long target = origin switch
        {
            0 => offset,
            1 => checked(_position + offset),
            2 => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0 || target > _length)
            throw new COMException("Invalid lead-in audio track position.", unchecked((int)0x80030001));
        if (!_hasRead && (target == 0 || target == _length))
        {
            _file.Position = Math.Min(target, _file.Length);
            _position = target;
        }
        else if (target < _position)
        {
            throw new COMException("The lead-in audio track is forward-only.", unchecked((int)0x80030001));
        }
        else if (target > _position)
        {
            while (_position < target)
            {
                Read(_buffer, (int)Math.Min(_buffer.Length, target - _position), IntPtr.Zero);
            }
        }

        if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, _position);
    }

    public void Stat(out STATSTG stat, int flags) => stat = new() { type = 2, cbSize = _length, grfMode = 0 };
    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();
    public void SetSize(long size) => throw new NotSupportedException();
    public void CopyTo(IStream target, long count, IntPtr read, IntPtr written) => throw new NotSupportedException();
    public void Commit(int flags) { }
    public void Revert() => throw new NotSupportedException();
    public void LockRegion(long offset, long count, int type) => throw new NotSupportedException();
    public void UnlockRegion(long offset, long count, int type) => throw new NotSupportedException();
    public void Clone(out IStream stream) => throw new NotSupportedException();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _file.Dispose();
    }
}
