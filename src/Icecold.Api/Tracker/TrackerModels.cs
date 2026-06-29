using System.Net;

namespace Icecold.Api.Tracker;

public sealed record TrackerAnnounceInput(
    string InfoHashHex,
    byte[] PeerId,
    IPAddress IpAddress,
    int Port,
    long Uploaded,
    long Downloaded,
    long Left,
    string? Event,
    bool Compact,
    int NumberWanted);

public sealed record PeerSnapshot(
    byte[] PeerId,
    IPAddress IpAddress,
    int Port,
    long Left);

public sealed record TrackerAnnounceResult(
    IReadOnlyList<PeerSnapshot> Peers,
    int Complete,
    int Incomplete,
    TimeSpan Interval,
    TimeSpan MinInterval);
