using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Icecold.Api.Admin;

public sealed class AdminApiKeyHeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath;
        if (path is null || !path.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
            return;

        operation.Parameters ??= [];
        if (operation.Parameters.Any(parameter => parameter.Name == AdminApiKeyAuthorizationFilter.HeaderName))
            return;

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = AdminApiKeyAuthorizationFilter.HeaderName,
            In = ParameterLocation.Header,
            Required = true,
            Description = "Admin API key for local development."
        });
    }
}
