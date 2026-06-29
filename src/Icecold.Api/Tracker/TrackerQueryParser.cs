using System.Globalization;
using System.Net;
using System.Text;
using Icecold.Api.Torrents;

namespace Icecold.Api.Tracker;

public static class TrackerQueryParser
{
    public static bool TryParse(HttpRequest request, out TrackerAnnounceInput? input, out string failureReason)
    {
        input = null;
        failureReason = "";

        Dictionary<string, List<byte[]>> parameters;
        try
        {
            parameters = ParseRawQuery(request.QueryString.Value ?? "");
        }
        catch (FormatException ex)
        {
            failureReason = ex.Message;
            return false;
        }

        if (!TryGet(parameters, "info_hash", out var infoHash) || infoHash.Length != 20)
        {
            failureReason = "info_hash must be exactly 20 bytes";
            return false;
        }

        if (!TryGet(parameters, "peer_id", out var peerId) || peerId.Length != 20)
        {
            failureReason = "peer_id must be exactly 20 bytes";
            return false;
        }

        if (!TryGetInt(parameters, "port", out var port) || port is < 1 or > 65535)
        {
            failureReason = "port must be between 1 and 65535";
            return false;
        }

        if (!TryGetLong(parameters, "uploaded", out var uploaded)
            || !TryGetLong(parameters, "downloaded", out var downloaded)
            || !TryGetLong(parameters, "left", out var left))
        {
            failureReason = "uploaded, downloaded, and left are required integer values";
            return false;
        }

        var compact = TryGetString(parameters, "compact", out var compactValue) && compactValue == "1";
        var numberWanted = TryGetInt(parameters, "numwant", out var requested) ? requested : 50;
        var eventName = TryGetString(parameters, "event", out var parsedEvent) ? parsedEvent : null;
        var ipAddress = ResolveIpAddress(request, parameters);

        input = new TrackerAnnounceInput(
            InfoHashUtil.ToHex(infoHash),
            peerId,
            ipAddress,
            port,
            uploaded,
            downloaded,
            left,
            eventName,
            compact,
            numberWanted);

        return true;
    }

    static IPAddress ResolveIpAddress(HttpRequest request, Dictionary<string, List<byte[]>> parameters)
    {
        if (TryGetString(parameters, "ip", out var ipText) && IPAddress.TryParse(ipText, out var provided))
            return provided;

        return request.HttpContext.Connection.RemoteIpAddress ?? IPAddress.Loopback;
    }

    static Dictionary<string, List<byte[]>> ParseRawQuery(string rawQuery)
    {
        var result = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        var query = rawQuery.StartsWith('?') ? rawQuery[1..] : rawQuery;
        if (query.Length == 0)
            return result;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var rawKey = equals >= 0 ? pair[..equals] : pair;
            var rawValue = equals >= 0 ? pair[(equals + 1)..] : "";
            var key = Encoding.ASCII.GetString(PercentDecode(rawKey)).ToLowerInvariant();
            var value = PercentDecode(rawValue);

            if (!result.TryGetValue(key, out var values))
            {
                values = [];
                result[key] = values;
            }

            values.Add(value);
        }

        return result;
    }

    static byte[] PercentDecode(string value)
    {
        using var stream = new MemoryStream(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%')
            {
                if (i + 2 >= value.Length || !byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var decoded))
                    throw new FormatException("Query string contains invalid percent encoding.");

                stream.WriteByte(decoded);
                i += 2;
            }
            else
            {
                var codepoint = value[i];
                if (codepoint > byte.MaxValue)
                    throw new FormatException("Query string contains non-byte characters.");
                stream.WriteByte((byte)codepoint);
            }
        }

        return stream.ToArray();
    }

    static bool TryGet(Dictionary<string, List<byte[]>> parameters, string key, out byte[] value)
    {
        if (parameters.TryGetValue(key, out var values) && values.Count > 0)
        {
            value = values[0];
            return true;
        }

        value = [];
        return false;
    }

    static bool TryGetString(Dictionary<string, List<byte[]>> parameters, string key, out string value)
    {
        if (TryGet(parameters, key, out var bytes))
        {
            value = Encoding.ASCII.GetString(bytes);
            return true;
        }

        value = "";
        return false;
    }

    static bool TryGetInt(Dictionary<string, List<byte[]>> parameters, string key, out int value)
    {
        value = 0;
        return TryGetString(parameters, key, out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    static bool TryGetLong(Dictionary<string, List<byte[]>> parameters, string key, out long value)
    {
        value = 0;
        return TryGetString(parameters, key, out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
