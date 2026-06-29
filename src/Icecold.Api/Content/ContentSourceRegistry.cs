using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Content;

public sealed class ContentSourceRegistry
{
    readonly IReadOnlyDictionary<string, IContentSource> sources;

    public ContentSourceRegistry(IOptions<IcecoldOptions> options, IWebHostEnvironment environment)
    {
        var configuredSources = options.Value.ContentSources.Count == 0
            ? [new ContentSourceOptions
            {
                Name = "local",
                Type = "local",
                RootPath = Path.Combine(environment.ContentRootPath, "data", "files")
            }]
            : options.Value.ContentSources;

        var built = new Dictionary<string, IContentSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in configuredSources)
        {
            if (string.IsNullOrWhiteSpace(source.Name))
                throw new InvalidOperationException("Every content source must have a name.");

            if (built.ContainsKey(source.Name))
                throw new InvalidOperationException($"Duplicate content source '{source.Name}'.");

            built[source.Name] = source.Type.ToLowerInvariant() switch
            {
                "local" => new LocalFileContentSource(source.Name, ResolveConfiguredRootPath(source, environment.ContentRootPath)),
                _ => throw new InvalidOperationException($"Unsupported content source type '{source.Type}' for '{source.Name}'.")
            };
        }

        sources = built;
    }

    public IContentSource GetRequired(string name)
        => sources.TryGetValue(name, out var source) ? source : throw new ContentSourceNotFoundException(name);

    static string ResolveConfiguredRootPath(ContentSourceOptions source, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(source.RootPath))
            throw new InvalidOperationException($"Local content source '{source.Name}' must configure RootPath.");

        return Path.IsPathRooted(source.RootPath)
            ? source.RootPath
            : Path.Combine(contentRootPath, source.RootPath);
    }
}
