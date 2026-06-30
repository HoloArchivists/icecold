using System.Globalization;

namespace Icecold.LoadTests;

sealed class CliOptions
{
    readonly Dictionary<string, string?> values;

    CliOptions(Dictionary<string, string?> values)
    {
        this.values = values;
    }

    public static CliOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{arg}'. Options must use --name value or --name=value.");

            var keyValue = arg[2..];
            var equals = keyValue.IndexOf('=');
            if (equals >= 0)
            {
                values[keyValue[..equals]] = keyValue[(equals + 1)..];
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                values[keyValue] = args[++i];
            else
                values[keyValue] = "true";
        }

        return new CliOptions(values);
    }

    public string GetString(string key, string defaultValue)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : defaultValue;

    public string? GetString(string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    public int GetInt32(string key, int defaultValue)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? int.Parse(value, CultureInfo.InvariantCulture)
            : defaultValue;

    public long GetInt64(string key, long defaultValue)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? long.Parse(value, CultureInfo.InvariantCulture)
            : defaultValue;

    public bool GetBoolean(string key, bool defaultValue)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? bool.Parse(value)
            : defaultValue;

    public Uri GetUri(string key, string defaultValue)
        => new(GetString(key, defaultValue), UriKind.Absolute);
}
