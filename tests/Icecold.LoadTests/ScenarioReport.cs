using System.Text.Json;

namespace Icecold.LoadTests;

sealed class ScenarioReport
{
    public required string Scenario { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public double DurationSeconds { get; set; }

    public Dictionary<string, object?> Metrics { get; } = new(StringComparer.Ordinal);

    public string ToJson()
        => JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });

    public void Print()
        => Console.WriteLine(ToJson());
}
