using System.IO;

namespace DiscImageStudio.Services;

/// <summary>Counts audio files inside a data folder. Pure enumeration: nothing is decoded;
/// the row exists so DVD/BD projects that carry music show it at a glance.</summary>
internal static class AudioFileSummary
{
    private static readonly string[] AudioExtensions =
    [
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma"
    ];

    internal static bool IsAudioFile(string path)
        => AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Recursive count and byte total; unreadable entries are skipped rather than
    /// failing the summary.</summary>
    internal static (int Count, long Bytes) Scan(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return (0, 0);
        }

        int count = 0;
        long bytes = 0;
        EnumerationOptions options = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
        };
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*", options))
            {
                if (!IsAudioFile(path))
                {
                    continue;
                }

                count++;
                try
                {
                    bytes += new FileInfo(path).Length;
                }
                catch (FileNotFoundException)
                {
                }
                catch (IOException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return (count, bytes);
    }
}
