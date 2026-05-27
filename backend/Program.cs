using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("config.json", optional: true, reloadOnChange: true);

var configuredDataRoot = builder.Configuration["Panel:DataRoot"] ?? "data";
var dataRoot = Path.GetFullPath(Path.IsPathRooted(configuredDataRoot)
    ? configuredDataRoot
    : Path.Combine(builder.Environment.ContentRootPath, configuredDataRoot));
Directory.CreateDirectory(dataRoot);

var httpPort = builder.Configuration.GetValue("Panel:HttpPort", 8080);
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(httpPort));

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 1024L * 1024L * 1024L;
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("dev", policy => policy
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowAnyOrigin());
});

builder.Services.AddDbContext<PanelDbContext>((serviceProvider, options) =>
{
    var config = serviceProvider.GetRequiredService<IConfiguration>();
    var provider = (config["Panel:DatabaseProvider"] ?? "sqlite").ToLowerInvariant();
    if (provider == "postgres" || provider == "postgresql")
    {
        options.UseNpgsql(config.GetConnectionString("PostgreSQL"));
        return;
    }

    var sqlite = config.GetConnectionString("SQLite") ?? $"Data Source={Path.Combine(dataRoot, "panel.db")}";
    if (sqlite.Contains("Data Source=data/", StringComparison.OrdinalIgnoreCase) ||
        sqlite.Contains("Data Source=data\\", StringComparison.OrdinalIgnoreCase))
    {
        sqlite = sqlite.Replace("Data Source=data/", $"Data Source={dataRoot.Replace("\\", "/")}/", StringComparison.OrdinalIgnoreCase)
            .Replace("Data Source=data\\", $"Data Source={dataRoot}\\", StringComparison.OrdinalIgnoreCase);
    }

    options.UseSqlite(sqlite);
});

var jwtSecret = builder.Configuration["Panel:JwtSecret"] ?? "";
if (jwtSecret.Length < 32)
{
    jwtSecret = "DEV_ONLY_CHANGE_THIS_SECRET_TO_AT_LEAST_32_CHARS";
}

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromSeconds(15)
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.HttpContext.Request.Path.Value?.Contains("/logs/stream", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var token = context.Request.Query["access_token"].ToString();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        context.Token = token;
                    }
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(UserRole.Admin));
});

builder.Services.AddSingleton(new PanelPaths(dataRoot));
builder.Services.AddSingleton<TemplateStore>();
builder.Services.AddSingleton<DockerEngine>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<FileManagerService>();
builder.Services.AddHostedService<ServerAutoStartService>();
builder.Services.AddHostedService<DailyBackupService>();

var app = builder.Build();

app.UseCors("dev");
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (FileNotFoundException ex)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { message = ex.Message });
    }
    catch (DockerApiException ex)
    {
        context.Response.StatusCode = (int)ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { message = ex.ResponseBody ?? ex.Message });
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { message = ex.Message });
    }
});
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});
app.UseAuthentication();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseAuthorization();

await EnsureDatabaseAsync(app.Services);

if (args.Any(arg => arg.Equals("setup", StringComparison.OrdinalIgnoreCase)))
{
    await CliSetup.RunAsync(app.Services, args);
    return;
}

app.MapGet("/api/health", () => Results.Ok(new
{
    ok = true,
    time = DateTimeOffset.UtcNow,
    mode = "GameHostPanel"
})).AllowAnonymous();

app.MapGet("/api/auth/bootstrap-required", async (PanelDbContext db) =>
{
    return Results.Ok(new { required = !await db.Users.AnyAsync() });
}).AllowAnonymous();

app.MapPost("/api/auth/bootstrap", async (BootstrapRequest request, PanelDbContext db, JwtTokenService tokens) =>
{
    if (await db.Users.AnyAsync())
    {
        return Results.BadRequest(new { message = "Bootstrap ist bereits abgeschlossen." });
    }

    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
    {
        return Results.BadRequest(new { message = "Benutzername und Passwort mit mindestens 8 Zeichen sind erforderlich." });
    }

    var user = new PanelUser
    {
        Username = request.Username.Trim(),
        PasswordHash = PasswordHasher.Hash(request.Password),
        Role = UserRole.Admin
    };
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Ok(tokens.CreateLoginResponse(user));
}).AllowAnonymous();

app.MapPost("/api/auth/login", async (LoginRequest request, PanelDbContext db, JwtTokenService tokens) =>
{
    var user = await db.Users.FirstOrDefaultAsync(x => x.Username == request.Username);
    if (user is null || !PasswordHasher.Verify(request.Password, user.PasswordHash))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(tokens.CreateLoginResponse(user));
}).AllowAnonymous();

app.MapGet("/api/auth/me", (ClaimsPrincipal user) => Results.Ok(UserSummary.FromClaims(user)))
    .RequireAuthorization();

app.MapGet("/api/templates", (TemplateStore templates) => Results.Ok(templates.All()))
    .RequireAuthorization();

app.MapGet("/api/dashboard", async (PanelDbContext db, DockerEngine docker, CancellationToken ct) =>
{
    var servers = await QueryServers(db).ToListAsync(ct);
    var running = servers.Count(x => x.Status == ServerStatus.Running);
    var dockerStatus = await docker.GetStatusSummaryAsync(ct);
    return Results.Ok(new DashboardDto(
        servers.Count,
        running,
        servers.Count - running,
        servers.Where(x => x.Status == ServerStatus.Running).Select(x => ServerDto.FromEntity(x)).ToList(),
        dockerStatus));
}).RequireAuthorization();

app.MapGet("/api/servers", async (PanelDbContext db, CancellationToken ct) =>
{
    var servers = (await QueryServers(db).ToListAsync(ct))
        .OrderByDescending(x => x.CreatedAt)
        .ToList();
    return Results.Ok(servers.Select(x => ServerDto.FromEntity(x)));
}).RequireAuthorization();

app.MapGet("/api/servers/{id:guid}", async (Guid id, PanelDbContext db, DockerEngine docker, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    var status = await docker.TryRefreshStatusAsync(server, ct);
    if (status.Changed)
    {
        await db.SaveChangesAsync(ct);
    }

    var stats = await docker.TryGetStatsAsync(server, ct);
    return Results.Ok(ServerDto.FromEntity(server, stats));
}).RequireAuthorization();

app.MapPost("/api/servers", async (
    CreateServerRequest request,
    PanelDbContext db,
    TemplateStore templates,
    DockerEngine docker,
    PanelPaths paths,
    CancellationToken ct) =>
{
    var template = templates.Find(request.TemplateId);
    if (template is null)
    {
        return Results.BadRequest(new { message = "Template nicht gefunden." });
    }

    var server = new GameServer
    {
        Id = Guid.NewGuid(),
        Name = string.IsNullOrWhiteSpace(request.Name) ? template.Name : request.Name.Trim(),
        Description = request.Description?.Trim() ?? template.Description,
        TemplateId = template.Id,
        Game = template.Category,
        Image = string.IsNullOrWhiteSpace(request.Image) ? template.Image : request.Image.Trim(),
        StartCommand = request.StartCommand ?? template.StartCommand ?? "",
        MemoryMb = request.MemoryMb ?? template.RecommendedMemoryMb,
        CpuLimit = request.CpuLimit ?? 0,
        AutoStart = request.AutoStart,
        Status = ServerStatus.Stopped
    };

    foreach (var pair in Merge(template.Env, request.Env))
    {
        server.Environment.Add(new ServerEnvironmentVariable { Key = pair.Key, Value = pair.Value });
    }

    var requestedPorts = (request.Ports is { Count: > 0 }
            ? request.Ports.Select(port => new PortDto(port.Container, port.Host, port.Protocol))
            : template.Ports.Select(port => new PortDto(port.Container, port.Host, port.Protocol)))
        .ToList();
    foreach (var port in requestedPorts)
    {
        server.Ports.Add(new ServerPort
        {
            ContainerPort = port.Container,
            HostPort = port.Host,
            Protocol = NormalizeProtocol(port.Protocol)
        });
    }

    var serverRoot = paths.ServerRoot(server.Id);
    Directory.CreateDirectory(serverRoot);
    var requestedVolumes = (request.Volumes is { Count: > 0 } ? request.Volumes : template.Volumes).ToList();
    if (requestedVolumes.Count == 0)
    {
        requestedVolumes.Add(new TemplateVolume("/data", false));
    }

    foreach (var volume in requestedVolumes)
    {
        server.Volumes.Add(new ServerVolume
        {
            HostPath = string.IsNullOrWhiteSpace(volume.HostPath)
                ? serverRoot
                : Path.GetFullPath(volume.HostPath),
            ContainerPath = volume.ContainerPath,
            ReadOnly = volume.ReadOnly
        });
    }

    db.Servers.Add(server);
    await db.SaveChangesAsync(ct);

    if (request.PullImage)
    {
        await docker.PullImageAsync(server.Image, ct);
    }

    await docker.EnsureContainerAsync(server, ct);
    if (request.StartNow)
    {
        await docker.StartAsync(server, ct);
    }

    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/servers/{server.Id}", ServerDto.FromEntity(server));
}).RequireAuthorization();

app.MapPut("/api/servers/{id:guid}", async (
    Guid id,
    UpdateServerRequest request,
    PanelDbContext db,
    DockerEngine docker,
    CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    if (!string.IsNullOrWhiteSpace(request.Name))
    {
        server.Name = request.Name.Trim();
    }

    server.Description = request.Description ?? server.Description;
    server.Image = request.Image ?? server.Image;
    server.StartCommand = request.StartCommand ?? server.StartCommand;
    server.MemoryMb = request.MemoryMb ?? server.MemoryMb;
    server.CpuLimit = request.CpuLimit ?? server.CpuLimit;
    server.AutoStart = request.AutoStart ?? server.AutoStart;
    server.UpdatedAt = DateTimeOffset.UtcNow;

    if (request.Env is not null)
    {
        server.Environment.Clear();
        foreach (var pair in request.Env.OrderBy(x => x.Key))
        {
            server.Environment.Add(new ServerEnvironmentVariable { Key = pair.Key, Value = pair.Value });
        }
    }

    if (request.Ports is not null)
    {
        server.Ports.Clear();
        foreach (var port in request.Ports)
        {
            server.Ports.Add(new ServerPort
            {
                ContainerPort = port.Container,
                HostPort = port.Host,
                Protocol = NormalizeProtocol(port.Protocol)
            });
        }
    }

    await db.SaveChangesAsync(ct);

    if (request.RecreateContainer)
    {
        await docker.DeleteContainerAsync(server, ct);
        await docker.EnsureContainerAsync(server, ct);
        await db.SaveChangesAsync(ct);
    }

    return Results.Ok(ServerDto.FromEntity(server));
}).RequireAuthorization();

app.MapPost("/api/servers/{id:guid}/start", async (Guid id, PanelDbContext db, DockerEngine docker, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    await docker.EnsureContainerAsync(server, ct);
    await docker.StartAsync(server, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(ServerDto.FromEntity(server));
}).RequireAuthorization();

app.MapPost("/api/servers/{id:guid}/stop", async (Guid id, PanelDbContext db, DockerEngine docker, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    await docker.StopAsync(server, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(ServerDto.FromEntity(server));
}).RequireAuthorization();

app.MapPost("/api/servers/{id:guid}/restart", async (Guid id, PanelDbContext db, DockerEngine docker, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    await docker.StopAsync(server, ct);
    await docker.StartAsync(server, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(ServerDto.FromEntity(server));
}).RequireAuthorization();

app.MapPost("/api/servers/{id:guid}/kill", async (Guid id, PanelDbContext db, DockerEngine docker, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    await docker.KillAsync(server, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(ServerDto.FromEntity(server));
}).RequireAuthorization();

app.MapDelete("/api/servers/{id:guid}", async (Guid id, bool? deleteFiles, PanelDbContext db, DockerEngine docker, FileManagerService files, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    await docker.DeleteContainerAsync(server, ct);
    if (deleteFiles == true)
    {
        files.DeleteServerRoot(server);
    }

    db.Servers.Remove(server);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/servers/{id:guid}/stats", async (Guid id, PanelDbContext db, DockerEngine docker, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    var stats = await docker.TryGetStatsAsync(server, ct);
    return Results.Ok(stats);
}).RequireAuthorization();

app.MapGet("/api/servers/{id:guid}/logs/stream", async (Guid id, HttpContext context, PanelDbContext db, DockerEngine docker, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket request required", ct);
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await docker.StreamLogsAsync(server, socket, ct);
}).RequireAuthorization();

app.MapPost("/api/servers/{id:guid}/console/command", async (Guid id, ConsoleCommandRequest request, PanelDbContext db, DockerEngine docker, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    var output = await docker.ExecuteConsoleCommandAsync(server, request.Command, ct);
    return Results.Ok(new ConsoleCommandResponse(output));
}).RequireAuthorization();

app.MapGet("/api/servers/{id:guid}/files", async (Guid id, string? path, PanelDbContext db, FileManagerService files, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(await files.ListAsync(server, path ?? "", ct));
}).RequireAuthorization();

app.MapGet("/api/servers/{id:guid}/files/content", async (Guid id, string path, PanelDbContext db, FileManagerService files, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(await files.ReadTextAsync(server, path, ct));
}).RequireAuthorization();

app.MapPut("/api/servers/{id:guid}/files/content", async (Guid id, string path, FileWriteRequest request, PanelDbContext db, FileManagerService files, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    await files.WriteTextAsync(server, path, request.Content, ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/servers/{id:guid}/files/download", async (Guid id, string path, PanelDbContext db, FileManagerService files, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    var file = files.ResolveFile(server, path);
    return Results.File(file, "application/octet-stream", Path.GetFileName(file));
}).RequireAuthorization();

app.MapPost("/api/servers/{id:guid}/files/upload", async (Guid id, string? path, IFormFile file, PanelDbContext db, FileManagerService files, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    await files.UploadAsync(server, path ?? "", file, ct);
    return Results.Ok(new { uploaded = file.FileName, file.Length });
}).DisableAntiforgery().RequireAuthorization();

app.MapPost("/api/servers/{id:guid}/files/mkdir", async (Guid id, FilePathRequest request, PanelDbContext db, FileManagerService files, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    files.CreateDirectory(server, request.Path);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/servers/{id:guid}/files/touch", async (Guid id, FilePathRequest request, PanelDbContext db, FileManagerService files, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    await files.CreateFileAsync(server, request.Path, ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/servers/{id:guid}/files/move", async (Guid id, FileMoveRequest request, PanelDbContext db, FileManagerService files, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    files.MovePath(server, request.SourcePath, request.TargetPath);
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/servers/{id:guid}/files", async (Guid id, string path, PanelDbContext db, FileManagerService files, CancellationToken ct) =>
{
    var server = await FindServerAsync(db, id, ct);
    if (server is null)
    {
        return Results.NotFound();
    }

    files.DeletePath(server, path);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/docker/status", async (DockerEngine docker, CancellationToken ct) => Results.Ok(await docker.GetStatusSummaryAsync(ct)))
    .RequireAuthorization("AdminOnly");

app.MapGet("/api/docker/images", async (DockerEngine docker, CancellationToken ct) => Results.Ok(await docker.ListImagesAsync(ct)))
    .RequireAuthorization("AdminOnly");

app.MapPost("/api/docker/images/pull", async (ImagePullRequest request, DockerEngine docker, CancellationToken ct) =>
{
    await docker.PullImageAsync(request.Image, ct);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

app.MapDelete("/api/docker/images/{image}", async (string image, bool? force, DockerEngine docker, CancellationToken ct) =>
{
    await docker.DeleteImageAsync(WebUtility.UrlDecode(image), force ?? false, ct);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/docker/volumes", async (DockerEngine docker, CancellationToken ct) => Results.Ok(await docker.ListVolumesAsync(ct)))
    .RequireAuthorization("AdminOnly");

app.MapGet("/api/docker/networks", async (DockerEngine docker, CancellationToken ct) => Results.Ok(await docker.ListNetworksAsync(ct)))
    .RequireAuthorization("AdminOnly");

app.MapGet("/api/users", async (PanelDbContext db, CancellationToken ct) =>
{
    var users = await db.Users.OrderBy(x => x.Username).Select(x => new UserSummary(x.Id, x.Username, x.Role, x.CreatedAt)).ToListAsync(ct);
    return Results.Ok(users);
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/users", async (CreateUserRequest request, PanelDbContext db, CancellationToken ct) =>
{
    if (await db.Users.AnyAsync(x => x.Username == request.Username, ct))
    {
        return Results.BadRequest(new { message = "Benutzer existiert bereits." });
    }

    var user = new PanelUser
    {
        Username = request.Username.Trim(),
        PasswordHash = PasswordHasher.Hash(request.Password),
        Role = request.Role == UserRole.Admin ? UserRole.Admin : UserRole.User
    };
    db.Users.Add(user);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/users/{user.Id}", new UserSummary(user.Id, user.Username, user.Role, user.CreatedAt));
}).RequireAuthorization("AdminOnly");

app.MapDelete("/api/users/{id:int}", async (int id, PanelDbContext db, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var currentId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (currentId == id.ToString())
    {
        return Results.BadRequest(new { message = "Du kannst dich nicht selbst löschen." });
    }

    var user = await db.Users.FindAsync([id], ct);
    if (user is null)
    {
        return Results.NotFound();
    }

    db.Users.Remove(user);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/api-keys", async (PanelDbContext db, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var userId = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var isAdmin = principal.IsInRole(UserRole.Admin);
    var query = db.ApiKeys.AsNoTracking();
    if (!isAdmin)
    {
        query = query.Where(x => x.UserId == userId);
    }

    var keys = (await query
            .Select(x => new ApiKeySummary(x.Id, x.Name, x.Prefix, x.CreatedAt, x.LastUsedAt, x.ExpiresAt))
            .ToListAsync(ct))
        .OrderByDescending(x => x.CreatedAt)
        .ToList();
    return Results.Ok(keys);
}).RequireAuthorization();

app.MapPost("/api/api-keys", async (CreateApiKeyRequest request, PanelDbContext db, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var userId = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var rawKey = $"ghp_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("+", "").Replace("/", "").Replace("=", "")}";
    var key = new ApiKey
    {
        Name = string.IsNullOrWhiteSpace(request.Name) ? "API Key" : request.Name.Trim(),
        Prefix = rawKey[..Math.Min(12, rawKey.Length)],
        KeyHash = PasswordHasher.Hash(rawKey),
        UserId = userId,
        ExpiresAt = request.ExpiresAt
    };
    db.ApiKeys.Add(key);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/api-keys/{key.Id}", new { key = rawKey, summary = new ApiKeySummary(key.Id, key.Name, key.Prefix, key.CreatedAt, key.LastUsedAt, key.ExpiresAt) });
}).RequireAuthorization();

app.MapDelete("/api/api-keys/{id:int}", async (int id, PanelDbContext db, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var userId = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var isAdmin = principal.IsInRole(UserRole.Admin);
    var key = await db.ApiKeys.FindAsync([id], ct);
    if (key is null)
    {
        return Results.NotFound();
    }

    if (!isAdmin && key.UserId != userId)
    {
        return Results.Forbid();
    }

    db.ApiKeys.Remove(key);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

var frontendDist = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "frontend", "dist"));
if (Directory.Exists(frontendDist))
{
    var provider = new PhysicalFileProvider(frontendDist);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
    app.MapFallback(async context =>
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(frontendDist, "index.html"));
    });
}

app.Run();

static IQueryable<GameServer> QueryServers(PanelDbContext db)
{
    return db.Servers
        .Include(x => x.Ports)
        .Include(x => x.Environment)
        .Include(x => x.Volumes);
}

static Task<GameServer?> FindServerAsync(PanelDbContext db, Guid id, CancellationToken ct)
{
    return QueryServers(db).FirstOrDefaultAsync(x => x.Id == id, ct);
}

static async Task EnsureDatabaseAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
    await db.Database.EnsureCreatedAsync();
}

static Dictionary<string, string> Merge(Dictionary<string, string> left, Dictionary<string, string>? right)
{
    var merged = new Dictionary<string, string>(left, StringComparer.OrdinalIgnoreCase);
    if (right is null)
    {
        return merged;
    }

    foreach (var pair in right)
    {
        merged[pair.Key] = pair.Value;
    }

    return merged;
}

static string NormalizeProtocol(string? protocol)
{
    return string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
}

public static class ServerStatus
{
    public const string Created = "created";
    public const string Running = "running";
    public const string Stopped = "stopped";
    public const string Restarting = "restarting";
    public const string Crashed = "crashed";
    public const string Missing = "missing";
}

public static class UserRole
{
    public const string Admin = "Admin";
    public const string User = "User";
}

public sealed class PanelPaths(string dataRoot)
{
    public string DataRoot { get; } = dataRoot;

    public string ServerRoot(Guid id)
    {
        return Path.Combine(DataRoot, "servers", id.ToString("N"));
    }
}

public sealed class PanelDbContext(DbContextOptions<PanelDbContext> options) : DbContext(options)
{
    public DbSet<PanelUser> Users => Set<PanelUser>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<GameServer> Servers => Set<GameServer>();
    public DbSet<ServerPort> ServerPorts => Set<ServerPort>();
    public DbSet<ServerEnvironmentVariable> ServerEnvironment => Set<ServerEnvironmentVariable>();
    public DbSet<ServerVolume> ServerVolumes => Set<ServerVolume>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PanelUser>()
            .HasIndex(x => x.Username)
            .IsUnique();

        modelBuilder.Entity<GameServer>()
            .HasMany(x => x.Ports)
            .WithOne(x => x.Server)
            .HasForeignKey(x => x.ServerId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GameServer>()
            .HasMany(x => x.Environment)
            .WithOne(x => x.Server)
            .HasForeignKey(x => x.ServerId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GameServer>()
            .HasMany(x => x.Volumes)
            .WithOne(x => x.Server)
            .HasForeignKey(x => x.ServerId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ApiKey>()
            .HasOne(x => x.User)
            .WithMany(x => x.ApiKeys)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PanelUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = UserRole.User;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ApiKey> ApiKeys { get; set; } = [];
}

public sealed class ApiKey
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public int UserId { get; set; }
    public PanelUser? User { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class GameServer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string Game { get; set; } = "";
    public string Image { get; set; } = "";
    public string StartCommand { get; set; } = "";
    public string Status { get; set; } = ServerStatus.Stopped;
    public string? DockerContainerId { get; set; }
    public int MemoryMb { get; set; }
    public double CpuLimit { get; set; }
    public bool AutoStart { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ServerPort> Ports { get; set; } = [];
    public List<ServerEnvironmentVariable> Environment { get; set; } = [];
    public List<ServerVolume> Volumes { get; set; } = [];
}

public sealed class ServerPort
{
    public int Id { get; set; }
    public Guid ServerId { get; set; }
    public GameServer? Server { get; set; }
    public int ContainerPort { get; set; }
    public int HostPort { get; set; }
    public string Protocol { get; set; } = "tcp";
}

public sealed class ServerEnvironmentVariable
{
    public int Id { get; set; }
    public Guid ServerId { get; set; }
    public GameServer? Server { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class ServerVolume
{
    public int Id { get; set; }
    public Guid ServerId { get; set; }
    public GameServer? Server { get; set; }
    public string HostPath { get; set; } = "";
    public string ContainerPath { get; set; } = "/data";
    public bool ReadOnly { get; set; }
}

public sealed class TemplateStore
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<TemplateStore> _logger;
    private readonly Lazy<List<GameTemplate>> _templates;

    public TemplateStore(IWebHostEnvironment environment, ILogger<TemplateStore> logger)
    {
        _environment = environment;
        _logger = logger;
        _templates = new Lazy<List<GameTemplate>>(LoadTemplates);
    }

    public IReadOnlyList<GameTemplate> All() => _templates.Value;

    public GameTemplate? Find(string id)
    {
        return _templates.Value.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private List<GameTemplate> LoadTemplates()
    {
        var root = Path.Combine(_environment.ContentRootPath, "templates");
        Directory.CreateDirectory(root);
        var result = new List<GameTemplate>();
        foreach (var file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var json = File.ReadAllText(file);
                if (json.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    result.AddRange(JsonSerializer.Deserialize<List<GameTemplate>>(json, JsonDefaults.Options) ?? []);
                }
                else
                {
                    var item = JsonSerializer.Deserialize<GameTemplate>(json, JsonDefaults.Options);
                    if (item is not null)
                    {
                        result.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Template konnte nicht geladen werden: {File}", file);
            }
        }

        return result.OrderBy(x => x.Category).ThenBy(x => x.Name).ToList();
    }
}

public sealed class DockerEngine
{
    private readonly DockerClient _client;
    private readonly ILogger<DockerEngine> _logger;

    public DockerEngine(IConfiguration configuration, ILogger<DockerEngine> logger)
    {
        _logger = logger;
        var endpoint = configuration["Panel:DockerEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "npipe://./pipe/docker_engine"
                : "unix:///var/run/docker.sock";
        }

        _client = new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
    }

    public async Task<DockerStatusDto> GetStatusSummaryAsync(CancellationToken ct)
    {
        try
        {
            var version = await _client.System.GetVersionAsync(ct);
            var info = await _client.System.GetSystemInfoAsync(ct);
            return new DockerStatusDto(true, version.Version, info.OSType, info.Architecture, info.NCPU, info.MemTotal, null);
        }
        catch (Exception ex)
        {
            return new DockerStatusDto(false, null, null, null, 0, 0, ex.Message);
        }
    }

    public async Task<List<object>> ListImagesAsync(CancellationToken ct)
    {
        var images = await _client.Images.ListImagesAsync(new ImagesListParameters { All = true }, ct);
        return images.Select(x => new
        {
            x.ID,
            tags = x.RepoTags ?? [],
            x.Size,
            created = x.Created
        }).Cast<object>().ToList();
    }

    public async Task<List<object>> ListVolumesAsync(CancellationToken ct)
    {
        var volumes = await _client.Volumes.ListAsync(ct);
        return volumes.Volumes.Select(x => new
        {
            x.Name,
            x.Driver,
            x.Mountpoint,
            x.CreatedAt,
            x.Labels
        }).Cast<object>().ToList();
    }

    public async Task<List<object>> ListNetworksAsync(CancellationToken ct)
    {
        var networks = await _client.Networks.ListNetworksAsync(new NetworksListParameters(), ct);
        return networks.Select(x => new
        {
            x.ID,
            x.Name,
            x.Driver,
            x.Scope
        }).Cast<object>().ToList();
    }

    public async Task PullImageAsync(string image, CancellationToken ct)
    {
        var parsed = DockerImageName.Parse(image);
        await _client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = parsed.Repository, Tag = parsed.Tag },
            null,
            new Progress<JSONMessage>(message =>
            {
                if (!string.IsNullOrWhiteSpace(message.ErrorMessage))
                {
                    _logger.LogWarning("Docker pull: {Message}", message.ErrorMessage);
                }
            }),
            ct);
    }

    public Task DeleteImageAsync(string image, bool force, CancellationToken ct)
    {
        return _client.Images.DeleteImageAsync(image, new ImageDeleteParameters { Force = force, NoPrune = false }, ct);
    }

    public async Task EnsureContainerAsync(GameServer server, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(server.DockerContainerId))
        {
            try
            {
                await _client.Containers.InspectContainerAsync(server.DockerContainerId, ct);
                return;
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                server.DockerContainerId = null;
            }
        }

        var create = BuildCreateParameters(server);
        var response = await _client.Containers.CreateContainerAsync(create, ct);
        server.DockerContainerId = response.ID;
        server.Status = ServerStatus.Created;
        server.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task StartAsync(GameServer server, CancellationToken ct)
    {
        await EnsureContainerAsync(server, ct);
        var started = await _client.Containers.StartContainerAsync(server.DockerContainerId, new ContainerStartParameters(), ct);
        server.Status = started ? ServerStatus.Running : server.Status;
        server.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task StopAsync(GameServer server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.DockerContainerId))
        {
            server.Status = ServerStatus.Stopped;
            return;
        }

        try
        {
            await _client.Containers.StopContainerAsync(server.DockerContainerId, new ContainerStopParameters { WaitBeforeKillSeconds = 10u }, ct);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotModified || ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already stopped or missing.
        }

        server.Status = ServerStatus.Stopped;
        server.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task KillAsync(GameServer server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.DockerContainerId))
        {
            return;
        }

        await _client.Containers.KillContainerAsync(server.DockerContainerId, new ContainerKillParameters(), ct);
        server.Status = ServerStatus.Stopped;
        server.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task DeleteContainerAsync(GameServer server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.DockerContainerId))
        {
            return;
        }

        try
        {
            await _client.Containers.RemoveContainerAsync(server.DockerContainerId, new ContainerRemoveParameters
            {
                Force = true,
                RemoveVolumes = false
            }, ct);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone.
        }

        server.DockerContainerId = null;
        server.Status = ServerStatus.Stopped;
        server.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task<(bool Changed, string Status)> TryRefreshStatusAsync(GameServer server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.DockerContainerId))
        {
            return (false, server.Status);
        }

        try
        {
            var inspect = await _client.Containers.InspectContainerAsync(server.DockerContainerId, ct);
            var status = inspect.State?.Status ?? ServerStatus.Missing;
            if (!string.Equals(server.Status, status, StringComparison.OrdinalIgnoreCase))
            {
                server.Status = status;
                server.UpdatedAt = DateTimeOffset.UtcNow;
                return (true, status);
            }
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            server.Status = ServerStatus.Missing;
            server.DockerContainerId = null;
            server.UpdatedAt = DateTimeOffset.UtcNow;
            return (true, ServerStatus.Missing);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status konnte nicht aktualisiert werden.");
        }

        return (false, server.Status);
    }

    public async Task<ResourceSnapshot> TryGetStatsAsync(GameServer server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.DockerContainerId) || server.Status != ServerStatus.Running)
        {
            return ResourceSnapshot.Empty;
        }

        try
        {
            return ResourceSnapshot.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stats konnten nicht gelesen werden.");
            return ResourceSnapshot.Empty;
        }
    }

    public async Task StreamLogsAsync(GameServer server, WebSocket socket, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.DockerContainerId))
        {
            await SendTextAsync(socket, "Container ist noch nicht erstellt.\n", ct);
            return;
        }

        try
        {
            var parameters = new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true,
                Follow = true,
                Timestamps = true,
                Tail = "250"
            };
            using var stream = await _client.Containers.GetContainerLogsAsync(server.DockerContainerId, false, parameters, ct);
            var buffer = new byte[8192];
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
                if (result.EOF)
                {
                    break;
                }

                if (result.Count > 0)
                {
                    await socket.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Text, true, ct);
                }
            }
        }
        catch (Exception ex) when (socket.State == WebSocketState.Open)
        {
            await SendTextAsync(socket, $"Log-Stream beendet: {ex.Message}\n", CancellationToken.None);
        }
    }

    private static Task SendTextAsync(WebSocket socket, string text, CancellationToken ct)
    {
        return socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
    }

    public async Task<string> ExecuteConsoleCommandAsync(GameServer server, string command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Befehl darf nicht leer sein.");
        }

        if (string.IsNullOrWhiteSpace(server.DockerContainerId))
        {
            throw new InvalidOperationException("Container ist noch nicht erstellt.");
        }

        var normalizedCommand = NormalizeConsoleCommand(command);
        var execCommand = BuildConsoleExecCommand(server, normalizedCommand);

        var exec = await _client.Exec.ExecCreateContainerAsync(server.DockerContainerId, new ContainerExecCreateParameters
        {
            AttachStdout = true,
            AttachStderr = true,
            AttachStdin = false,
            Tty = false,
            Cmd = execCommand
        }, ct);

        using var stream = await _client.Exec.StartAndAttachContainerExecAsync(exec.ID, false, ct);
        using var stdout = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
            if (result.EOF)
            {
                break;
            }

            if (result.Count > 0)
            {
                stdout.Write(buffer, 0, result.Count);
            }
        }

        var output = Encoding.UTF8.GetString(stdout.ToArray());
        if (string.IsNullOrWhiteSpace(output) && IsMinecraftServerImage(server))
        {
            return $"Minecraft-Befehl gesendet: {normalizedCommand}\n";
        }

        return output;
    }

    private static IList<string> BuildConsoleExecCommand(GameServer server, string normalizedCommand)
    {
        if (IsMinecraftServerImage(server))
        {
            return new[] { "/bin/sh", "-lc", $"mc-send-to-console {ShellEscape(normalizedCommand)}" };
        }

        return new[] { "/bin/sh", "-lc", normalizedCommand };
    }

    private static bool IsMinecraftServerImage(GameServer server)
    {
        return server.Image.Contains("itzg/minecraft-server", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeConsoleCommand(string command)
    {
        var trimmed = command.Trim();
        return trimmed.StartsWith('/') ? trimmed[1..] : trimmed;
    }

    private static string ShellEscape(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    private static CreateContainerParameters BuildCreateParameters(GameServer server)
    {
        var exposedPorts = server.Ports
            .GroupBy(x => $"{x.ContainerPort}/{x.Protocol}")
            .ToDictionary(x => x.Key, _ => new EmptyStruct());
        IDictionary<string, IList<PortBinding>> portBindings = server.Ports
            .GroupBy(x => $"{x.ContainerPort}/{x.Protocol}")
            .ToDictionary(
                x => x.Key,
                x => (IList<PortBinding>)x.Select(port => new PortBinding { HostIP = "0.0.0.0", HostPort = port.HostPort.ToString() }).ToList());

        var binds = server.Volumes
            .Select(x => $"{Path.GetFullPath(x.HostPath)}:{x.ContainerPath}{(x.ReadOnly ? ":ro" : "")}")
            .ToList();

        return new CreateContainerParameters
        {
            Image = server.Image,
            Name = $"ghp-{SanitizeName(server.Name)}-{server.Id.ToString("N")[..8]}",
            Env = server.Environment.Select(x => $"{x.Key}={x.Value}").ToList(),
            Cmd = string.IsNullOrWhiteSpace(server.StartCommand) ? null : ShellSplit(server.StartCommand),
            ExposedPorts = exposedPorts,
            Labels = new Dictionary<string, string>
            {
                ["gamehostpanel.serverId"] = server.Id.ToString(),
                ["gamehostpanel.name"] = server.Name
            },
            HostConfig = new HostConfig
            {
                Binds = binds,
                PortBindings = portBindings,
                Memory = server.MemoryMb > 0 ? server.MemoryMb * 1024L * 1024L : 0,
                NanoCPUs = server.CpuLimit > 0 ? (long)(server.CpuLimit * 1_000_000_000L) : 0,
                RestartPolicy = new RestartPolicy { Name = server.AutoStart ? RestartPolicyKind.UnlessStopped : RestartPolicyKind.No }
            }
        };
    }

    private static IList<string> ShellSplit(string command)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        foreach (var ch in command)
        {
            if ((ch == '"' || ch == '\'') && quote == '\0')
            {
                quote = ch;
                continue;
            }

            if (ch == quote)
            {
                quote = '\0';
                continue;
            }

            if (char.IsWhiteSpace(ch) && quote == '\0')
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    private static string SanitizeName(string name)
    {
        var chars = name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        return string.Join("", chars).Trim('-');
    }
}

public sealed class FileManagerService(PanelPaths paths)
{
    private const long MaxTextFileBytes = 5 * 1024 * 1024;

    public Task<List<FileEntryDto>> ListAsync(GameServer server, string relativePath, CancellationToken ct)
    {
        var root = ResolveRoot(server);
        var target = ResolvePath(root, relativePath);
        if (!Directory.Exists(target))
        {
            throw new FileNotFoundException("Ordner nicht gefunden.", relativePath);
        }

        var entries = Directory.GetFileSystemEntries(target)
            .Select(path =>
            {
                var info = new FileInfo(path);
                var isDirectory = Directory.Exists(path);
                return new FileEntryDto(
                    Path.GetFileName(path),
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    isDirectory,
                    isDirectory ? 0 : info.Length,
                    isDirectory ? null : info.LastWriteTimeUtc);
            })
            .OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult(entries);
    }

    public async Task<FileContentDto> ReadTextAsync(GameServer server, string relativePath, CancellationToken ct)
    {
        var file = ResolveFile(server, relativePath);
        var info = new FileInfo(file);
        if (info.Length > MaxTextFileBytes)
        {
            throw new InvalidOperationException("Datei ist zu groß für den Editor.");
        }

        return new FileContentDto(relativePath, await File.ReadAllTextAsync(file, Encoding.UTF8, ct), info.LastWriteTimeUtc);
    }

    public async Task WriteTextAsync(GameServer server, string relativePath, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Dateipfad fehlt.");
        }

        var root = ResolveRoot(server);
        var target = ResolvePath(root, relativePath);
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Ungültiger Dateipfad.");
        }

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(target, content, Encoding.UTF8, ct);
    }

    public async Task UploadAsync(GameServer server, string relativeDirectory, IFormFile file, CancellationToken ct)
    {
        var root = ResolveRoot(server);
        var targetDirectory = ResolvePath(root, relativeDirectory);
        Directory.CreateDirectory(targetDirectory);
        var target = ResolvePath(root, Path.Combine(relativeDirectory, Path.GetFileName(file.FileName)));
        await using var stream = File.Create(target);
        await file.CopyToAsync(stream, ct);
    }

    public string ResolveFile(GameServer server, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Dateipfad fehlt.");
        }

        var root = ResolveRoot(server);
        var target = ResolvePath(root, relativePath);
        if (!File.Exists(target))
        {
            throw new FileNotFoundException("Datei nicht gefunden.", relativePath);
        }

        return target;
    }

    public void CreateDirectory(GameServer server, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Ordnerpfad fehlt.");
        }

        var root = ResolveRoot(server);
        Directory.CreateDirectory(ResolvePath(root, relativePath));
    }

    public async Task CreateFileAsync(GameServer server, string relativePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Dateipfad fehlt.");
        }

        var root = ResolveRoot(server);
        var target = ResolvePath(root, relativePath);
        if (Directory.Exists(target))
        {
            throw new InvalidOperationException("Am Ziel existiert bereits ein Ordner.");
        }

        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Ungültiger Dateipfad.");
        }

        Directory.CreateDirectory(directory);
        if (!File.Exists(target))
        {
            await File.WriteAllTextAsync(target, string.Empty, Encoding.UTF8, ct);
        }
    }

    public void DeletePath(GameServer server, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Root-Ordner kann nicht gelöscht werden.");
        }

        var root = ResolveRoot(server);
        var target = ResolvePath(root, relativePath);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
            return;
        }

        if (File.Exists(target))
        {
            File.Delete(target);
        }
    }

    public void MovePath(GameServer server, string sourceRelativePath, string targetRelativePath)
    {
        if (string.IsNullOrWhiteSpace(sourceRelativePath) || string.IsNullOrWhiteSpace(targetRelativePath))
        {
            throw new InvalidOperationException("Quell- und Zielpfad sind erforderlich.");
        }

        var root = ResolveRoot(server);
        var source = ResolvePath(root, sourceRelativePath);
        var target = ResolvePath(root, targetRelativePath);

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Directory.Exists(source))
        {
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new InvalidOperationException("Zielpfad existiert bereits.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(source, target);
            return;
        }

        if (File.Exists(source))
        {
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new InvalidOperationException("Zielpfad existiert bereits.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
            return;
        }

        throw new FileNotFoundException("Quelle nicht gefunden.", sourceRelativePath);
    }

    public void DeleteServerRoot(GameServer server)
    {
        var root = ResolveRoot(server);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private string ResolveRoot(GameServer server)
    {
        var firstWritable = server.Volumes.FirstOrDefault(x => !x.ReadOnly) ?? server.Volumes.FirstOrDefault();
        var root = firstWritable?.HostPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = paths.ServerRoot(server.Id);
        }

        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static string ResolvePath(string root, string relativePath)
    {
        relativePath = (relativePath ?? "").TrimStart('/', '\\');
        var target = Path.GetFullPath(Path.Combine(root, relativePath));
        var normalizedRoot = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!target.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !target.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Pfad liegt außerhalb des Server-Ordners.");
        }

        return target;
    }
}

public sealed class JwtTokenService(IConfiguration configuration)
{
    public LoginResponse CreateLoginResponse(PanelUser user)
    {
        var secret = configuration["Panel:JwtSecret"] ?? "DEV_ONLY_CHANGE_THIS_SECRET_TO_AT_LEAST_32_CHARS";
        if (secret.Length < 32)
        {
            secret = "DEV_ONLY_CHANGE_THIS_SECRET_TO_AT_LEAST_32_CHARS";
        }

        var hours = configuration.GetValue("Panel:TokenHours", 12);
        var expires = DateTimeOffset.UtcNow.AddHours(hours);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role)
        };
        var token = new JwtSecurityToken(
            claims: claims,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256));
        return new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token), expires, new UserSummary(user.Id, user.Username, user.Role, user.CreatedAt));
    }
}

public sealed class ApiKeyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, PanelDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true || !context.Request.Headers.TryGetValue("X-API-Key", out var values))
        {
            await next(context);
            return;
        }

        var rawKey = values.ToString();
        var prefix = rawKey.Length >= 12 ? rawKey[..12] : rawKey;
        var candidates = await db.ApiKeys.Include(x => x.User)
            .Where(x => x.Prefix == prefix && (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow))
            .ToListAsync(context.RequestAborted);
        var apiKey = candidates.FirstOrDefault(x => PasswordHasher.Verify(rawKey, x.KeyHash));
        if (apiKey?.User is not null)
        {
            apiKey.LastUsedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(context.RequestAborted);
            var identity = new ClaimsIdentity("ApiKey");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, apiKey.User.Id.ToString()));
            identity.AddClaim(new Claim(ClaimTypes.Name, apiKey.User.Username));
            identity.AddClaim(new Claim(ClaimTypes.Role, apiKey.User.Role));
            context.User = new ClaimsPrincipal(identity);
        }

        await next(context);
    }
}

public static class PasswordHasher
{
    public static string Hash(string value)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(value, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2_sha256${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string value, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 3 || parts[0] != "pbkdf2_sha256")
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(value, salt, 120_000, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public sealed class ServerAutoStartService(IServiceScopeFactory scopeFactory, ILogger<ServerAutoStartService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
        var docker = scope.ServiceProvider.GetRequiredService<DockerEngine>();
        var servers = await db.Servers
            .Include(x => x.Ports)
            .Include(x => x.Environment)
            .Include(x => x.Volumes)
            .Where(x => x.AutoStart)
            .ToListAsync(stoppingToken);

        foreach (var server in servers)
        {
            try
            {
                await docker.EnsureContainerAsync(server, stoppingToken);
                await docker.StartAsync(server, stoppingToken);
                await db.SaveChangesAsync(stoppingToken);
                logger.LogInformation("Autostart: {Server}", server.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Autostart fehlgeschlagen: {Server}", server.Name);
            }
        }
    }
}

public sealed class DailyBackupService(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    PanelPaths paths,
    ILogger<DailyBackupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = configuration.GetValue("Panel:Backups:Enabled", true);
        if (!enabled)
        {
            logger.LogInformation("Daily backups disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var hour = Math.Clamp(configuration.GetValue("Panel:Backups:Hour", 3), 0, 23);
            var minute = Math.Clamp(configuration.GetValue("Panel:Backups:Minute", 15), 0, 59);
            var nextRun = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0, now.Kind);
            if (nextRun <= now)
            {
                nextRun = nextRun.AddDays(1);
            }

            var delay = nextRun - now;
            logger.LogInformation("Next backup scheduled at {Time}", nextRun);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                RunBackup();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Daily backup failed.");
            }
        }
    }

    private void RunBackup()
    {
        var outputDirConfigured = configuration["Panel:Backups:OutputDirectory"];
        var outputDir = string.IsNullOrWhiteSpace(outputDirConfigured)
            ? Path.Combine(paths.DataRoot, "backups")
            : Path.GetFullPath(Path.IsPathRooted(outputDirConfigured)
                ? outputDirConfigured
                : Path.Combine(environment.ContentRootPath, outputDirConfigured));
        Directory.CreateDirectory(outputDir);

        var keep = Math.Max(2, configuration.GetValue("Panel:Backups:MaxFiles", 2));
        var stamp = DateTime.Now.ToString("yyyy-MM-dd");
        var zipPath = Path.Combine(outputDir, $"gamehostpanel-backup-{stamp}.zip");
        var tempDir = Path.Combine(Path.GetTempPath(), $"ghp-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dataSource = paths.DataRoot;
            if (Directory.Exists(dataSource))
            {
                CopyDirectory(dataSource, Path.Combine(tempDir, "data"));
            }

            var configPath = Path.Combine(environment.ContentRootPath, "config.json");
            if (File.Exists(configPath))
            {
                var configTargetDir = Path.Combine(tempDir, "backend");
                Directory.CreateDirectory(configTargetDir);
                File.Copy(configPath, Path.Combine(configTargetDir, "config.json"), overwrite: true);
            }

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(tempDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            logger.LogInformation("Backup written: {File}", zipPath);

            var oldBackups = Directory.GetFiles(outputDir, "gamehostpanel-backup-*.zip")
                .OrderByDescending(File.GetCreationTimeUtc)
                .Skip(keep)
                .ToList();
            foreach (var old in oldBackups)
            {
                File.Delete(old);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var target = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var targetSub = Path.Combine(destinationDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, targetSub);
        }
    }
}

public static class CliSetup
{
    public static async Task RunAsync(IServiceProvider services, string[] args)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
        await db.Database.EnsureCreatedAsync();

        var username = GetArg(args, "--user") ?? "admin";
        var password = GetArg(args, "--password");
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Write("Admin password: ");
            password = ReadPassword();
            Console.WriteLine();
        }

        if (password.Length < 8)
        {
            Console.WriteLine("Passwort muss mindestens 8 Zeichen haben.");
            return;
        }

        var user = await db.Users.FirstOrDefaultAsync(x => x.Username == username);
        if (user is null)
        {
            db.Users.Add(new PanelUser
            {
                Username = username,
                PasswordHash = PasswordHasher.Hash(password),
                Role = UserRole.Admin
            });
        }
        else
        {
            user.PasswordHash = PasswordHasher.Hash(password);
            user.Role = UserRole.Admin;
        }

        await db.SaveChangesAsync();
        Console.WriteLine($"Admin-User bereit: {username}");
    }

    private static string? GetArg(string[] args, string name)
    {
        var index = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string ReadPassword()
    {
        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace && builder.Length > 0)
            {
                builder.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }

        return builder.ToString();
    }
}

public readonly record struct DockerImageName(string Repository, string Tag)
{
    public static DockerImageName Parse(string image)
    {
        var lastSlash = image.LastIndexOf('/');
        var lastColon = image.LastIndexOf(':');
        if (lastColon > lastSlash)
        {
            return new DockerImageName(image[..lastColon], image[(lastColon + 1)..]);
        }

        return new DockerImageName(image, "latest");
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

public sealed record BootstrapRequest(string Username, string Password);
public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, UserSummary User);
public sealed record UserSummary(int Id, string Username, string Role, DateTimeOffset CreatedAt)
{
    public static object FromClaims(ClaimsPrincipal principal)
    {
        return new
        {
            id = principal.FindFirstValue(ClaimTypes.NameIdentifier),
            username = principal.FindFirstValue(ClaimTypes.Name),
            role = principal.FindFirstValue(ClaimTypes.Role)
        };
    }
}

public sealed record DashboardDto(int TotalServers, int RunningServers, int StoppedServers, List<ServerDto> Running, DockerStatusDto Docker);
public sealed record DockerStatusDto(bool Available, string? Version, string? OsType, string? Architecture, long CpuCount, long MemoryBytes, string? Error);
public sealed record ResourceSnapshot(double CpuPercent, long MemoryBytes, long MemoryLimitBytes, long NetworkRxBytes, long NetworkTxBytes)
{
    public static ResourceSnapshot Empty => new(0, 0, 0, 0, 0);
}

public sealed record ServerDto(
    Guid Id,
    string Name,
    string Description,
    string TemplateId,
    string Game,
    string Image,
    string StartCommand,
    string Status,
    string? DockerContainerId,
    int MemoryMb,
    double CpuLimit,
    bool AutoStart,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<PortDto> Ports,
    Dictionary<string, string> Env,
    List<VolumeDto> Volumes,
    ResourceSnapshot? Stats)
{
    public static ServerDto FromEntity(GameServer server, ResourceSnapshot? stats = null)
    {
        return new ServerDto(
            server.Id,
            server.Name,
            server.Description,
            server.TemplateId,
            server.Game,
            server.Image,
            server.StartCommand,
            server.Status,
            server.DockerContainerId,
            server.MemoryMb,
            server.CpuLimit,
            server.AutoStart,
            server.CreatedAt,
            server.UpdatedAt,
            server.Ports.Select(x => new PortDto(x.ContainerPort, x.HostPort, x.Protocol)).ToList(),
            server.Environment
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase),
            server.Volumes.Select(x => new VolumeDto(x.HostPath, x.ContainerPath, x.ReadOnly)).ToList(),
            stats);
    }
}

public sealed record GameTemplate(
    string Id,
    string Name,
    string Category,
    string Image,
    string Description,
    string? StartCommand,
    Dictionary<string, string> Env,
    List<TemplatePort> Ports,
    List<TemplateVolume> Volumes,
    int RecommendedMemoryMb);

public sealed record TemplatePort(int Container, int Host, string Protocol);
public sealed record TemplateVolume(string ContainerPath, bool ReadOnly, string? HostPath = null);
public sealed record PortDto(int Container, int Host, string Protocol);
public sealed record VolumeDto(string HostPath, string ContainerPath, bool ReadOnly);
public sealed record CreateServerRequest(
    string TemplateId,
    string Name,
    string? Description,
    string? Image,
    string? StartCommand,
    Dictionary<string, string>? Env,
    List<PortDto>? Ports,
    List<TemplateVolume>? Volumes,
    int? MemoryMb,
    double? CpuLimit,
    bool AutoStart,
    bool PullImage,
    bool StartNow);

public sealed record UpdateServerRequest(
    string? Name,
    string? Description,
    string? Image,
    string? StartCommand,
    Dictionary<string, string>? Env,
    List<PortDto>? Ports,
    int? MemoryMb,
    double? CpuLimit,
    bool? AutoStart,
    bool RecreateContainer);

public sealed record FileEntryDto(string Name, string Path, bool IsDirectory, long Size, DateTime? ModifiedAt);
public sealed record FileContentDto(string Path, string Content, DateTime ModifiedAt);
public sealed record FileWriteRequest(string Content);
public sealed record FilePathRequest(string Path);
public sealed record FileMoveRequest(string SourcePath, string TargetPath);
public sealed record ConsoleCommandRequest(string Command);
public sealed record ConsoleCommandResponse(string Output);
public sealed record ImagePullRequest(string Image);
public sealed record CreateUserRequest(string Username, string Password, string Role);
public sealed record CreateApiKeyRequest(string Name, DateTimeOffset? ExpiresAt);
public sealed record ApiKeySummary(int Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? ExpiresAt);
