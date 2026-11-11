using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DiscImageStudio.Cd;

/// <summary>A materialized CD-DA PCM payload for one source file: headerless
/// 44.1 kHz, 16-bit, stereo, little-endian samples, ready for IMAPI AddTrack.</summary>
public sealed record CdAudioPcmFile(string SourcePath, string PcmFilePath, long PcmBytes);

public interface ICdAudioTranscoder
{
    /// <summary>Decodes a supported audio file to exact CD-DA PCM in the local cache.
    /// The result is cached per source identity, so repeated preparation is cheap.</summary>
    CdAudioPcmFile Prepare(string sourcePath, CancellationToken cancellationToken = default);

    /// <summary>Extends prepared PCM to the exact on-disc track length by padding with
    /// silence. Returns the prepared file unchanged when the lengths already match.</summary>
    CdAudioPcmFile MaterializePadded(CdAudioPcmFile prepared, long paddedBytes,
        CancellationToken cancellationToken = default);

    /// <summary>Removes every cache entry this transcoder created.</summary>
    void DeleteCache();
}

public static class CdAudioTranscoderFactory
{
    private static readonly string[] SourceExtensions = [".wav", ".mp3", ".flac"];

    public static bool IsSupportedSource(string path)
        => SourceExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static IReadOnlyList<string> SupportedExtensions => SourceExtensions;

    public static ICdAudioTranscoder Create() => new CdAudioTranscoder();
}

/// <summary>
/// Produces playable CD-DA lead-in PCM. Compliant WAV files (44.1 kHz, 16-bit,
/// stereo PCM) are copied directly; everything else — MP3, FLAC, and other WAV
/// flavours — is decoded through the Windows Media Foundation source reader,
/// which inserts the audio resampler so the output is always CD-DA PCM.
/// </summary>
internal sealed class CdAudioTranscoder : ICdAudioTranscoder
{
    internal static readonly string CacheDirectory = Path.Combine(
        Path.GetTempPath(), "DiscImageStudio", "cd-audio");

    public CdAudioPcmFile Prepare(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("音频文件不存在。", fullPath);
        }

        (string key, DateTime stamp, long length) = CacheKey(fullPath);
        string cachePath = Path.Combine(CacheDirectory, key + ".pcm");
        string metaPath = Path.Combine(CacheDirectory, key + ".meta");
        if (TryReadCache(metaPath, cachePath, out long cachedBytes))
        {
            return new CdAudioPcmFile(fullPath, cachePath, cachedBytes);
        }

        Directory.CreateDirectory(CacheDirectory);
        string temporary = Path.Combine(CacheDirectory, key + $".{Guid.NewGuid():N}.tmp");
        try
        {
            long bytes;
            if (Path.GetExtension(fullPath).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                && TryCopyCompliantWav(fullPath, temporary))
            {
                bytes = new FileInfo(temporary).Length;
            }
            else
            {
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException(
                        "仅支持 44.1 kHz/16-bit/立体声 PCM WAV；MP3/FLAC 转码需要 Windows Media Foundation。");
                }

                bytes = MediaFoundationAudio.Decode(fullPath, temporary, cancellationToken);
            }

            File.Move(temporary, cachePath, overwrite: true);
            File.WriteAllText(metaPath, $"{bytes}\n{stamp.Ticks}\n{length}\n", Encoding.ASCII);
            return new CdAudioPcmFile(fullPath, cachePath, bytes);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public CdAudioPcmFile MaterializePadded(CdAudioPcmFile prepared, long paddedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (paddedBytes < prepared.PcmBytes || paddedBytes % CdDiscParameters.BytesPerSector != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(paddedBytes),
                "The padded track length must be a whole number of CD sectors and at least the decoded audio.");
        }

        if (paddedBytes == prepared.PcmBytes)
        {
            return prepared;
        }

        (string key, _, _) = CacheKey(prepared.SourcePath);
        string cachePath = Path.Combine(CacheDirectory, $"{key}-p{paddedBytes}.pcm");
        if (File.Exists(cachePath) && new FileInfo(cachePath).Length == paddedBytes)
        {
            return prepared with { PcmFilePath = cachePath, PcmBytes = paddedBytes };
        }

        Directory.CreateDirectory(CacheDirectory);
        string temporary = Path.Combine(CacheDirectory, $"{key}-p{paddedBytes}.{Guid.NewGuid():N}.tmp");
        try
        {
            // The file streams must close before the cache move below.
            {
                using FileStream source = new(prepared.PcmFilePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, bufferSize: 1024 * 1024, FileOptions.SequentialScan);
                using FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, bufferSize: 1024 * 1024, FileOptions.SequentialScan);
                byte[] buffer = new byte[1024 * 1024];
                long remaining = prepared.PcmBytes;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (count <= 0)
                    {
                        throw new IOException("缓存的音轨 PCM 文件意外结束。");
                    }

                    output.Write(buffer, 0, count);
                    remaining -= count;
                }

                long silence = paddedBytes - prepared.PcmBytes;
                Array.Clear(buffer);
                while (silence > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(buffer.Length, silence);
                    output.Write(buffer, 0, count);
                    silence -= count;
                }
            }

            File.Move(temporary, cachePath, overwrite: true);
            return prepared with { PcmFilePath = cachePath, PcmBytes = paddedBytes };
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public void DeleteCache()
    {
        try
        {
            if (Directory.Exists(CacheDirectory))
            {
                Directory.Delete(CacheDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file in use can survive; the cache is disposable by design.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryCopyCompliantWav(string sourcePath, string destinationPath)
    {
        try
        {
            using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 1024 * 1024, FileOptions.SequentialScan);
            (long offset, long length) = CdWaveFile.ReadAudioRange(source);
            using FileStream output = new(destinationPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 1024 * 1024, FileOptions.SequentialScan);
            source.Position = offset;
            byte[] buffer = new byte[1024 * 1024];
            long remaining = length;
            while (remaining > 0)
            {
                int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count <= 0)
                {
                    throw new EndOfStreamException("WAV 音频数据比声明的长度短。");
                }

                output.Write(buffer, 0, count);
                remaining -= count;
            }

            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryReadCache(string metaPath, string cachePath, out long pcmBytes)
    {
        pcmBytes = 0;
        try
        {
            if (!File.Exists(metaPath) || !File.Exists(cachePath))
            {
                return false;
            }

            string[] lines = File.ReadAllLines(metaPath);
            if (lines.Length < 1 || !long.TryParse(lines[0], out long bytes))
            {
                return false;
            }

            if (new FileInfo(cachePath).Length != bytes)
            {
                return false;
            }

            pcmBytes = bytes;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (string Key, DateTime Stamp, long Length) CacheKey(string fullPath)
    {
        FileInfo info = new(fullPath);
        string identity = $"{fullPath.ToUpperInvariant()}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        // Only a cache key: speed matters more than cryptographic strength here.
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return (key, info.LastWriteTimeUtc, info.Length);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Media Foundation source-reader decoding to 44.1 kHz, 16-bit, stereo PCM.
/// Interface declarations follow the Windows SDK mfobjects.h/mfreadwrite.h
/// vtable order; every GUID below was taken from mfapi.h, not hand-written.
/// </summary>
internal static class MediaFoundationAudio
{
    private const uint MfVersion = 0x0002_0070; // MF_SDK_VERSION << 16 | MF_API_VERSION
    private const int MfStartupLite = 0x1;

    private const uint FirstAudioStream = 0xFFFFFFFD; // MF_SOURCE_READER_FIRST_AUDIO_STREAM
    private const uint EndOfStreamFlag = 0x2;         // MF_SOURCE_READERF_ENDOFSTREAM

    private static readonly Guid MajorTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid MtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MtAudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MtAudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MtAudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

    private static int _startupCount;

    internal static long Decode(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        Debug.Assert(OperatingSystem.IsWindows());
        Startup();
        IMFSourceReader? reader = null;
        string stage = "打开源";
        try
        {
            int hr = MFCreateSourceReaderFromURL(sourcePath, IntPtr.Zero, out reader);
            if (hr < 0)
            {
                throw CreateDecodeException(hr, sourcePath,
                    $"无法打开音频文件（{stage}；可能缺少解码器或格式不受支持）。");
            }

            if (reader is null)
            {
                throw CreateDecodeException(hr, sourcePath, $"Media Foundation 未返回源读取器（{stage}）。");
            }

            stage = "选择音频流";
            reader.SetStreamSelection(FirstAudioStream, true);
            IMFMediaType? output = null;
            try
            {
                stage = "设置 PCM 输出格式";
                output = CreatePcmMediaType();
                reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, output);
            }
            finally
            {
                if (output is not null && Marshal.IsComObject(output))
                {
                    Marshal.FinalReleaseComObject(output);
                }
            }

            using FileStream file = new(destinationPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 1024 * 1024, FileOptions.SequentialScan);
            byte[] buffer = new byte[1024 * 1024];
            long total = 0;
            while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IMFMediaBuffer? mediaBuffer = null;
                    try
                    {
                        stage = "读取样本";
                        reader.ReadSample(FirstAudioStream, 0, out _, out uint flags, out _, out IMFSample? sample);
                        if ((flags & EndOfStreamFlag) != 0)
                        {
                            break;
                        }

                        if (sample is null)
                        {
                            continue;
                        }

                        stage = "解包样本";
                        sample.ConvertToContiguousBuffer(out mediaBuffer);
                        if (mediaBuffer is null)
                        {
                            continue;
                        }

                        stage = "锁定缓冲区";
                        mediaBuffer.Lock(out IntPtr data, out _, out int currentLength);
                    try
                    {
                        long offset = 0;
                        while (offset < currentLength)
                        {
                            int chunk = (int)Math.Min(buffer.Length, currentLength - offset);
                            Marshal.Copy(IntPtr.Add(data, (int)offset), buffer, 0, chunk);
                            file.Write(buffer, 0, chunk);
                            offset += chunk;
                            total += chunk;
                        }
                    }
                    finally
                    {
                        mediaBuffer.Unlock();
                    }
                }
                finally
                {
                    if (mediaBuffer is not null && Marshal.IsComObject(mediaBuffer))
                    {
                        Marshal.FinalReleaseComObject(mediaBuffer);
                    }
                }
            }

            if (total == 0)
            {
                throw new InvalidDataException("音频文件没有可解码的音频流。");
            }

            if (total % 4 != 0)
            {
                // Defensive: a whole stereo sample always fits; pad the tail.
                file.Write(stackalloc byte[4 - (int)(total % 4)]);
                total += 4 - total % 4;
            }

            return total;
        }
        catch (COMException exception)
        {
            throw CreateDecodeException(exception.HResult, sourcePath, $"{exception.Message}（{stage}）");
        }
        finally
        {
            if (reader is not null && Marshal.IsComObject(reader))
            {
                Marshal.FinalReleaseComObject(reader);
            }

            Shutdown();
        }
    }

    private static InvalidDataException CreateDecodeException(int hResult, string sourcePath, string reason)
        => new($"解码音频文件失败 {Path.GetFileName(sourcePath)}（0x{hResult:X8}）：{reason}");

    private static IMFMediaType CreatePcmMediaType()
    {
        int hr = MFCreateMediaType(out IMFMediaType? type);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        if (type is null)
        {
            throw new InvalidOperationException("Media Foundation 未返回媒体类型对象。");
        }

        Guid audio = MajorTypeAudio;
        type.SetGUID(MtMajorType, ref audio);
        Guid pcm = SubtypePcm;
        type.SetGUID(MtSubtype, ref pcm);
        type.SetUINT32(MtAudioSamplesPerSecond, 44100);
        type.SetUINT32(MtAudioBitsPerSample, 16);
        type.SetUINT32(MtAudioChannels, 2);
        // Deliberately partial: block alignment and average byte rate stay unset so
        // the source reader completes the type and inserts its audio resampler.
        return type;
    }

    private static void Startup()
    {
        if (Interlocked.Increment(ref _startupCount) != 1)
        {
            return;
        }

        int hr = MFStartup(MfVersion, MfStartupLite);
        if (hr < 0)
        {
            _startupCount = 0;
            throw new PlatformNotSupportedException($"Windows Media Foundation 初始化失败（0x{hr:X8}）。");
        }
    }

    private static void Shutdown()
    {
        if (Interlocked.Decrement(ref _startupCount) == 0)
        {
            try { MFShutdown(); }
            catch (EntryPointNotFoundException) { }
        }
    }

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, int flags);

    [DllImport("mfplat.dll")]
    private static extern void MFShutdown();

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IMFMediaType? mediaType);

    [DllImport("mfreadwrite.dll")]
    private static extern int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string url,
        IntPtr attributes,
        out IMFSourceReader? reader);

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType
    {
        // IMFAttributes (slots 1..18, never called through this interface).
        void AttributesGetItem();
        void AttributesGetItemType();
        void AttributesCompareItem();
        void AttributesCompare();
        void AttributesGetUINT32();
        void AttributesGetUINT64();
        void AttributesGetDouble();
        void AttributesGetGUID();
        void AttributesGetStringLength();
        void AttributesGetString();
        void AttributesGetAllocatedString();
        void AttributesGetBlobSize();
        void AttributesGetBlob();
        void AttributesGetAllocatedBlob();
        void AttributesGetUnknown();
        void AttributesSetItem();
        void AttributesDeleteItem();
        void AttributesDeleteAllItems();
        // Slots 19..24: the attribute setters this decoder actually uses.
        void SetUINT32(ref Guid key, uint value);
        void AttributesSetUINT64();
        void AttributesSetDouble();
        void SetGUID(ref Guid key, ref Guid value);
        void AttributesSetString();
        void AttributesSetBlob();
        void AttributesSetUnknown();
        void AttributesLockStore();
        void AttributesUnlockStore();
        void AttributesGetCount();
        void AttributesGetItemByIndex();
        void AttributesCopyAllItems();
        // IMFMediaType (slots 31.., never called).
        void GetMajorType();
        void IsCompressedFormat();
        void IsEqual();
        void GetRepresentation();
        void FreeRepresentation();
    }

    // The two samples of interface inheritance below deliberately re-declare the
    // IMFAttributes slots instead of deriving from it: inlining the vtable keeps
    // every slot position explicit and immune to base-interface flattening.
    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        // IMFAttributes (slots 1..30, never called through this interface).
        void AttributesGetItem();
        void AttributesGetItemType();
        void AttributesCompareItem();
        void AttributesCompare();
        void AttributesGetUINT32();
        void AttributesGetUINT64();
        void AttributesGetDouble();
        void AttributesGetGUID();
        void AttributesGetStringLength();
        void AttributesGetString();
        void AttributesGetAllocatedString();
        void AttributesGetBlobSize();
        void AttributesGetBlob();
        void AttributesGetAllocatedBlob();
        void AttributesGetUnknown();
        void AttributesSetItem();
        void AttributesDeleteItem();
        void AttributesDeleteAllItems();
        void AttributesSetUINT32();
        void AttributesSetUINT64();
        void AttributesSetDouble();
        void AttributesSetGUID();
        void AttributesSetString();
        void AttributesSetBlob();
        void AttributesSetUnknown();
        void AttributesLockStore();
        void AttributesUnlockStore();
        void AttributesGetCount();
        void AttributesGetItemByIndex();
        void AttributesCopyAllItems();
        // IMFSample (slots 31..).
        void GetSampleFlags(out int flags);
        void SetSampleFlags(int flags);
        void GetSampleTime(out long timestamp);
        void SetSampleTime(long timestamp);
        void GetSampleDuration(out long duration);
        void SetSampleDuration(long duration);
        void GetBufferCount(out uint count);
        void GetBufferByIndex(uint index, out IntPtr buffer);
        void ConvertToContiguousBuffer(out IMFMediaBuffer? buffer);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        void Unlock();
        void GetCurrentLength(out int length);
        void SetCurrentLength(int length);
        void GetMaxLength(out int length);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSourceReader
    {
        void GetStreamSelection(uint streamIndex, out int selected);
        void SetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
        void GetNativeMediaType(uint streamIndex, uint typeIndex, out IMFMediaType? type);
        void GetCurrentMediaType(uint streamIndex, out IMFMediaType? type);
        void SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType type);
        void SetCurrentPosition(ref Guid format, IntPtr position);
        void ReadSample(
            uint streamIndex,
            uint controlFlags,
            out uint actualStreamIndex,
            out uint streamFlags,
            out long timestamp,
            out IMFSample? sample);
        void Flush(uint streamIndex);
        void GetServiceForStream(uint streamIndex, ref Guid service, ref Guid iid, out IntPtr serviceObject);
        void GetPresentationAttribute(uint streamIndex, ref Guid attribute, out long value);
    }
}
