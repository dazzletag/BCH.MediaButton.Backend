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

// Relax audience validation to accept both GUID and api://GUID forms
builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
{
    var clientId = builder.Configuration["AzureAd:ClientId"];
    var configuredAudience = builder.Configuration["AzureAd:Audience"];
    var audiences = new List<string?>();
    if (!string.IsNullOrWhiteSpace(configuredAudience))
        audiences.Add(configuredAudience);
    if (!string.IsNullOrWhiteSpace(clientId))
    {
        audiences.Add(clientId);
        audiences.Add($"api://{clientId}");
    }
    options.TokenValidationParameters.ValidateAudience = false;
    options.TokenValidationParameters.ValidAudiences = audiences.Where(a => !string.IsNullOrWhiteSpace(a));
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOrRelative", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
        {
            // Roles can arrive as either the raw "roles" claim or the mapped ClaimTypes.Role.
            var roleValues = ctx.User.Claims
                .Where(c =>
                    string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Type, System.Security.Claims.ClaimTypes.Role, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return roleValues.Contains("Admin") || roleValues.Contains("Relative");
        });
    });

    options.AddPolicy("AdminOnly", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
        {
            var roleValues = ctx.User.Claims
                .Where(c =>
                    string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Type, System.Security.Claims.ClaimTypes.Role, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return roleValues.Contains("Admin");
        });
    });
});

builder.Services.AddScoped<StorageSasService>();
builder.Services.AddScoped<StorageCorsInitializer>();
builder.Services.AddScoped<MediaButtonBackend.Services.MobizioService>();
builder.Services.AddHttpClient();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseMiddleware<DeviceAuthMiddleware>();
app.UseAuthorization();

app.MapControllers();

// Apply any pending EF Core migrations and ensure Blob Storage CORS rules
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    var corsInit = scope.ServiceProvider.GetRequiredService<StorageCorsInitializer>();
    corsInit.EnsureCorsAsync().GetAwaiter().GetResult();
}

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

app.MapFallbackToFile("index.html");

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
