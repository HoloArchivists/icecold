namespace Icecold.Api.PeerWire;

public sealed class PeerWireSuperSeedCoordinator
{
    readonly object gate = new();
    readonly Dictionary<string, int> activePeerCounts = new(StringComparer.Ordinal);
    readonly Dictionary<string, TorrentState> torrents = new(StringComparer.Ordinal);

    public PeerWirePeerLease TrackPeer(string infoHashHex)
    {
        lock (gate)
        {
            activePeerCounts.TryGetValue(infoHashHex, out var activePeerCount);
            activePeerCount++;
            activePeerCounts[infoHashHex] = activePeerCount;
            return new PeerWirePeerLease(this, infoHashHex, activePeerCount);
        }
    }

    public PeerWireSuperSeedSession CreateSession(string infoHashHex, int pieceCount, int maxPiecesPerPeer)
    {
        lock (gate)
        {
            if (!torrents.TryGetValue(infoHashHex, out var torrent))
            {
                torrent = new TorrentState(infoHashHex, pieceCount);
                torrents[infoHashHex] = torrent;
            }

            var session = new SessionState(Guid.NewGuid(), Math.Max(1, maxPiecesPerPeer));
            torrent.Sessions[session.Id] = session;
            return new PeerWireSuperSeedSession(this, torrent.InfoHashHex, session.Id);
        }
    }

    internal IReadOnlyList<int> AssignAvailable(string infoHashHex, Guid sessionId)
    {
        lock (gate)
        {
            if (!TryGetSession(infoHashHex, sessionId, out var torrent, out var session))
                return [];

            var assigned = new List<int>();
            while (session.Advertised.Count < session.MaxPiecesPerPeer)
            {
                var next = SelectPiece(torrent, session);
                if (next is null)
                    break;

                session.Advertised.Add(next.Value);
                assigned.Add(next.Value);
            }

            return assigned;
        }
    }

    internal bool CanServe(string infoHashHex, Guid sessionId, int pieceIndex)
    {
        lock (gate)
        {
            return TryGetSession(infoHashHex, sessionId, out _, out var session)
                && session.Advertised.Contains(pieceIndex);
        }
    }

    internal IReadOnlyList<int> MarkHave(string infoHashHex, Guid sessionId, int pieceIndex)
    {
        lock (gate)
        {
            if (!TryGetSession(infoHashHex, sessionId, out _, out var session))
                return [];

            session.Advertised.Remove(pieceIndex);
            session.Progress.Remove(pieceIndex);
        }

        return AssignAvailable(infoHashHex, sessionId);
    }

    internal IReadOnlyList<int> MarkBlockServed(
        string infoHashHex,
        Guid sessionId,
        int pieceIndex,
        int begin,
        int length,
        int pieceLength)
    {
        lock (gate)
        {
            if (!TryGetSession(infoHashHex, sessionId, out _, out var session)
                || !session.Advertised.Contains(pieceIndex))
            {
                return [];
            }

            if (!session.Progress.TryGetValue(pieceIndex, out var progress))
            {
                progress = new PieceProgress();
                session.Progress[pieceIndex] = progress;
            }

            if (!progress.Add(begin, begin + length, pieceLength))
                return [];

            session.Advertised.Remove(pieceIndex);
            session.Progress.Remove(pieceIndex);
        }

        return AssignAvailable(infoHashHex, sessionId);
    }

    internal void Close(string infoHashHex, Guid sessionId)
    {
        lock (gate)
        {
            if (!torrents.TryGetValue(infoHashHex, out var torrent))
                return;

            torrent.Sessions.Remove(sessionId);
            if (torrent.Sessions.Count == 0)
                torrents.Remove(infoHashHex);
        }
    }

    internal void ReleasePeer(string infoHashHex)
    {
        lock (gate)
        {
            if (!activePeerCounts.TryGetValue(infoHashHex, out var activePeerCount))
                return;

            if (activePeerCount <= 1)
            {
                activePeerCounts.Remove(infoHashHex);
                return;
            }

            activePeerCounts[infoHashHex] = activePeerCount - 1;
        }
    }

    bool TryGetSession(string infoHashHex, Guid sessionId, out TorrentState torrent, out SessionState session)
    {
        if (torrents.TryGetValue(infoHashHex, out torrent!)
            && torrent.Sessions.TryGetValue(sessionId, out session!))
        {
            return true;
        }

        torrent = null!;
        session = null!;
        return false;
    }

    static int? SelectPiece(TorrentState torrent, SessionState session)
    {
        if (torrent.PieceCount <= 0 || session.Advertised.Count >= torrent.PieceCount)
            return null;

        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < torrent.PieceCount; i++)
            {
                var candidate = (torrent.NextPiece + i) % torrent.PieceCount;
                if (session.Advertised.Contains(candidate))
                    continue;

                var assignedElsewhere = torrent.Sessions.Values.Any(s => s.Id != session.Id && s.Advertised.Contains(candidate));
                if (pass == 0 && assignedElsewhere)
                    continue;

                torrent.NextPiece = (candidate + 1) % torrent.PieceCount;
                return candidate;
            }
        }

        return null;
    }

    sealed class TorrentState(string infoHashHex, int pieceCount)
    {
        public string InfoHashHex { get; } = infoHashHex;
        public int PieceCount { get; } = pieceCount;
        public int NextPiece { get; set; }
        public Dictionary<Guid, SessionState> Sessions { get; } = [];
    }

    sealed class SessionState(Guid id, int maxPiecesPerPeer)
    {
        public Guid Id { get; } = id;
        public int MaxPiecesPerPeer { get; } = maxPiecesPerPeer;
        public HashSet<int> Advertised { get; } = [];
        public Dictionary<int, PieceProgress> Progress { get; } = [];
    }

    sealed class PieceProgress
    {
        readonly List<(int Start, int End)> ranges = [];

        public bool Add(int start, int end, int pieceLength)
        {
            if (start < 0 || end <= start)
                return false;

            start = Math.Min(start, pieceLength);
            end = Math.Min(end, pieceLength);
            ranges.Add((start, end));
            ranges.Sort((left, right) => left.Start.CompareTo(right.Start));

            var covered = 0;
            var currentStart = -1;
            var currentEnd = -1;
            foreach (var range in ranges)
            {
                if (currentStart < 0)
                {
                    currentStart = range.Start;
                    currentEnd = range.End;
                    continue;
                }

                if (range.Start <= currentEnd)
                {
                    currentEnd = Math.Max(currentEnd, range.End);
                    continue;
                }

                covered += currentEnd - currentStart;
                currentStart = range.Start;
                currentEnd = range.End;
            }

            if (currentStart >= 0)
                covered += currentEnd - currentStart;

            return covered >= pieceLength;
        }
    }
}

public sealed class PeerWirePeerLease(
    PeerWireSuperSeedCoordinator coordinator,
    string infoHashHex,
    int activePeerCount) : IAsyncDisposable
{
    int disposed;

    public int ActivePeerCount { get; } = activePeerCount;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            coordinator.ReleasePeer(infoHashHex);

        return ValueTask.CompletedTask;
    }
}

public sealed class PeerWireSuperSeedSession(
    PeerWireSuperSeedCoordinator coordinator,
    string infoHashHex,
    Guid sessionId) : IAsyncDisposable
{
    public IReadOnlyList<int> AssignAvailable()
        => coordinator.AssignAvailable(infoHashHex, sessionId);

    public bool CanServe(int pieceIndex)
        => coordinator.CanServe(infoHashHex, sessionId, pieceIndex);

    public IReadOnlyList<int> MarkHave(int pieceIndex)
        => coordinator.MarkHave(infoHashHex, sessionId, pieceIndex);

    public IReadOnlyList<int> MarkBlockServed(PeerWirePieceRequest request, int pieceLength)
        => coordinator.MarkBlockServed(
            infoHashHex,
            sessionId,
            request.PieceIndex,
            request.Begin,
            request.Length,
            pieceLength);

    public ValueTask DisposeAsync()
    {
        coordinator.Close(infoHashHex, sessionId);
        return ValueTask.CompletedTask;
    }
}
