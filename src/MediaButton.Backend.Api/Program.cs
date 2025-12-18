using System.Reflection;
using MediaButtonBackend.Auth;
using MediaButtonBackend.Data;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
{
    // Minimal API plus controllers; no special configuration yet.
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOrRelative", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
        {
            var roles = ctx.User.FindAll("roles").Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return roles.Contains("Admin") || roles.Contains("Relative");
        });
    });
});

builder.Services.AddScoped<StorageSasService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseMiddleware<DeviceAuthMiddleware>();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", () =>
{
    var payload = new HealthResponse(
        Status: "ok",
        Version: BuildInfo.Version,
        TimestampUtc: DateTimeOffset.UtcNow);

    return Results.Ok(payload);
});

app.MapGet("/api/info", () =>
{
    var payload = new ApiInfoResponse(
        Service: "BCH Media Button Backend",
        Version: BuildInfo.Version,
        Environment: app.Environment.EnvironmentName,
        Description: "Backend for Raspberry Pi devices and staff web UI.");

    return Results.Ok(payload);
});

app.Run();

internal static class BuildInfo
{
    internal static readonly string Version =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.1.0";
}

internal record HealthResponse(string Status, string Version, DateTimeOffset TimestampUtc);

internal record ApiInfoResponse(string Service, string Version, string Environment, string Description);
