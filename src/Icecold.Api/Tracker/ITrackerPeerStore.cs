namespace Icecold.Api.Tracker;

public interface ITrackerPeerStore
{
    TrackerAnnounceResult Announce(TrackerAnnounceInput input);

    TrackerScrapeStats Scrape(string infoHashHex);
}
