namespace Icecold.Api.Admin;

public sealed record IndexFileRequest(string Source, string Path, string? DisplayName, int? Priority = null);

// Indexes the listed files of one source as a single multi-file torrent, in the given order.
public sealed record IndexFilesRequest(string Source, string Name, IReadOnlyList<IndexFilesRequestEntry> Files, int? Priority = null);

public sealed record IndexFilesRequestEntry(string Path, string? TorrentPath = null);

public sealed record SetPriorityRequest(int Priority);
