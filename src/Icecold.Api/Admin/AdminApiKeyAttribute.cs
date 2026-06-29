using Microsoft.AspNetCore.Mvc;

namespace Icecold.Api.Admin;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AdminApiKeyAttribute() : TypeFilterAttribute(typeof(AdminApiKeyAuthorizationFilter));
