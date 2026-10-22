using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Icecold.Api.Torrents;

namespace Icecold.Api.Indexing;

// Validation and identity for the file list of a multi-file index request.
public static class MultiFileManifest
{
    public const string SourcePathPrefix = "manifest:";

    const int MaxSegmentLength = 255;

    // Name and every path segment must be usable as a file or directory name by clients on any OS.
    public static string? ValidateName(string name)
        => ValidateSegment(name, "name");

    public static string? ValidateTorrentPath(string torrentPath)
    {
        if (string.IsNullOrEmpty(torrentPath))
            return "torrentPath must not be empty";

        foreach (var segment in torrentPath.Split('/'))
        {
            if (ValidateSegment(segment, $"torrentPath '{torrentPath}'") is { } error)
                return error;
        }

        // Reserved for the BEP 47 pad files the builder adds at the top level.
        if (torrentPath.Split('/')[0].Equals(".pad", StringComparison.OrdinalIgnoreCase))
            return $"torrentPath '{torrentPath}' must not start with the reserved directory '.pad'";

        return null;
    }

    // Paths are compared case-insensitively because clients on Windows and macOS would collide.
    public static string? FindConflict(IReadOnlyList<string> torrentPaths)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in torrentPaths)
        {
            if (!files.Add(path))
                return $"torrentPath '{path}' appears more than once";

            var separator = path.IndexOf('/');
            while (separator >= 0)
            {
                directories.Add(path[..separator]);
                separator = path.IndexOf('/', separator + 1);
            }
        }

        var clash = files.FirstOrDefault(directories.Contains);
        return clash is null ? null : $"torrentPath '{clash}' is both a file and a directory";
    }

    // Identity of the request: the same name and files (paths, placement and content versions)
    // always produce the same torrent, so resubmissions are matched by this hash.
    public static string Hash(string name, IReadOnlyList<MultiFileTorrentEntry> files)
    {
        var manifest = new object[]
        {
            name,
            files.Select(f => new object?[] { f.Metadata.Path, f.TorrentPath, f.Metadata.Length, f.Metadata.Version }).ToArray()
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest)));
    }

    static string? ValidateSegment(string segment, string label)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return $"{label} must not contain empty segments";

        if (segment is "." or "..")
            return $"{label} must not contain '.' or '..' segments";

        if (Encoding.UTF8.GetByteCount(segment) > MaxSegmentLength)
            return $"{label} has a segment longer than {MaxSegmentLength} bytes";

        if (segment.Any(c => char.IsControl(c) || c is '/' or '\\'))
            return $"{label} must not contain control characters, '/' or '\\' within a segment";

        return null;
    }
}
