using System.Net.Mail;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using BCrypt.Net;
using Microsoft.AspNetCore.StaticFiles;
using Npgsql;
using NpgsqlTypes;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var configuredFrontendOrigin =
    Environment.GetEnvironmentVariable("NETLIFY_SITE_URL") ??
    Environment.GetEnvironmentVariable("FRONTEND_ORIGIN") ??
    builder.Configuration["Frontend:Origin"];

var allowedOrigins = BuildAllowedOrigins(configuredFrontendOrigin);
var allowNetlifyFallbackOrigin = string.IsNullOrWhiteSpace(configuredFrontendOrigin);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFlutterApp", policy =>
    {
        policy.SetIsOriginAllowed(origin => IsAllowedOrigin(origin, allowedOrigins, allowNetlifyFallbackOrigin))
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var connectionString = Environment.GetEnvironmentVariable("SUPABASE_DB_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("SUPABASE_DB_CONNECTION environment variable is missing.");
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".exe"] = "application/octet-stream";

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypeProvider,
    OnPrepareResponse = context =>
    {
        if (context.Context.Request.Path.StartsWithSegments("/downloads"))
        {
            var fileName = Path.GetFileName(context.File.Name);
            context.Context.Response.Headers.ContentDisposition =
                $"attachment; filename=\"{fileName}\"";
        }
    },
});

app.UseCors("AllowFlutterApp");

app.MapGet("/", () => "Fix My Device API is running");

app.MapPost("/api/auth/register", async (RegisterRequest request) =>
{
    if (!TryValidateRegistration(request, out var validationMessage))
    {
        return Results.BadRequest(new { message = validationMessage });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var normalizedEmail = NormalizeEmail(request.Email!);

        await using var existsCommand = new NpgsqlCommand(
            """
            select 1
            from app_users
            where lower(email) = @email
            limit 1
            """,
            connection);
        existsCommand.Parameters.AddWithValue("email", normalizedEmail);

        var existingUser = await existsCommand.ExecuteScalarAsync();
        if (existingUser is not null)
        {
            return Results.Conflict(new { message = "User already exists." });
        }

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password!.Trim());
        var token = Guid.NewGuid().ToString("N");
        var agentSetupCode = GenerateAgentSetupCode();
        var createdAt = DateTimeOffset.UtcNow;

        await using var insertCommand = new NpgsqlCommand(
            """
            insert into app_users (
                id,
                email,
                password_hash,
                token,
                agent_setup_code,
                created_at
            )
            values (
                @id,
                @email,
                @password_hash,
                @token,
                @agent_setup_code,
                @created_at
            )
            """,
            connection);

        insertCommand.Parameters.AddWithValue("id", Guid.NewGuid());
        insertCommand.Parameters.AddWithValue("email", normalizedEmail);
        insertCommand.Parameters.AddWithValue("password_hash", passwordHash);
        insertCommand.Parameters.AddWithValue("token", token);
        insertCommand.Parameters.AddWithValue("agent_setup_code", agentSetupCode);
        insertCommand.Parameters.AddWithValue("created_at", createdAt);

        await insertCommand.ExecuteNonQueryAsync();

        return Results.Ok(new
        {
            message = "User registered successfully",
            token,
            email = normalizedEmail,
            agentSetupCode,
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine("Register failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Unable to register right now." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/auth/login", async (LoginRequest request) =>
{
    if (!TryValidateLogin(request, out var validationMessage))
    {
        return Results.BadRequest(new { message = validationMessage });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var normalizedEmail = NormalizeEmail(request.Email!);

        await using var selectCommand = new NpgsqlCommand(
            """
            select id, email, password_hash, token, agent_setup_code
            from app_users
            where lower(email) = @email
            limit 1
            """,
            connection);

        selectCommand.Parameters.AddWithValue("email", normalizedEmail);

        await using var reader = await selectCommand.ExecuteReaderAsync(CommandBehavior.SingleRow);
        if (!await reader.ReadAsync())
        {
            return Results.Unauthorized();
        }

        var userId = reader.GetGuid(reader.GetOrdinal("id"));
        var email = reader["email"]?.ToString() ?? normalizedEmail;
        var storedHash = reader["password_hash"]?.ToString() ?? string.Empty;
        var token = reader["token"]?.ToString() ?? string.Empty;
        var agentSetupCode = reader["agent_setup_code"]?.ToString() ?? string.Empty;
        await reader.CloseAsync();

        if (string.IsNullOrWhiteSpace(storedHash) ||
            !BCrypt.Net.BCrypt.Verify(request.Password!.Trim(), storedHash))
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            token = Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(agentSetupCode))
        {
            agentSetupCode = GenerateAgentSetupCode();
        }

        await using var updateCommand = new NpgsqlCommand(
            """
            update app_users
            set token = @token,
                agent_setup_code = @agent_setup_code
            where id = @id
            """,
            connection);

        updateCommand.Parameters.AddWithValue("id", userId);
        updateCommand.Parameters.AddWithValue("token", token);
        updateCommand.Parameters.AddWithValue("agent_setup_code", agentSetupCode);
        await updateCommand.ExecuteNonQueryAsync();

        return Results.Ok(new
        {
            token,
            email,
            agentSetupCode,
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine("Login failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Unable to sign in right now." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/agent/setup-code", async (HttpRequest request) =>
{
    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetAuthorizedUserAsync(request, connection);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var agentSetupCode = string.IsNullOrWhiteSpace(user.AgentSetupCode)
            ? GenerateAgentSetupCode()
            : user.AgentSetupCode;

        if (!string.Equals(agentSetupCode, user.AgentSetupCode, StringComparison.Ordinal))
        {
            await using var updateCommand = new NpgsqlCommand(
                """
                update app_users
                set agent_setup_code = @agent_setup_code
                where id = @id
                """,
                connection);
            updateCommand.Parameters.AddWithValue("id", user.Id);
            updateCommand.Parameters.AddWithValue("agent_setup_code", agentSetupCode);
            await updateCommand.ExecuteNonQueryAsync();
        }

        return Results.Ok(new { agentSetupCode });
    }
    catch (Exception ex)
    {
        Console.WriteLine("Get setup code failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Unable to load setup code right now." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/devices", async (HttpRequest request) =>
{
    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetAuthorizedUserAsync(request, connection);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var devices = new List<DeviceRecord>();

        await using var command = new NpgsqlCommand(
            """
            select
                id,
                device_name,
                processor,
                processor_speed,
                installed_ram,
                usable_ram,
                graphics_card,
                graphics_memory,
                total_storage,
                used_storage,
                free_storage,
                device_id,
                product_id,
                system_type,
                windows_edition,
                windows_version,
                os_build,
                installed_on,
                status,
                last_seen_at,
                drives_json
            from devices
            where user_id = @user_id
            order by last_seen_at desc nulls last
            """,
            connection);
        command.Parameters.AddWithValue("user_id", user.Id);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            devices.Add(new DeviceRecord(
                reader["id"]?.ToString() ?? string.Empty,
                reader["device_name"]?.ToString() ?? "Unknown",
                reader["processor"]?.ToString() ?? "Unknown",
                reader["processor_speed"]?.ToString() ?? "Unknown",
                reader["installed_ram"]?.ToString() ?? "Unknown",
                reader["usable_ram"]?.ToString() ?? "Unknown",
                reader["graphics_card"]?.ToString() ?? "Unknown",
                reader["graphics_memory"]?.ToString() ?? "Unknown",
                reader["total_storage"]?.ToString() ?? "Unknown",
                reader["used_storage"]?.ToString() ?? "Unknown",
                reader["free_storage"]?.ToString() ?? "Unknown",
                reader["device_id"]?.ToString() ?? "Unknown",
                reader["product_id"]?.ToString() ?? "Unknown",
                reader["system_type"]?.ToString() ?? "Unknown",
                reader["windows_edition"]?.ToString() ?? "Unknown",
                reader["windows_version"]?.ToString() ?? "Unknown",
                reader["os_build"]?.ToString() ?? "Unknown",
                reader["installed_on"]?.ToString() ?? "Unknown",
                reader["status"]?.ToString() ?? "Online",
                ToIsoString(reader["last_seen_at"]),
                DeserializeDrives(reader["drives_json"]?.ToString())));
        }

        return Results.Ok(devices);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Get devices failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Unable to load devices right now." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/devices/system-info-by-code", async (DeviceSystemInfoRequest request) =>
{
    if (request is null)
    {
        return Results.BadRequest(new { message = "Device payload is required." });
    }

    var setupCode = NormalizeSetupCode(request.SetupCode ?? request.AgentSetupCode);
    if (!IsValidSetupCode(setupCode))
    {
        return Results.BadRequest(new { message = "Agent setup code format is invalid." });
    }

    if (!TryValidateDevicePayload(request, out var validationMessage))
    {
        return Results.BadRequest(new { message = validationMessage });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var userCommand = new NpgsqlCommand(
            """
            select id
            from app_users
            where upper(agent_setup_code) = @setup_code
            limit 1
            """,
            connection);
        userCommand.Parameters.AddWithValue("setup_code", setupCode);

        var userResult = await userCommand.ExecuteScalarAsync();
        if (userResult is not Guid userId)
        {
            return Results.Unauthorized();
        }

        var normalizedDeviceId = NormalizeOptionalValue(request.DeviceId, 128);
        var drives = NormalizeDrives(request.Drives);
        var drivesJson = JsonSerializer.Serialize(drives);
        var now = DateTimeOffset.UtcNow;
        var status = NormalizeStatus(request.Status);

        await using var existingDeviceCommand = new NpgsqlCommand(
            """
            select id
            from devices
            where user_id = @user_id
              and device_id = @device_id
            limit 1
            """,
            connection);
        existingDeviceCommand.Parameters.AddWithValue("user_id", userId);
        existingDeviceCommand.Parameters.AddWithValue("device_id", normalizedDeviceId);

        var existingDeviceResult = await existingDeviceCommand.ExecuteScalarAsync();

        if (existingDeviceResult is Guid existingDeviceId)
        {
            await using var updateCommand = new NpgsqlCommand(
                """
                update devices
                set device_name = @device_name,
                    processor = @processor,
                    processor_speed = @processor_speed,
                    installed_ram = @installed_ram,
                    usable_ram = @usable_ram,
                    graphics_card = @graphics_card,
                    graphics_memory = @graphics_memory,
                    total_storage = @total_storage,
                    used_storage = @used_storage,
                    free_storage = @free_storage,
                    device_id = @device_id,
                    product_id = @product_id,
                    system_type = @system_type,
                    windows_edition = @windows_edition,
                    windows_version = @windows_version,
                    os_build = @os_build,
                    installed_on = @installed_on,
                    status = @status,
                    last_seen_at = @last_seen_at,
                    drives_json = @drives_json,
                    updated_at = @updated_at
                where id = @id
                  and user_id = @user_id
                """,
                connection);

            AddDeviceParameters(
                updateCommand,
                existingDeviceId,
                userId,
                request,
                normalizedDeviceId,
                status,
                now,
                drivesJson,
                includeCreatedAt: false);

            await updateCommand.ExecuteNonQueryAsync();
        }
        else
        {
            await using var insertCommand = new NpgsqlCommand(
                """
                insert into devices (
                    id,
                    user_id,
                    device_name,
                    processor,
                    processor_speed,
                    installed_ram,
                    usable_ram,
                    graphics_card,
                    graphics_memory,
                    total_storage,
                    used_storage,
                    free_storage,
                    device_id,
                    product_id,
                    system_type,
                    windows_edition,
                    windows_version,
                    os_build,
                    installed_on,
                    status,
                    last_seen_at,
                    drives_json,
                    created_at,
                    updated_at
                )
                values (
                    @id,
                    @user_id,
                    @device_name,
                    @processor,
                    @processor_speed,
                    @installed_ram,
                    @usable_ram,
                    @graphics_card,
                    @graphics_memory,
                    @total_storage,
                    @used_storage,
                    @free_storage,
                    @device_id,
                    @product_id,
                    @system_type,
                    @windows_edition,
                    @windows_version,
                    @os_build,
                    @installed_on,
                    @status,
                    @last_seen_at,
                    @drives_json,
                    @created_at,
                    @updated_at
                )
                """,
                connection);

            AddDeviceParameters(
                insertCommand,
                Guid.NewGuid(),
                userId,
                request,
                normalizedDeviceId,
                status,
                now,
                drivesJson,
                includeCreatedAt: true);

            await insertCommand.ExecuteNonQueryAsync();
        }

        return Results.Ok(new { message = "System info saved successfully" });
    }
    catch (Exception ex)
    {
        Console.WriteLine("System info save failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Failed to save device info." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Run();

static HashSet<string> BuildAllowedOrigins(string? configuredFrontendOrigin)
{
    var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "http://localhost:3000",
        "http://localhost:4200",
        "http://localhost:5055",
        "http://localhost:8080",
        "http://127.0.0.1:3000",
        "http://127.0.0.1:4200",
        "http://127.0.0.1:5055",
        "http://127.0.0.1:8080",
    };

    if (Uri.TryCreate(configuredFrontendOrigin, UriKind.Absolute, out var frontendUri) &&
        (frontendUri.Scheme == Uri.UriSchemeHttps || frontendUri.Scheme == Uri.UriSchemeHttp))
    {
        origins.Add(frontendUri.GetLeftPart(UriPartial.Authority));
    }

    return origins;
}

static bool IsAllowedOrigin(string? origin, HashSet<string> allowedOrigins, bool allowNetlifyFallbackOrigin)
{
    if (string.IsNullOrWhiteSpace(origin))
    {
        return false;
    }

    if (allowedOrigins.Contains(origin))
    {
        return true;
    }

    if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
    {
        return false;
    }

    if (originUri.IsLoopback)
    {
        return originUri.Scheme == Uri.UriSchemeHttp || originUri.Scheme == Uri.UriSchemeHttps;
    }

    if (allowNetlifyFallbackOrigin &&
        originUri.Scheme == Uri.UriSchemeHttps &&
        originUri.Host.EndsWith(".netlify.app", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return false;
}

static async Task<AppUserRecord?> TryGetAuthorizedUserAsync(HttpRequest request, NpgsqlConnection connection)
{
    if (!request.Headers.TryGetValue("Authorization", out var authorizationHeader))
    {
        return null;
    }

    var headerValue = authorizationHeader.ToString();
    if (string.IsNullOrWhiteSpace(headerValue) ||
        !headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    var token = headerValue[7..].Trim();
    if (!Regex.IsMatch(token, "^[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant))
    {
        return null;
    }

    await using var command = new NpgsqlCommand(
        """
        select id, email, token, agent_setup_code
        from app_users
        where token = @token
        limit 1
        """,
        connection);
    command.Parameters.AddWithValue("token", token);

    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
    return await reader.ReadAsync()
        ? new AppUserRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader["email"]?.ToString() ?? string.Empty,
            reader["token"]?.ToString() ?? string.Empty,
            reader["agent_setup_code"]?.ToString() ?? string.Empty)
        : null;
}

static void AddDeviceParameters(
    NpgsqlCommand command,
    Guid deviceId,
    Guid userId,
    DeviceSystemInfoRequest request,
    string normalizedDeviceId,
    string status,
    DateTimeOffset now,
    string drivesJson,
    bool includeCreatedAt)
{
    command.Parameters.AddWithValue("id", deviceId);
    command.Parameters.AddWithValue("user_id", userId);
    command.Parameters.AddWithValue("device_name", NormalizeOptionalValue(request.DeviceName, 200));
    command.Parameters.AddWithValue("processor", NormalizeOptionalValue(request.Processor, 200));
    command.Parameters.AddWithValue("processor_speed", NormalizeOptionalValue(request.ProcessorSpeed, 100));
    command.Parameters.AddWithValue("installed_ram", NormalizeOptionalValue(request.InstalledRam, 100));
    command.Parameters.AddWithValue("usable_ram", NormalizeOptionalValue(request.UsableRam, 100));
    command.Parameters.AddWithValue("graphics_card", NormalizeOptionalValue(request.GraphicsCard, 200));
    command.Parameters.AddWithValue("graphics_memory", NormalizeOptionalValue(request.GraphicsMemory, 100));
    command.Parameters.AddWithValue("total_storage", NormalizeOptionalValue(request.TotalStorage, 100));
    command.Parameters.AddWithValue("used_storage", NormalizeOptionalValue(request.UsedStorage, 100));
    command.Parameters.AddWithValue("free_storage", NormalizeOptionalValue(request.FreeStorage, 100));
    command.Parameters.AddWithValue("device_id", normalizedDeviceId);
    command.Parameters.AddWithValue("product_id", NormalizeOptionalValue(request.ProductId, 128));
    command.Parameters.AddWithValue("system_type", NormalizeOptionalValue(request.SystemType, 100));
    command.Parameters.AddWithValue("windows_edition", NormalizeOptionalValue(request.WindowsEdition, 100));
    command.Parameters.AddWithValue("windows_version", NormalizeOptionalValue(request.WindowsVersion, 100));
    command.Parameters.AddWithValue("os_build", NormalizeOptionalValue(request.OsBuild, 100));
    command.Parameters.AddWithValue("installed_on", NormalizeOptionalValue(request.InstalledOn, 100));
    command.Parameters.AddWithValue("status", status);
    command.Parameters.AddWithValue("last_seen_at", now);
    command.Parameters.Add(new NpgsqlParameter("drives_json", NpgsqlDbType.Jsonb) { Value = drivesJson });
    command.Parameters.AddWithValue("updated_at", now);

    if (includeCreatedAt)
    {
        command.Parameters.AddWithValue("created_at", now);
    }
}

static bool TryValidateRegistration(RegisterRequest request, out string message)
{
    if (!IsValidEmail(request.Email))
    {
        message = "Enter a valid email address.";
        return false;
    }

    if (!IsValidPassword(request.Password))
    {
        message = "Password must be 8-128 characters and include a letter and a number.";
        return false;
    }

    message = string.Empty;
    return true;
}

static bool TryValidateLogin(LoginRequest request, out string message)
{
    if (!IsValidEmail(request.Email))
    {
        message = "Enter a valid email address.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(request.Password))
    {
        message = "Password is required.";
        return false;
    }

    message = string.Empty;
    return true;
}

static bool TryValidateDevicePayload(DeviceSystemInfoRequest request, out string message)
{
    if (!ValidateRequiredField("Device name", request.DeviceName, 200, out message) ||
        !ValidateRequiredField("Device ID", request.DeviceId, 128, out message) ||
        !ValidateOptionalField("Processor", request.Processor, 200, out message) ||
        !ValidateOptionalField("Processor speed", request.ProcessorSpeed, 100, out message) ||
        !ValidateOptionalField("Installed RAM", request.InstalledRam, 100, out message) ||
        !ValidateOptionalField("Usable RAM", request.UsableRam, 100, out message) ||
        !ValidateOptionalField("Graphics card", request.GraphicsCard, 200, out message) ||
        !ValidateOptionalField("Graphics memory", request.GraphicsMemory, 100, out message) ||
        !ValidateOptionalField("Total storage", request.TotalStorage, 100, out message) ||
        !ValidateOptionalField("Used storage", request.UsedStorage, 100, out message) ||
        !ValidateOptionalField("Free storage", request.FreeStorage, 100, out message) ||
        !ValidateOptionalField("Product ID", request.ProductId, 128, out message) ||
        !ValidateOptionalField("System type", request.SystemType, 100, out message) ||
        !ValidateOptionalField("Windows edition", request.WindowsEdition, 100, out message) ||
        !ValidateOptionalField("Windows version", request.WindowsVersion, 100, out message) ||
        !ValidateOptionalField("OS build", request.OsBuild, 100, out message) ||
        !ValidateOptionalField("Installed on", request.InstalledOn, 100, out message))
    {
        return false;
    }

    if (request.Drives is { Count: > 32 })
    {
        message = "Too many drives were submitted.";
        return false;
    }

    foreach (var drive in request.Drives ?? new List<DriveInfoRequest>())
    {
        if (!ValidateOptionalField("Drive letter", drive.DriveLetter, 32, out message) ||
            !ValidateOptionalField("Drive type", drive.DriveType, 64, out message) ||
            !ValidateOptionalField("File system", drive.FileSystem, 64, out message) ||
            !ValidateOptionalField("Volume label", drive.VolumeLabel, 128, out message) ||
            !ValidateOptionalField("Drive total size", drive.TotalSize, 100, out message) ||
            !ValidateOptionalField("Drive used space", drive.UsedSpace, 100, out message) ||
            !ValidateOptionalField("Drive free space", drive.FreeSpace, 100, out message))
        {
            return false;
        }
    }

    message = string.Empty;
    return true;
}

static bool ValidateRequiredField(string fieldName, string? value, int maxLength, out string message)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        message = $"{fieldName} is required.";
        return false;
    }

    if (value.Trim().Length > maxLength)
    {
        message = $"{fieldName} is too long.";
        return false;
    }

    message = string.Empty;
    return true;
}

static bool ValidateOptionalField(string fieldName, string? value, int maxLength, out string message)
{
    if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length > maxLength)
    {
        message = $"{fieldName} is too long.";
        return false;
    }

    message = string.Empty;
    return true;
}

static bool IsValidEmail(string? email)
{
    if (string.IsNullOrWhiteSpace(email) || email.Trim().Length > 254)
    {
        return false;
    }

    try
    {
        var parsed = new MailAddress(email.Trim());
        return string.Equals(parsed.Address, email.Trim(), StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}

static bool IsValidPassword(string? password)
{
    if (string.IsNullOrWhiteSpace(password))
    {
        return false;
    }

    var trimmed = password.Trim();
    return trimmed.Length is >= 8 and <= 128 &&
           trimmed.Any(char.IsLetter) &&
           trimmed.Any(char.IsDigit);
}

static List<DriveInfoRequest> DeserializeDrives(string? drivesJson)
{
    if (string.IsNullOrWhiteSpace(drivesJson))
    {
        return new List<DriveInfoRequest>();
    }

    try
    {
        return JsonSerializer.Deserialize<List<DriveInfoRequest>>(drivesJson) ?? new List<DriveInfoRequest>();
    }
    catch
    {
        return new List<DriveInfoRequest>();
    }
}

static List<DriveInfoRequest> NormalizeDrives(List<DriveInfoRequest>? drives)
{
    return (drives ?? new List<DriveInfoRequest>())
        .Select(drive => new DriveInfoRequest(
            NormalizeOptionalValue(drive.DriveLetter, 32),
            NormalizeOptionalValue(drive.DriveType, 64),
            NormalizeOptionalValue(drive.FileSystem, 64),
            NormalizeOptionalValue(drive.VolumeLabel, 128),
            NormalizeOptionalValue(drive.TotalSize, 100),
            NormalizeOptionalValue(drive.UsedSpace, 100),
            NormalizeOptionalValue(drive.FreeSpace, 100)))
        .ToList();
}

static string GenerateAgentSetupCode()
{
    var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    return $"FMD-{raw[..4]}-{raw[4..8]}-{raw[8..12]}-{raw[12..16]}";
}

static bool IsValidSetupCode(string? setupCode)
{
    return !string.IsNullOrWhiteSpace(setupCode) &&
           Regex.IsMatch(
               setupCode,
               "^FMD(?:-[A-Z0-9]{4}){2,4}$",
               RegexOptions.CultureInvariant);
}

static string NormalizeSetupCode(string? setupCode)
{
    return string.IsNullOrWhiteSpace(setupCode)
        ? string.Empty
        : setupCode.Trim().ToUpperInvariant();
}

static string NormalizeStatus(string? status)
{
    var normalized = NormalizeOptionalValue(status, 32);
    return normalized.Equals("offline", StringComparison.OrdinalIgnoreCase)
        ? "Offline"
        : "Online";
}

static string NormalizeOptionalValue(string? value, int maxLength)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "Unknown";
    }

    var trimmed = value.Trim();
    return trimmed.Length <= maxLength
        ? trimmed
        : trimmed[..maxLength];
}

static string ToIsoString(object? value)
{
    return value switch
    {
        null or DBNull => string.Empty,
        DateTimeOffset dto => dto.ToString("O"),
        DateTime dt => dt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("O")
            : dt.ToUniversalTime().ToString("O"),
        _ => value.ToString() ?? string.Empty,
    };
}

static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

record RegisterRequest(string? Email, string? Password);
record LoginRequest(string? Email, string? Password);

record AppUserRecord(
    Guid Id,
    string Email,
    string Token,
    string AgentSetupCode);

record DeviceRecord(
    string Id,
    string DeviceName,
    string Processor,
    string ProcessorSpeed,
    string InstalledRam,
    string UsableRam,
    string GraphicsCard,
    string GraphicsMemory,
    string TotalStorage,
    string UsedStorage,
    string FreeStorage,
    string DeviceId,
    string ProductId,
    string SystemType,
    string WindowsEdition,
    string WindowsVersion,
    string OsBuild,
    string InstalledOn,
    string Status,
    string LastSeenAt,
    List<DriveInfoRequest> Drives);

record DeviceSystemInfoRequest(
    string? SetupCode,
    string? AgentSetupCode,
    string? DeviceName,
    string? Processor,
    string? ProcessorSpeed,
    string? InstalledRam,
    string? UsableRam,
    string? GraphicsCard,
    string? GraphicsMemory,
    string? TotalStorage,
    string? UsedStorage,
    string? FreeStorage,
    string? DeviceId,
    string? ProductId,
    string? SystemType,
    string? WindowsEdition,
    string? WindowsVersion,
    string? OsBuild,
    string? InstalledOn,
    string? Status,
    string? LastSeenAt,
    List<DriveInfoRequest>? Drives);

record DriveInfoRequest(
    string? DriveLetter,
    string? DriveType,
    string? FileSystem,
    string? VolumeLabel,
    string? TotalSize,
    string? UsedSpace,
    string? FreeSpace);
