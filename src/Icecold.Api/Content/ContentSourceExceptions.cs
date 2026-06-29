namespace Icecold.Api.Content;

public class ContentSourceException(string message) : Exception(message);

public sealed class ContentSourceNotFoundException(string sourceName)
    : ContentSourceException($"Content source '{sourceName}' is not configured.");

public sealed class ContentItemNotFoundException(string sourceName, string path)
    : ContentSourceException($"Content item '{path}' was not found in source '{sourceName}'.");
