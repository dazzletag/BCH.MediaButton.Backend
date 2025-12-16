using System.Reflection;
using MediaButtonBackend.Auth;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
{
    // Minimal API plus controllers; no special configuration yet.
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseMiddleware<DeviceAuthMiddleware>();

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
