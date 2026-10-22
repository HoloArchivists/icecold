namespace Icecold.Api.Data;

// One backing file of a multi-file torrent. Index is the file's position in the info dictionary;
// its offset in the torrent's byte space follows from the lengths and the piece length
// (see TorrentContentLayout), so it is not stored.
public sealed class TorrentFileRecord
{
    public Guid Id { get; set; }

    public Guid TorrentId { get; set; }

    public int Index { get; set; }

    public string SourcePath { get; set; } = "";

    // Slash-separated path inside the torrent, below the torrent name.
    public string TorrentPath { get; set; } = "";

    public long ContentLength { get; set; }

    public string? ContentVersion { get; set; }

    public DateTimeOffset? ContentLastModified { get; set; }
}
