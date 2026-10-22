namespace Icecold.Api.Admin;

public sealed record IndexFileRequest(string Source, string Path, string? DisplayName, int? Priority = null);

public sealed record SetPriorityRequest(int Priority);
