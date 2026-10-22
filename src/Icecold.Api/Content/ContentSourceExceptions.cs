namespace Icecold.Api.Content;

public class ContentSourceException(string message) : Exception(message);

public sealed class ContentSourceNotFoundException(string sourceName)
    : ContentSourceException($"Content source '{sourceName}' is not configured.");

public sealed class ContentItemNotFoundException(string sourceName, string path)
    : ContentSourceException($"Content item '{path}' was not found in source '{sourceName}'.");

// The backing store is temporarily unreachable (timeouts, 5xx, dropped connections). Indexing
// retries these instead of failing the job.
public sealed class ContentSourceUnavailableException(string message, Exception? innerException = null)
    : IOException(message, innerException);
