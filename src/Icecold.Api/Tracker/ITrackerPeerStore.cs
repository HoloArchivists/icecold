namespace Icecold.Api.Tracker;

public interface ITrackerPeerStore
{
    TrackerAnnounceResult Announce(TrackerAnnounceInput input);
}
