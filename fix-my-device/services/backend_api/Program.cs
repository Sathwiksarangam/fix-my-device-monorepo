using System.Data;
using System.Net.Mail;
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

var transferStorageRoot = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "transfer-storage");
Directory.CreateDirectory(transferStorageRoot);
var agentInstallerExternalUrl = Environment.GetEnvironmentVariable("AGENT_INSTALLER_EXTERNAL_URL");
var agentInstallerPath = Path.Combine(builder.Environment.ContentRootPath, "downloads", "FixMyDeviceSetup.exe");
await EnsureTransferTablesAsync(connectionString);
await EnsureRecoveryFileListingColumnsAsync(connectionString);
await EnsureRecoverySettingsColumnsAsync(connectionString);

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

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

app.MapGet("/downloads/FixMyDeviceSetup.exe", () =>
{
    if (File.Exists(agentInstallerPath))
    {
        return Results.File(
            agentInstallerPath,
            "application/octet-stream",
            "FixMyDeviceSetup.exe");
    }

    if (!string.IsNullOrWhiteSpace(agentInstallerExternalUrl))
    {
        return Results.Redirect(agentInstallerExternalUrl, permanent: false);
    }

    return Results.Text(
        "FixMyDeviceSetup.exe is not available on this server yet. Configure AGENT_INSTALLER_EXTERNAL_URL or place the installer in the backend downloads folder.",
        "text/plain",
        statusCode: StatusCodes.Status404NotFound);
});

app.MapGet("/api/debug/db-check", async () =>
{
    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var usersCommand = new NpgsqlCommand("select count(*) from app_users", connection);
        await using var devicesCommand = new NpgsqlCommand("select count(*) from devices", connection);

        var users = Convert.ToInt32(await usersCommand.ExecuteScalarAsync() ?? 0);
        var devices = Convert.ToInt32(await devicesCommand.ExecuteScalarAsync() ?? 0);

        return Results.Ok(new
        {
            database = "ok",
            users,
            devices,
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return Results.Json(
            new
            {
                message = "Database debug check failed",
                error = ex.Message,
                details = ex.ToString(),
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/debug/setup-code/{code}", async (string code) =>
{
    try
    {
        var normalizedCode = NormalizeSetupCode(code);
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return Results.BadRequest(new { message = "Setup code is required." });
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select id, email, agent_setup_code
            from app_users
            where agent_setup_code = @setup_code
            limit 1
            """,
            connection);
        command.Parameters.AddWithValue("setup_code", normalizedCode);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
        if (!await reader.ReadAsync())
        {
            return Results.Json(
                new
                {
                    exists = false,
                    setupCode = normalizedCode,
                },
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new
        {
            exists = true,
            setupCode = normalizedCode,
            userId = reader["id"]?.ToString() ?? string.Empty,
            email = reader["email"]?.ToString() ?? string.Empty,
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return Results.Json(
            new
            {
                message = "Setup code debug check failed",
                error = ex.Message,
                details = ex.ToString(),
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

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
    try
    {
        Console.WriteLine("Device sync request received");
        Console.WriteLine(JsonSerializer.Serialize(request));

        if (request is null)
        {
            return Results.BadRequest(new { message = "Device payload is required." });
        }

        var rawSetupCode = request.SetupCode ?? request.AgentSetupCode;
        if (string.IsNullOrWhiteSpace(rawSetupCode))
        {
            return Results.BadRequest(new { message = "Setup code is required." });
        }

        var setupCode = NormalizeSetupCode(rawSetupCode);
        if (!IsValidSetupCode(setupCode))
        {
            return Results.Json(
                new { message = "Setup code not found." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!TryValidateDevicePayload(request, out var validationMessage))
        {
            return Results.BadRequest(new { message = validationMessage });
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var userCommand = new NpgsqlCommand(
            """
            select id
            from app_users
            where agent_setup_code = @setup_code
            limit 1
            """,
            connection);
        userCommand.Parameters.AddWithValue("setup_code", setupCode);

        var userResult = await userCommand.ExecuteScalarAsync();
        if (userResult is not Guid userId)
        {
            return Results.Json(
                new { message = "Setup code not found." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var normalizedDeviceId = NormalizeOptionalValue(request.DeviceId, 128);
        var drives = NormalizeDrives(request.Drives);
        var drivesJson = JsonSerializer.Serialize(drives);
        var now = DateTimeOffset.UtcNow;
        var status = NormalizeStatus(request.Status);

        await using var upsertCommand = new NpgsqlCommand(
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
            on conflict (user_id, device_id)
            do update set
                device_name = excluded.device_name,
                processor = excluded.processor,
                processor_speed = excluded.processor_speed,
                installed_ram = excluded.installed_ram,
                usable_ram = excluded.usable_ram,
                graphics_card = excluded.graphics_card,
                graphics_memory = excluded.graphics_memory,
                total_storage = excluded.total_storage,
                used_storage = excluded.used_storage,
                free_storage = excluded.free_storage,
                product_id = excluded.product_id,
                system_type = excluded.system_type,
                windows_edition = excluded.windows_edition,
                windows_version = excluded.windows_version,
                os_build = excluded.os_build,
                installed_on = excluded.installed_on,
                status = excluded.status,
                last_seen_at = excluded.last_seen_at,
                drives_json = excluded.drives_json,
                updated_at = excluded.updated_at
            """,
            connection);

        AddDeviceParameters(
            upsertCommand,
            Guid.NewGuid(),
            userId,
            request,
            normalizedDeviceId,
            status,
            now,
            drivesJson,
            includeCreatedAt: true);

        await upsertCommand.ExecuteNonQueryAsync();

        return Results.Ok(new { message = "System info saved successfully" });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return Results.Json(
            new
            {
                message = "Device sync failed",
                error = ex.Message,
                details = ex.ToString(),
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/recovery/settings", async (HttpRequest httpRequest, RecoverySettingsRequest request) =>
{
    if (request is null)
    {
        return Results.BadRequest(new { message = "Recovery settings payload is required." });
    }

    var setupCode = NormalizeSetupCode(request.SetupCode ?? request.AgentSetupCode);
    var hasSetupCode = !string.IsNullOrWhiteSpace(setupCode);
    if (hasSetupCode && !IsValidSetupCode(setupCode))
    {
        return Results.BadRequest(new { message = "Agent setup code format is invalid." });
    }

    if (!TryValidateRecoverySettingsRequest(request, out var validationMessage))
    {
        return Results.BadRequest(new { message = validationMessage });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetAuthorizedUserAsync(httpRequest, connection);
        if (user is null && hasSetupCode)
        {
            user = await TryGetUserBySetupCodeAsync(connection, setupCode);
        }

        if (user is null)
        {
            return Results.Unauthorized();
        }

        var device = await TryResolveRecoveryDeviceAsync(
            connection,
            user.Id,
            NormalizeOptionalValueOrEmpty(request.DeviceId, 128));
        device ??= await TryGetLatestOwnedDeviceAsync(
            connection,
            user.Id,
            NormalizeOptionalValueOrEmpty(request.DeviceName, 200));
        if (device is null)
        {
            return Results.BadRequest(new { message = "Device must be connected before recovery can be enabled." });
        }

        var approvedLocations = FilterSupportedApprovedLocations(request.ApprovedLocations);
        if (request.Enabled && approvedLocations.Count == 0)
        {
            return Results.BadRequest(new { message = "No supported recovery locations were submitted." });
        }

        var approvedLocationsJson = JsonSerializer.Serialize(approvedLocations);
        var now = DateTimeOffset.UtcNow;
        var scanRequestedAt = now;

        await using var upsertCommand = new NpgsqlCommand(
            """
            insert into recovery_settings (
                id,
                user_id,
                device_id,
                device_name,
                enabled,
                approved_locations,
                last_synced_at,
                settings_updated_at,
                scan_requested_at,
                created_at,
                updated_at
            )
            values (
                @id,
                @user_id,
                @device_id,
                @device_name,
                @enabled,
                @approved_locations,
                @last_synced_at,
                @settings_updated_at,
                @scan_requested_at,
                @created_at,
                @updated_at
            )
            on conflict (user_id, device_id)
            do update set
                device_name = excluded.device_name,
                enabled = excluded.enabled,
                approved_locations = excluded.approved_locations,
                settings_updated_at = excluded.settings_updated_at,
                scan_requested_at = excluded.scan_requested_at,
                updated_at = excluded.updated_at
            """,
            connection);

        upsertCommand.Parameters.AddWithValue("id", Guid.NewGuid());
        upsertCommand.Parameters.AddWithValue("user_id", user.Id);
        upsertCommand.Parameters.AddWithValue("device_id", device.Id);
        upsertCommand.Parameters.AddWithValue("device_name", NormalizeOptionalValue(request.DeviceName, 200));
        upsertCommand.Parameters.AddWithValue("enabled", request.Enabled);
        upsertCommand.Parameters.Add(new NpgsqlParameter("approved_locations", NpgsqlDbType.Jsonb) { Value = approvedLocationsJson });
        upsertCommand.Parameters.AddWithValue("last_synced_at", DBNull.Value);
        upsertCommand.Parameters.AddWithValue("settings_updated_at", now);
        upsertCommand.Parameters.AddWithValue("scan_requested_at", scanRequestedAt);
        upsertCommand.Parameters.AddWithValue("created_at", now);
        upsertCommand.Parameters.AddWithValue("updated_at", now);
        await upsertCommand.ExecuteNonQueryAsync();

        return Results.Ok(new
        {
            message = "Emergency recovery settings saved successfully.",
            enabled = request.Enabled,
            approvedLocations,
            scanRequestedAt,
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine("Recovery settings save failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Unable to save emergency recovery settings right now." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/recovery/settings", async (HttpRequest request) =>
{
    var deviceIdValue = request.Query["deviceId"].ToString();
    if (!Guid.TryParse(deviceIdValue, out var deviceId))
    {
        return Results.BadRequest(new { message = "A valid deviceId is required." });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetAuthorizedUserAsync(request, connection);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var device = await TryGetOwnedDeviceByIdAsync(connection, user.Id, deviceId);
        if (device is null)
        {
            return Results.NotFound(new { message = "Device not found." });
        }

        await using var settingsCommand = new NpgsqlCommand(
            """
            select enabled, approved_locations, last_synced_at
                 , settings_updated_at, scan_requested_at
            from recovery_settings
            where user_id = @user_id
              and device_id = @device_id
            limit 1
            """,
            connection);
        settingsCommand.Parameters.AddWithValue("user_id", user.Id);
        settingsCommand.Parameters.AddWithValue("device_id", device.Id);

        await using var reader = await settingsCommand.ExecuteReaderAsync(CommandBehavior.SingleRow);
        if (!await reader.ReadAsync())
        {
            return Results.Ok(new RecoverySettingsResponse(
                device.Id.ToString(),
                device.DeviceName,
                false,
                new List<RecoveryApprovedLocationRecord>(),
                string.Empty,
                string.Empty,
                string.Empty));
        }

        var enabled = reader["enabled"] is bool enabledValue && enabledValue;
        var approvedLocations = DeserializeApprovedLocations(reader["approved_locations"]?.ToString());
        var lastSyncedAt = ToIsoString(reader["last_synced_at"]);
        var updatedAt = ToIsoString(reader["settings_updated_at"]);
        var scanRequestedAt = ToIsoString(reader["scan_requested_at"]);

        return Results.Ok(new RecoverySettingsResponse(
            device.Id.ToString(),
            device.DeviceName,
            enabled,
            approvedLocations,
            lastSyncedAt,
            updatedAt,
            scanRequestedAt));
    }
    catch (Exception ex)
    {
        Console.WriteLine("Get recovery settings failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Unable to load emergency recovery settings right now." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/agent/recovery/settings", async (AgentRecoverySettingsRequest request) =>
{
    var setupCode = NormalizeSetupCode(request.SetupCode ?? request.AgentSetupCode);
    if (!IsValidSetupCode(setupCode))
    {
        return Results.BadRequest(new { message = "Agent setup code format is invalid." });
    }

    if (string.IsNullOrWhiteSpace(request.DeviceId))
    {
        return Results.BadRequest(new { message = "Device ID is required." });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetUserBySetupCodeAsync(connection, setupCode);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var device = await TryGetOwnedDeviceByHardwareIdAsync(
            connection,
            user.Id,
            NormalizeOptionalValue(request.DeviceId, 128));
        device ??= await TryGetLatestOwnedDeviceAsync(connection, user.Id, string.Empty);
        if (device is null)
        {
            return Results.NotFound(new { message = "Device not found." });
        }

        var settings = await TryGetRecoverySettingsAsync(connection, user.Id, device.Id);
        if (settings is null)
        {
            return Results.Ok(new RecoverySettingsResponse(
                device.Id.ToString(),
                device.DeviceName,
                false,
                new List<RecoveryApprovedLocationRecord>(),
                string.Empty,
                string.Empty,
                string.Empty));
        }

        return Results.Ok(new RecoverySettingsResponse(
            device.Id.ToString(),
            settings.DeviceName,
            settings.Enabled,
            DeserializeApprovedLocations(settings.ApprovedLocationsJson),
            settings.LastSyncedAt,
            settings.SettingsUpdatedAt,
            settings.ScanRequestedAt));
    }
    catch (Exception ex)
    {
        Console.WriteLine("Get agent recovery settings failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Unable to load recovery settings for the agent right now." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/recovery/request-scan", async (HttpRequest request, RecoveryScanRequest payload) =>
{
    if (payload is null || !Guid.TryParse(payload.DeviceId, out var deviceId))
    {
        return Results.BadRequest(new { message = "A valid deviceId is required." });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetAuthorizedUserAsync(request, connection);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var device = await TryGetOwnedDeviceByIdAsync(connection, user.Id, deviceId);
        if (device is null)
        {
            return Results.NotFound(new { message = "Device not found." });
        }

        var now = DateTimeOffset.UtcNow;
        await using var command = new NpgsqlCommand(
            """
            update recovery_settings
            set scan_requested_at = @scan_requested_at,
                updated_at = @updated_at
            where user_id = @user_id
              and device_id = @device_id
            """,
            connection);
        command.Parameters.AddWithValue("scan_requested_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        command.Parameters.AddWithValue("user_id", user.Id);
        command.Parameters.AddWithValue("device_id", device.Id);
        var rows = await command.ExecuteNonQueryAsync();

        if (rows == 0)
        {
            return Results.BadRequest(new { message = "Save Emergency Recovery settings first before starting a scan." });
        }

        return Results.Ok(new
        {
            message = "Recovery scan requested successfully.",
            scanRequestedAt = now.ToString("O"),
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine("Request recovery scan failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Unable to request a recovery scan right now." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/agent/reset", async (HttpRequest request, ResetAgentRequest payload) =>
{
    if (payload is null || !Guid.TryParse(payload.DeviceId, out var deviceId))
    {
        return Results.BadRequest(new { message = "A valid deviceId is required." });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetAuthorizedUserAsync(request, connection);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var device = await TryGetOwnedDeviceByIdAsync(connection, user.Id, deviceId);
        if (device is null)
        {
            return Results.NotFound(new { message = "Device not found." });
        }

        var newSetupCode = GenerateAgentSetupCode();
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await connection.BeginTransactionAsync();

        await using (var updateUser = new NpgsqlCommand(
            """
            update app_users
            set agent_setup_code = @agent_setup_code
            where id = @user_id
            """,
            connection,
            transaction))
        {
            updateUser.Parameters.AddWithValue("agent_setup_code", newSetupCode);
            updateUser.Parameters.AddWithValue("user_id", user.Id);
            await updateUser.ExecuteNonQueryAsync();
        }

        await using (var updateDevice = new NpgsqlCommand(
            """
            update devices
            set status = 'Disconnected',
                updated_at = @updated_at
            where id = @device_id
              and user_id = @user_id
            """,
            connection,
            transaction))
        {
            updateDevice.Parameters.AddWithValue("updated_at", now);
            updateDevice.Parameters.AddWithValue("device_id", device.Id);
            updateDevice.Parameters.AddWithValue("user_id", user.Id);
            await updateDevice.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();

        return Results.Ok(new
        {
            message = "Agent reset successfully. Reconnect with the new setup code.",
            agentSetupCode = newSetupCode,
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine("Reset agent failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Unable to reset the agent right now." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/recovery/upload", SaveRecoveryFileListingsAsync);
app.MapPost("/api/recovery/file-listings", SaveRecoveryFileListingsAsync);
app.MapPost("/api/recovery/file-listings/batch", SaveRecoveryFileListingsBatchAsync);

app.MapGet("/api/recovery/file-list", async (HttpRequest request) =>
{
    var deviceIdValue = request.Query["deviceId"].ToString();
    if (!Guid.TryParse(deviceIdValue, out var deviceId))
    {
        return Results.BadRequest(new { message = "A valid deviceId is required." });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetAuthorizedUserAsync(request, connection);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var device = await TryGetOwnedDeviceByIdAsync(connection, user.Id, deviceId);
        if (device is null)
        {
            return Results.NotFound(new { message = "Device not found." });
        }

        var files = new List<RecoveryFileRecord>();
        await using var fileListCommand = new NpgsqlCommand(
            """
            select
                file_name,
                full_path,
                root_label,
                root_path,
                extension,
                size_bytes,
                last_modified_at,
                is_directory,
                drive_letter
            from recovery_file_listings
            where user_id = @user_id
              and device_id = @device_id
            order by is_directory desc, file_name asc
            """,
            connection);
        fileListCommand.Parameters.AddWithValue("user_id", user.Id);
        fileListCommand.Parameters.AddWithValue("device_id", device.Id);

        await using var reader = await fileListCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            files.Add(new RecoveryFileRecord(
                reader["file_name"]?.ToString() ?? "Unknown",
                reader["full_path"]?.ToString() ?? string.Empty,
                reader["root_label"]?.ToString() ?? string.Empty,
                reader["root_path"]?.ToString() ?? string.Empty,
                reader["extension"]?.ToString() ?? string.Empty,
                reader["size_bytes"] is long sizeValue ? sizeValue : 0,
                ToIsoString(reader["last_modified_at"]),
                reader["is_directory"] is bool isDirectory && isDirectory,
                reader["drive_letter"]?.ToString() ?? string.Empty));
        }

        return Results.Ok(files);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Get recovery file list failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Unable to load emergency recovery file list right now." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/recovery/{deviceId:guid}", async (HttpRequest request, Guid deviceId) =>
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

        var device = await TryGetOwnedDeviceByIdAsync(connection, user.Id, deviceId);
        if (device is null)
        {
            return Results.NotFound(new { message = "Device not found." });
        }

        var enabled = false;
        var approvedLocations = new List<RecoveryApprovedLocationRecord>();
        var lastScanTime = string.Empty;

        await using (var settingsCommand = new NpgsqlCommand(
            """
            select enabled, approved_locations, last_synced_at
            from recovery_settings
            where user_id = @user_id
              and device_id = @device_id
            limit 1
            """,
            connection))
        {
            settingsCommand.Parameters.AddWithValue("user_id", user.Id);
            settingsCommand.Parameters.AddWithValue("device_id", device.Id);

            await using var settingsReader = await settingsCommand.ExecuteReaderAsync(CommandBehavior.SingleRow);
            if (await settingsReader.ReadAsync())
            {
                enabled = settingsReader["enabled"] is bool enabledValue && enabledValue;
                approvedLocations = DeserializeApprovedLocations(settingsReader["approved_locations"]?.ToString());
                lastScanTime = ToIsoString(settingsReader["last_synced_at"]);
            }
        }

        var files = new List<RecoveryFileRecord>();
        await using (var fileListCommand = new NpgsqlCommand(
            """
            select
                file_name,
                full_path,
                root_label,
                root_path,
                extension,
                size_bytes,
                last_modified_at,
                is_directory,
                drive_letter
            from recovery_file_listings
            where user_id = @user_id
              and device_id = @device_id
            order by is_directory desc, file_name asc
            """,
            connection))
        {
            fileListCommand.Parameters.AddWithValue("user_id", user.Id);
            fileListCommand.Parameters.AddWithValue("device_id", device.Id);

            await using var reader = await fileListCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                files.Add(new RecoveryFileRecord(
                    reader["file_name"]?.ToString() ?? "Unknown",
                    reader["full_path"]?.ToString() ?? string.Empty,
                    reader["root_label"]?.ToString() ?? string.Empty,
                    reader["root_path"]?.ToString() ?? string.Empty,
                    reader["extension"]?.ToString() ?? string.Empty,
                    reader["size_bytes"] is long sizeValue ? sizeValue : 0,
                    ToIsoString(reader["last_modified_at"]),
                    reader["is_directory"] is bool isDirectory && isDirectory,
                    reader["drive_letter"]?.ToString() ?? string.Empty));
            }
        }

        return Results.Ok(new RecoveryInventoryResponse(
            device.Id.ToString(),
            device.DeviceName,
            enabled,
            approvedLocations,
            files.Count,
            lastScanTime,
            files));
    }
    catch (Exception ex)
    {
        Console.WriteLine("Get recovery inventory failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Unable to load emergency recovery inventory right now." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/transfers/history", async (HttpRequest request) =>
{
    var deviceIdValue = request.Query["deviceId"].ToString();
    if (!Guid.TryParse(deviceIdValue, out var deviceId))
    {
        return Results.BadRequest(new { message = "A valid deviceId is required." });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetAuthorizedUserAsync(request, connection);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var device = await TryGetOwnedDeviceByIdAsync(connection, user.Id, deviceId);
        if (device is null)
        {
            return Results.NotFound(new { message = "Device not found." });
        }

        var jobs = new List<TransferJobRecord>();
        await using var command = new NpgsqlCommand(
            """
            select
                id,
                job_type,
                status,
                requested_file_path,
                requested_file_name,
                destination_path,
                storage_key,
                storage_file_name,
                error_message,
                created_at,
                updated_at,
                completed_at
            from transfer_jobs
            where user_id = @user_id
              and device_id = @device_id
            order by created_at desc
            limit 100
            """,
            connection);
        command.Parameters.AddWithValue("user_id", user.Id);
        command.Parameters.AddWithValue("device_id", device.Id);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            jobs.Add(MapTransferJobRecord(reader));
        }

        return Results.Ok(jobs);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return Results.Json(
            new
            {
                message = "Unable to load transfer history.",
                error = ex.Message,
                details = ex.ToString(),
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/transfers/download-from-device", async (HttpRequest request, DownloadFromDeviceRequest payload) =>
{
    if (payload is null || !Guid.TryParse(payload.DeviceId, out var deviceId))
    {
        return Results.BadRequest(new { message = "A valid deviceId is required." });
    }

    if (string.IsNullOrWhiteSpace(payload.FilePath))
    {
        return Results.BadRequest(new { message = "A file path is required." });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetAuthorizedUserAsync(request, connection);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var device = await TryGetOwnedDeviceByIdAsync(connection, user.Id, deviceId);
        if (device is null)
        {
            return Results.NotFound(new { message = "Device not found." });
        }

        var fileName = Path.GetFileName(payload.FilePath.Trim());
        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.NewGuid();

        await using var command = new NpgsqlCommand(
            """
            insert into transfer_jobs (
                id,
                user_id,
                device_id,
                job_type,
                status,
                requested_file_path,
                requested_file_name,
                destination_path,
                created_at,
                updated_at
            )
            values (
                @id,
                @user_id,
                @device_id,
                @job_type,
                @status,
                @requested_file_path,
                @requested_file_name,
                @destination_path,
                @created_at,
                @updated_at
            )
            """,
            connection);
        command.Parameters.AddWithValue("id", jobId);
        command.Parameters.AddWithValue("user_id", user.Id);
        command.Parameters.AddWithValue("device_id", device.Id);
        command.Parameters.AddWithValue("job_type", "download_from_device");
        command.Parameters.AddWithValue("status", "Pending");
        command.Parameters.AddWithValue("requested_file_path", payload.FilePath.Trim());
        command.Parameters.AddWithValue("requested_file_name", fileName);
        command.Parameters.AddWithValue("destination_path", string.Empty);
        command.Parameters.AddWithValue("created_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        await command.ExecuteNonQueryAsync();

        return Results.Ok(new
        {
            message = "Download request created successfully.",
            jobId,
            status = "Pending",
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return Results.Json(
            new
            {
                message = "Unable to create download request.",
                error = ex.Message,
                details = ex.ToString(),
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/transfers/upload-to-device", async (HttpRequest request) =>
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

        var form = await request.ReadFormAsync();
        var deviceIdValue = form["deviceId"].ToString();
        if (!Guid.TryParse(deviceIdValue, out var deviceId))
        {
            return Results.BadRequest(new { message = "A valid deviceId is required." });
        }

        var device = await TryGetOwnedDeviceByIdAsync(connection, user.Id, deviceId);
        if (device is null)
        {
            return Results.NotFound(new { message = "Device not found." });
        }

        var file = form.Files["file"];
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { message = "A file is required." });
        }

        var safeFileName = Path.GetFileName(file.FileName);
        var storageKey = $"{Guid.NewGuid():N}_{safeFileName}";
        var storagePath = Path.Combine(transferStorageRoot, storageKey);

        await using (var fileStream = File.Create(storagePath))
        {
            await file.CopyToAsync(fileStream);
        }

        var destinationPath = form["destinationPath"].ToString();
        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.NewGuid();

        await using var command = new NpgsqlCommand(
            """
            insert into transfer_jobs (
                id,
                user_id,
                device_id,
                job_type,
                status,
                requested_file_path,
                requested_file_name,
                destination_path,
                storage_key,
                storage_file_name,
                created_at,
                updated_at
            )
            values (
                @id,
                @user_id,
                @device_id,
                @job_type,
                @status,
                @requested_file_path,
                @requested_file_name,
                @destination_path,
                @storage_key,
                @storage_file_name,
                @created_at,
                @updated_at
            )
            """,
            connection);
        command.Parameters.AddWithValue("id", jobId);
        command.Parameters.AddWithValue("user_id", user.Id);
        command.Parameters.AddWithValue("device_id", device.Id);
        command.Parameters.AddWithValue("job_type", "upload_to_device");
        command.Parameters.AddWithValue("status", "Pending");
        command.Parameters.AddWithValue("requested_file_path", string.Empty);
        command.Parameters.AddWithValue("requested_file_name", safeFileName);
        command.Parameters.AddWithValue("destination_path", destinationPath);
        command.Parameters.AddWithValue("storage_key", storageKey);
        command.Parameters.AddWithValue("storage_file_name", safeFileName);
        command.Parameters.AddWithValue("created_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        await command.ExecuteNonQueryAsync();

        return Results.Ok(new
        {
            message = "Upload-to-device request created successfully.",
            jobId,
            status = "Pending",
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return Results.Json(
            new
            {
                message = "Unable to create upload-to-device request.",
                error = ex.Message,
                details = ex.ToString(),
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/transfers/{jobId:guid}/download", async (HttpRequest request, Guid jobId) =>
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

        var job = await TryGetTransferJobByIdAsync(connection, jobId);
        if (job is null || job.UserId != user.Id)
        {
            return Results.NotFound(new { message = "Transfer job not found." });
        }

        if (!string.Equals(job.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(job.StorageKey))
        {
            return Results.BadRequest(new { message = "This transfer is not ready for download yet." });
        }

        var filePath = Path.Combine(transferStorageRoot, job.StorageKey);
        if (!File.Exists(filePath))
        {
            return Results.NotFound(new { message = "The transfer file is no longer available." });
        }

        var downloadFileName = string.IsNullOrWhiteSpace(job.StorageFileName)
            ? (string.IsNullOrWhiteSpace(job.RequestedFileName)
                ? Path.GetFileName(filePath)
                : job.RequestedFileName)
            : job.StorageFileName;
        var contentType = ResolveContentType(downloadFileName, contentTypeProvider);

        return Results.File(
            filePath,
            contentType,
            fileDownloadName: downloadFileName,
            enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return Results.Json(
            new
            {
                message = "Unable to download the completed transfer.",
                error = ex.Message,
                details = ex.ToString(),
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/agent/jobs/pending", async (AgentJobPollRequest request) =>
{
    try
    {
        var setupCode = NormalizeSetupCode(request.SetupCode ?? request.AgentSetupCode);
        if (!IsValidSetupCode(setupCode))
        {
            return Results.BadRequest(new { message = "Agent setup code format is invalid." });
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return Results.BadRequest(new { message = "Device ID is required." });
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetUserBySetupCodeAsync(connection, setupCode);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var device = await TryGetOwnedDeviceByHardwareIdAsync(connection, user.Id, NormalizeOptionalValue(request.DeviceId, 128));
        device ??= await TryGetLatestOwnedDeviceAsync(connection, user.Id, string.Empty);
        if (device is null)
        {
            return Results.Ok(Array.Empty<AgentTransferJobRecord>());
        }

        var jobs = new List<AgentTransferJobRecord>();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var selectCommand = new NpgsqlCommand(
            """
            select
                id,
                job_type,
                requested_file_path,
                destination_path,
                storage_file_name
            from transfer_jobs
            where user_id = @user_id
              and device_id = @device_id
              and status = 'Pending'
            order by created_at asc
            limit 10
            """,
            connection,
            transaction);
        selectCommand.Parameters.AddWithValue("user_id", user.Id);
        selectCommand.Parameters.AddWithValue("device_id", device.Id);

        await using (var reader = await selectCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                jobs.Add(new AgentTransferJobRecord(
                    reader["id"]?.ToString() ?? string.Empty,
                    reader["job_type"]?.ToString() ?? string.Empty,
                    reader["requested_file_path"]?.ToString() ?? string.Empty,
                    reader["destination_path"]?.ToString() ?? string.Empty,
                    reader["storage_file_name"]?.ToString() ?? string.Empty));
            }
        }

        foreach (var job in jobs)
        {
            await using var updateCommand = new NpgsqlCommand(
                """
                update transfer_jobs
                set status = 'In Progress',
                    updated_at = @updated_at
                where id = @id
                """,
                connection,
                transaction);
            updateCommand.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
            updateCommand.Parameters.AddWithValue("id", Guid.Parse(job.Id));
            await updateCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return Results.Ok(jobs);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return Results.Json(
            new
            {
                message = "Unable to load pending agent jobs.",
                error = ex.Message,
                details = ex.ToString(),
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/agent/jobs/{jobId:guid}/content", async (Guid jobId, string setupCode, string deviceId) =>
{
    try
    {
        var normalizedSetupCode = NormalizeSetupCode(setupCode);
        if (!IsValidSetupCode(normalizedSetupCode))
        {
            return Results.BadRequest(new { message = "Agent setup code format is invalid." });
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetUserBySetupCodeAsync(connection, normalizedSetupCode);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var device = await TryGetOwnedDeviceByHardwareIdAsync(connection, user.Id, NormalizeOptionalValue(deviceId, 128));
        if (device is null)
        {
            return Results.NotFound(new { message = "Device not found." });
        }

        var job = await TryGetTransferJobByIdAsync(connection, jobId);
        if (job is null || job.UserId != user.Id || job.DeviceId != device.Id)
        {
            return Results.NotFound(new { message = "Transfer job not found." });
        }

        var filePath = Path.Combine(transferStorageRoot, job.StorageKey);
        if (!File.Exists(filePath))
        {
            return Results.NotFound(new { message = "Transfer content not found." });
        }

        return Results.File(filePath, "application/octet-stream", job.StorageFileName);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return Results.Json(
            new
            {
                message = "Unable to load transfer content.",
                error = ex.Message,
                details = ex.ToString(),
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/agent/jobs/{jobId:guid}/complete-upload", async (Guid jobId, AgentJobUpdateRequest request) =>
{
    return await CompleteAgentTransferJobAsync(
        connectionString,
        jobId,
        request,
        "upload_to_device",
        transferStorageRoot,
        null);
});

app.MapPost("/api/agent/jobs/{jobId:guid}/complete-download", async (HttpRequest httpRequest, Guid jobId) =>
{
    try
    {
        var form = await httpRequest.ReadFormAsync();
        var request = new AgentJobUpdateRequest(
            form["setupCode"].ToString(),
            form["agentSetupCode"].ToString(),
            form["deviceId"].ToString(),
            string.Empty,
            string.Empty);
        var file = form.Files["file"];
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { message = "A file is required." });
        }

        return await CompleteAgentTransferJobAsync(
            connectionString,
            jobId,
            request,
            "download_from_device",
            transferStorageRoot,
            file);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return Results.Json(
            new
            {
                message = "Unable to complete the download-from-device job.",
                error = ex.Message,
                details = ex.ToString(),
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/agent/jobs/{jobId:guid}/fail", async (Guid jobId, AgentJobUpdateRequest request) =>
{
    try
    {
        var validation = await ValidateAgentJobOwnershipAsync(connectionString, jobId, request);
        if (validation.ErrorResult is not null)
        {
            return validation.ErrorResult;
        }

        await using var connection = validation.Connection!;
        await using var command = new NpgsqlCommand(
            """
            update transfer_jobs
            set status = 'Failed',
                error_message = @error_message,
                updated_at = @updated_at
            where id = @id
            """,
            connection);
        command.Parameters.AddWithValue("error_message", NormalizeOptionalValueOrEmpty(request.ErrorMessage, 400));
        command.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("id", jobId);
        await command.ExecuteNonQueryAsync();

        return Results.Ok(new { message = "Transfer job marked as failed." });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return Results.Json(
            new
            {
                message = "Unable to mark the transfer job as failed.",
                error = ex.Message,
                details = ex.ToString(),
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Run();

async Task<IResult> SaveRecoveryFileListingsAsync(RecoveryFileListRequest request)
{
    if (request is null)
    {
        return Results.BadRequest(new { message = "Recovery file list payload is required." });
    }

    var setupCode = NormalizeSetupCode(request.SetupCode ?? request.AgentSetupCode);
    if (!IsValidSetupCode(setupCode))
    {
        return Results.BadRequest(new { message = "Agent setup code format is invalid." });
    }

    if (!TryValidateRecoveryFileListRequest(request, out var validationMessage))
    {
        return Results.BadRequest(new { message = validationMessage });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetUserBySetupCodeAsync(connection, setupCode);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var normalizedDeviceId = NormalizeOptionalValue(request.DeviceId, 128);
        var device = await TryGetOwnedDeviceByHardwareIdAsync(connection, user.Id, normalizedDeviceId);
        device ??= await TryGetLatestOwnedDeviceAsync(
            connection,
            user.Id,
            NormalizeOptionalValueOrEmpty(request.DeviceName, 200));
        if (device is null)
        {
            return Results.BadRequest(new { message = "Device must be connected before recovery files can be scanned." });
        }

        var settings = await TryGetRecoverySettingsAsync(connection, user.Id, device.Id);
        if (settings is null || !settings.Enabled)
        {
            return Results.BadRequest(new { message = "Emergency recovery mode is not enabled for this device." });
        }

        var approvedLocations = DeserializeApprovedLocations(settings.ApprovedLocationsJson);
        var normalizedEntries = FilterRecoveryEntriesWithinApprovedLocations(
            NormalizeRecoveryFileEntries(request.Entries),
            approvedLocations);
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await connection.BeginTransactionAsync();

        await using (var deleteCommand = new NpgsqlCommand(
            """
            delete from recovery_file_listings
            where user_id = @user_id
              and device_id = @device_id
            """,
            connection,
            transaction))
        {
            deleteCommand.Parameters.AddWithValue("user_id", user.Id);
            deleteCommand.Parameters.AddWithValue("device_id", device.Id);
            await deleteCommand.ExecuteNonQueryAsync();
        }

        foreach (var entry in normalizedEntries)
        {
            var parsedLastModified = DateTimeOffset.TryParse(entry.LastModified, out var lastModifiedAt)
                ? lastModifiedAt
                : (DateTimeOffset?)null;

            await using var insertCommand = new NpgsqlCommand(
                """
                insert into recovery_file_listings (
                    id,
                    user_id,
                    device_id,
                    file_name,
                    full_path,
                    root_label,
                    root_path,
                    extension,
                    size_bytes,
                    last_modified_at,
                    is_directory,
                    drive_letter,
                    created_at,
                    updated_at
                )
                values (
                    @id,
                    @user_id,
                    @device_id,
                    @file_name,
                    @full_path,
                    @root_label,
                    @root_path,
                    @extension,
                    @size_bytes,
                    @last_modified_at,
                    @is_directory,
                    @drive_letter,
                    @created_at,
                    @updated_at
                )
                """,
                connection,
                transaction);

            insertCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertCommand.Parameters.AddWithValue("user_id", user.Id);
            insertCommand.Parameters.AddWithValue("device_id", device.Id);
            insertCommand.Parameters.AddWithValue("file_name", entry.FileName ?? string.Empty);
            insertCommand.Parameters.AddWithValue("full_path", entry.FullPath ?? string.Empty);
            insertCommand.Parameters.AddWithValue("root_label", entry.RootLabel ?? string.Empty);
            insertCommand.Parameters.AddWithValue("root_path", entry.RootPath ?? string.Empty);
            insertCommand.Parameters.AddWithValue("extension", entry.Extension ?? string.Empty);
            insertCommand.Parameters.AddWithValue("size_bytes", entry.SizeBytes);
            insertCommand.Parameters.AddWithValue("last_modified_at", parsedLastModified is null ? DBNull.Value : parsedLastModified.Value);
            insertCommand.Parameters.AddWithValue("is_directory", entry.IsDirectory);
            insertCommand.Parameters.AddWithValue("drive_letter", entry.DriveLetter ?? string.Empty);
            insertCommand.Parameters.AddWithValue("created_at", now);
            insertCommand.Parameters.AddWithValue("updated_at", now);
            await insertCommand.ExecuteNonQueryAsync();
        }

        await using (var updateSettingsCommand = new NpgsqlCommand(
            """
            update recovery_settings
            set device_name = @device_name,
                last_synced_at = @last_synced_at,
                scan_requested_at = null,
                updated_at = @updated_at
            where user_id = @user_id
              and device_id = @device_id
            """,
            connection,
            transaction))
        {
            updateSettingsCommand.Parameters.AddWithValue("device_name", NormalizeOptionalValue(request.DeviceName, 200));
            updateSettingsCommand.Parameters.AddWithValue("last_synced_at", now);
            updateSettingsCommand.Parameters.AddWithValue("updated_at", now);
            updateSettingsCommand.Parameters.AddWithValue("user_id", user.Id);
            updateSettingsCommand.Parameters.AddWithValue("device_id", device.Id);
            await updateSettingsCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();

        return Results.Ok(new
        {
            message = "Emergency recovery file list saved successfully.",
            entriesSaved = normalizedEntries.Count,
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine("Recovery file list save failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Unable to save emergency recovery file list right now." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}

async Task<IResult> SaveRecoveryFileListingsBatchAsync(RecoveryFileListRequest request)
{
    if (request is null)
    {
        return Results.BadRequest(new { message = "Recovery batch payload is required." });
    }

    var setupCode = NormalizeSetupCode(request.SetupCode ?? request.AgentSetupCode);
    if (!IsValidSetupCode(setupCode))
    {
        return Results.BadRequest(new { message = "Agent setup code format is invalid." });
    }

    if (!TryValidateRecoveryFileListRequest(request, out var validationMessage))
    {
        return Results.BadRequest(new { message = validationMessage });
    }

    var batchIndex = request.BatchIndex ?? 0;
    var totalBatches = request.TotalBatches ?? 1;
    if (batchIndex < 0 || totalBatches <= 0 || batchIndex >= totalBatches)
    {
        return Results.BadRequest(new { message = "Recovery batch metadata is invalid." });
    }

    if ((request.Entries?.Count ?? 0) > 1000)
    {
        return Results.BadRequest(new { message = "Recovery batch size must be 1000 entries or fewer." });
    }

    var replaceExisting = request.ReplaceExisting || batchIndex == 0;
    var isFinalBatch = request.IsFinalBatch || batchIndex == totalBatches - 1;

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var user = await TryGetUserBySetupCodeAsync(connection, setupCode);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var normalizedDeviceId = NormalizeOptionalValue(request.DeviceId, 128);
        var device = await TryGetOwnedDeviceByHardwareIdAsync(connection, user.Id, normalizedDeviceId);
        device ??= await TryGetLatestOwnedDeviceAsync(
            connection,
            user.Id,
            NormalizeOptionalValueOrEmpty(request.DeviceName, 200));
        if (device is null)
        {
            return Results.BadRequest(new { message = "Device must be connected before recovery files can be scanned." });
        }

        var settings = await TryGetRecoverySettingsAsync(connection, user.Id, device.Id);
        if (settings is null || !settings.Enabled)
        {
            return Results.BadRequest(new { message = "Emergency recovery mode is not enabled for this device." });
        }

        var approvedLocations = DeserializeApprovedLocations(settings.ApprovedLocationsJson);
        var normalizedEntries = FilterRecoveryEntriesWithinApprovedLocations(
            NormalizeRecoveryFileEntries(request.Entries),
            approvedLocations);
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await connection.BeginTransactionAsync();

        if (replaceExisting)
        {
            await using var deleteCommand = new NpgsqlCommand(
                """
                delete from recovery_file_listings
                where user_id = @user_id
                  and device_id = @device_id
                """,
                connection,
                transaction);
            deleteCommand.Parameters.AddWithValue("user_id", user.Id);
            deleteCommand.Parameters.AddWithValue("device_id", device.Id);
            await deleteCommand.ExecuteNonQueryAsync();
        }

        foreach (var entry in normalizedEntries)
        {
            var parsedLastModified = DateTimeOffset.TryParse(entry.LastModified, out var lastModifiedAt)
                ? lastModifiedAt
                : (DateTimeOffset?)null;

            await using var insertCommand = new NpgsqlCommand(
                """
                insert into recovery_file_listings (
                    id,
                    user_id,
                    device_id,
                    file_name,
                    full_path,
                    root_label,
                    root_path,
                    extension,
                    size_bytes,
                    last_modified_at,
                    is_directory,
                    drive_letter,
                    created_at,
                    updated_at
                )
                values (
                    @id,
                    @user_id,
                    @device_id,
                    @file_name,
                    @full_path,
                    @root_label,
                    @root_path,
                    @extension,
                    @size_bytes,
                    @last_modified_at,
                    @is_directory,
                    @drive_letter,
                    @created_at,
                    @updated_at
                )
                on conflict (user_id, device_id, full_path)
                do update set
                    file_name = excluded.file_name,
                    root_label = excluded.root_label,
                    root_path = excluded.root_path,
                    extension = excluded.extension,
                    size_bytes = excluded.size_bytes,
                    last_modified_at = excluded.last_modified_at,
                    is_directory = excluded.is_directory,
                    drive_letter = excluded.drive_letter,
                    updated_at = excluded.updated_at
                """,
                connection,
                transaction);

            insertCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertCommand.Parameters.AddWithValue("user_id", user.Id);
            insertCommand.Parameters.AddWithValue("device_id", device.Id);
            insertCommand.Parameters.AddWithValue("file_name", entry.FileName ?? string.Empty);
            insertCommand.Parameters.AddWithValue("full_path", entry.FullPath ?? string.Empty);
            insertCommand.Parameters.AddWithValue("root_label", entry.RootLabel ?? string.Empty);
            insertCommand.Parameters.AddWithValue("root_path", entry.RootPath ?? string.Empty);
            insertCommand.Parameters.AddWithValue("extension", entry.Extension ?? string.Empty);
            insertCommand.Parameters.AddWithValue("size_bytes", entry.SizeBytes);
            insertCommand.Parameters.AddWithValue("last_modified_at", parsedLastModified is null ? DBNull.Value : parsedLastModified.Value);
            insertCommand.Parameters.AddWithValue("is_directory", entry.IsDirectory);
            insertCommand.Parameters.AddWithValue("drive_letter", entry.DriveLetter ?? string.Empty);
            insertCommand.Parameters.AddWithValue("created_at", now);
            insertCommand.Parameters.AddWithValue("updated_at", now);
            await insertCommand.ExecuteNonQueryAsync();
        }

        await using (var updateSettingsCommand = new NpgsqlCommand(
            isFinalBatch
                ? """
                  update recovery_settings
                  set device_name = @device_name,
                      last_synced_at = @last_synced_at,
                      scan_requested_at = null,
                      updated_at = @updated_at
                  where user_id = @user_id
                    and device_id = @device_id
                  """
                : """
                  update recovery_settings
                  set device_name = @device_name,
                      updated_at = @updated_at
                  where user_id = @user_id
                    and device_id = @device_id
                  """,
            connection,
            transaction))
        {
            updateSettingsCommand.Parameters.AddWithValue("device_name", NormalizeOptionalValue(request.DeviceName, 200));
            if (isFinalBatch)
            {
                updateSettingsCommand.Parameters.AddWithValue("last_synced_at", now);
            }
            updateSettingsCommand.Parameters.AddWithValue("updated_at", now);
            updateSettingsCommand.Parameters.AddWithValue("user_id", user.Id);
            updateSettingsCommand.Parameters.AddWithValue("device_id", device.Id);
            await updateSettingsCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();

        return Results.Ok(new
        {
            message = "Emergency recovery batch saved successfully.",
            batchIndex,
            totalBatches,
            entriesSaved = normalizedEntries.Count,
            isFinalBatch,
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine("Recovery batch save failed:");
        Console.WriteLine(ex);
        return Results.Json(
            new { message = "Unable to save the recovery batch right now." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}

static async Task EnsureTransferTablesAsync(string connectionString)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    await using var command = new NpgsqlCommand(
        """
        create table if not exists transfer_jobs (
            id uuid not null primary key,
            user_id uuid not null references app_users(id),
            device_id uuid not null references devices(id),
            job_type text not null,
            status text not null default 'Pending',
            requested_file_path text not null default '',
            requested_file_name text not null default '',
            destination_path text not null default '',
            storage_key text not null default '',
            storage_file_name text not null default '',
            error_message text not null default '',
            created_at timestamp with time zone not null default now(),
            updated_at timestamp with time zone not null default now(),
            completed_at timestamp with time zone
        );

        create index if not exists idx_transfer_jobs_user_device_created
            on transfer_jobs(user_id, device_id, created_at desc);

        create index if not exists idx_transfer_jobs_device_status
            on transfer_jobs(device_id, status, created_at asc);
        """,
        connection);

    await command.ExecuteNonQueryAsync();
}

static async Task EnsureRecoveryFileListingColumnsAsync(string connectionString)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    await using var command = new NpgsqlCommand(
        """
        alter table if exists recovery_file_listings
            add column if not exists root_label text not null default '';

        alter table if exists recovery_file_listings
            add column if not exists root_path text not null default '';
        """,
        connection);

    await command.ExecuteNonQueryAsync();
}

static async Task EnsureRecoverySettingsColumnsAsync(string connectionString)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    await using var command = new NpgsqlCommand(
        """
        alter table if exists recovery_settings
            add column if not exists settings_updated_at timestamp with time zone;

        alter table if exists recovery_settings
            add column if not exists scan_requested_at timestamp with time zone;
        """,
        connection);

    await command.ExecuteNonQueryAsync();
}

static async Task<TransferJobOwnershipValidation> ValidateAgentJobOwnershipAsync(
    string connectionString,
    Guid jobId,
    AgentJobUpdateRequest request)
{
    var normalizedSetupCode = NormalizeSetupCode(request.SetupCode ?? request.AgentSetupCode);
    if (!IsValidSetupCode(normalizedSetupCode))
    {
        return new TransferJobOwnershipValidation(
            null,
            null,
            Results.BadRequest(new { message = "Agent setup code format is invalid." }));
    }

    if (string.IsNullOrWhiteSpace(request.DeviceId))
    {
        return new TransferJobOwnershipValidation(
            null,
            null,
            Results.BadRequest(new { message = "Device ID is required." }));
    }

    var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    var user = await TryGetUserBySetupCodeAsync(connection, normalizedSetupCode);
    if (user is null)
    {
        await connection.DisposeAsync();
        return new TransferJobOwnershipValidation(null, null, Results.Unauthorized());
    }

    var device = await TryGetOwnedDeviceByHardwareIdAsync(connection, user.Id, NormalizeOptionalValue(request.DeviceId, 128));
    if (device is null)
    {
        await connection.DisposeAsync();
        return new TransferJobOwnershipValidation(
            null,
            null,
            Results.NotFound(new { message = "Device not found." }));
    }

    var job = await TryGetTransferJobByIdAsync(connection, jobId);
    if (job is null || job.UserId != user.Id || job.DeviceId != device.Id)
    {
        await connection.DisposeAsync();
        return new TransferJobOwnershipValidation(
            null,
            null,
            Results.NotFound(new { message = "Transfer job not found." }));
    }

    return new TransferJobOwnershipValidation(connection, job, null);
}

static async Task<IResult> CompleteAgentTransferJobAsync(
    string connectionString,
    Guid jobId,
    AgentJobUpdateRequest request,
    string expectedJobType,
    string transferStorageRoot,
    IFormFile? uploadedFile)
{
    try
    {
        var validation = await ValidateAgentJobOwnershipAsync(connectionString, jobId, request);
        if (validation.ErrorResult is not null)
        {
            return validation.ErrorResult;
        }

        await using var connection = validation.Connection!;
        var job = validation.Job!;

        if (!string.Equals(job.JobType, expectedJobType, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { message = "Transfer job type mismatch." });
        }

        var now = DateTimeOffset.UtcNow;
        var storageKey = job.StorageKey;
        var storageFileName = job.StorageFileName;

        if (uploadedFile is not null)
        {
            storageFileName = string.IsNullOrWhiteSpace(uploadedFile.FileName)
                ? job.RequestedFileName
                : Path.GetFileName(uploadedFile.FileName);
            storageKey = $"{Guid.NewGuid():N}_{storageFileName}";
            var storagePath = Path.Combine(transferStorageRoot, storageKey);

            await using var fileStream = File.Create(storagePath);
            await uploadedFile.CopyToAsync(fileStream);
        }

        await using var command = new NpgsqlCommand(
            """
            update transfer_jobs
            set status = 'Completed',
                storage_key = @storage_key,
                storage_file_name = @storage_file_name,
                error_message = '',
                updated_at = @updated_at,
                completed_at = @completed_at
            where id = @id
            """,
            connection);
        command.Parameters.AddWithValue("storage_key", storageKey);
        command.Parameters.AddWithValue("storage_file_name", storageFileName);
        command.Parameters.AddWithValue("updated_at", now);
        command.Parameters.AddWithValue("completed_at", now);
        command.Parameters.AddWithValue("id", jobId);
        await command.ExecuteNonQueryAsync();

        return Results.Ok(new { message = "Transfer job completed successfully." });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return Results.Json(
            new
            {
                message = "Unable to complete the transfer job.",
                error = ex.Message,
                details = ex.ToString(),
            },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}

static TransferJobRecord MapTransferJobRecord(NpgsqlDataReader reader)
{
    return new TransferJobRecord(
        reader["id"]?.ToString() ?? string.Empty,
        reader["job_type"]?.ToString() ?? string.Empty,
        reader["status"]?.ToString() ?? string.Empty,
        reader["requested_file_path"]?.ToString() ?? string.Empty,
        reader["requested_file_name"]?.ToString() ?? string.Empty,
        reader["destination_path"]?.ToString() ?? string.Empty,
        reader["storage_file_name"]?.ToString() ?? string.Empty,
        reader["error_message"]?.ToString() ?? string.Empty,
        ToIsoString(reader["created_at"]),
        ToIsoString(reader["updated_at"]),
        ToIsoString(reader["completed_at"]));
}

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

static async Task<AppUserRecord?> TryGetUserBySetupCodeAsync(NpgsqlConnection connection, string setupCode)
{
    await using var command = new NpgsqlCommand(
        """
        select id, email, token, agent_setup_code
        from app_users
        where agent_setup_code = @setup_code
        limit 1
        """,
        connection);
    command.Parameters.AddWithValue("setup_code", setupCode);

    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
    return await reader.ReadAsync()
        ? new AppUserRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader["email"]?.ToString() ?? string.Empty,
            reader["token"]?.ToString() ?? string.Empty,
            reader["agent_setup_code"]?.ToString() ?? string.Empty)
        : null;
}

static async Task<OwnedDeviceRecord?> TryGetOwnedDeviceByIdAsync(NpgsqlConnection connection, Guid userId, Guid deviceId)
{
    await using var command = new NpgsqlCommand(
        """
        select id, user_id, device_id, device_name
        from devices
        where id = @id
          and user_id = @user_id
        limit 1
        """,
        connection);
    command.Parameters.AddWithValue("id", deviceId);
    command.Parameters.AddWithValue("user_id", userId);

    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
    return await reader.ReadAsync()
        ? new OwnedDeviceRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetGuid(reader.GetOrdinal("user_id")),
            reader["device_id"]?.ToString() ?? string.Empty,
            reader["device_name"]?.ToString() ?? "Unknown")
        : null;
}

static async Task<OwnedDeviceRecord?> TryGetOwnedDeviceByHardwareIdAsync(NpgsqlConnection connection, Guid userId, string deviceHardwareId)
{
    await using var command = new NpgsqlCommand(
        """
        select id, user_id, device_id, device_name
        from devices
        where user_id = @user_id
          and device_id = @device_id
        limit 1
        """,
        connection);
    command.Parameters.AddWithValue("user_id", userId);
    command.Parameters.AddWithValue("device_id", deviceHardwareId);

    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
    return await reader.ReadAsync()
        ? new OwnedDeviceRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetGuid(reader.GetOrdinal("user_id")),
            reader["device_id"]?.ToString() ?? string.Empty,
            reader["device_name"]?.ToString() ?? "Unknown")
        : null;
}

static async Task<OwnedDeviceRecord?> TryResolveRecoveryDeviceAsync(
    NpgsqlConnection connection,
    Guid userId,
    string rawDeviceIdentifier)
{
    if (Guid.TryParse(rawDeviceIdentifier, out var deviceId))
    {
        return await TryGetOwnedDeviceByIdAsync(connection, userId, deviceId);
    }

    if (string.IsNullOrWhiteSpace(rawDeviceIdentifier))
    {
        return null;
    }

    return await TryGetOwnedDeviceByHardwareIdAsync(connection, userId, rawDeviceIdentifier);
}

static async Task<OwnedDeviceRecord?> TryGetLatestOwnedDeviceAsync(
    NpgsqlConnection connection,
    Guid userId,
    string preferredDeviceName)
{
    await using var command = new NpgsqlCommand(
        """
        select id, user_id, device_id, device_name
        from devices
        where user_id = @user_id
        order by
            case
                when @preferred_device_name <> '' and lower(device_name) = lower(@preferred_device_name) then 0
                else 1
            end,
            last_seen_at desc nulls last,
            updated_at desc nulls last
        limit 1
        """,
        connection);
    command.Parameters.AddWithValue("user_id", userId);
    command.Parameters.AddWithValue("preferred_device_name", preferredDeviceName);

    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
    return await reader.ReadAsync()
        ? new OwnedDeviceRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetGuid(reader.GetOrdinal("user_id")),
            reader["device_id"]?.ToString() ?? string.Empty,
            reader["device_name"]?.ToString() ?? "Unknown")
        : null;
}

static async Task<RecoverySettingsStorageRecord?> TryGetRecoverySettingsAsync(NpgsqlConnection connection, Guid userId, Guid deviceId)
{
    await using var command = new NpgsqlCommand(
        """
        select
            enabled,
            approved_locations,
            device_name,
            last_synced_at,
            settings_updated_at,
            scan_requested_at
        from recovery_settings
        where user_id = @user_id
          and device_id = @device_id
        limit 1
        """,
        connection);
    command.Parameters.AddWithValue("user_id", userId);
    command.Parameters.AddWithValue("device_id", deviceId);

    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
    return await reader.ReadAsync()
        ? new RecoverySettingsStorageRecord(
            reader["enabled"] is bool enabled && enabled,
            reader["approved_locations"]?.ToString() ?? "[]",
            reader["device_name"]?.ToString() ?? string.Empty,
            ToIsoString(reader["last_synced_at"]),
            ToIsoString(reader["settings_updated_at"]),
            ToIsoString(reader["scan_requested_at"]))
        : null;
}

static async Task<TransferJobStorageRecord?> TryGetTransferJobByIdAsync(NpgsqlConnection connection, Guid jobId)
{
    await using var command = new NpgsqlCommand(
        """
        select
            id,
            user_id,
            device_id,
            job_type,
            status,
            requested_file_name,
            storage_key,
            storage_file_name
        from transfer_jobs
        where id = @id
        limit 1
        """,
        connection);
    command.Parameters.AddWithValue("id", jobId);

    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
    return await reader.ReadAsync()
        ? new TransferJobStorageRecord(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetGuid(reader.GetOrdinal("user_id")),
            reader.GetGuid(reader.GetOrdinal("device_id")),
            reader["job_type"]?.ToString() ?? string.Empty,
            reader["status"]?.ToString() ?? string.Empty,
            reader["requested_file_name"]?.ToString() ?? string.Empty,
            reader["storage_key"]?.ToString() ?? string.Empty,
            reader["storage_file_name"]?.ToString() ?? string.Empty)
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

static bool TryValidateRecoverySettingsRequest(RecoverySettingsRequest request, out string message)
{
    if (!ValidateRequiredField("Device ID", request.DeviceId, 128, out message) ||
        !ValidateRequiredField("Device name", request.DeviceName, 200, out message))
    {
        return false;
    }

    if (request.Enabled && (request.ApprovedLocations is null || request.ApprovedLocations.Count == 0))
    {
        message = "At least one approved recovery location is required.";
        return false;
    }

    if (request.ApprovedLocations is { Count: > 32 })
    {
        message = "Too many approved recovery locations were submitted.";
        return false;
    }

    foreach (var location in request.ApprovedLocations ?? new List<RecoveryApprovedLocationRecord>())
    {
        if (!ValidateRequiredField("Recovery location label", location.Label, 120, out message) ||
            !ValidateRequiredField("Recovery location path", location.FullPath, 400, out message) ||
            !ValidateOptionalField("Recovery drive letter", location.DriveLetter, 16, out message) ||
            !ValidateOptionalField("Recovery location type", location.LocationType, 40, out message))
        {
            return false;
        }
    }

    message = string.Empty;
    return true;
}

static bool TryValidateRecoveryFileListRequest(RecoveryFileListRequest request, out string message)
{
    if (!ValidateRequiredField("Device ID", request.DeviceId, 128, out message) ||
        !ValidateRequiredField("Device name", request.DeviceName, 200, out message))
    {
        return false;
    }

    if (request.Entries is null)
    {
        message = "Recovery file entries are required.";
        return false;
    }

    if (request.Entries.Count > 50000)
    {
        message = "Too many recovery file entries were submitted.";
        return false;
    }

    if (request.BatchIndex is int batchIndex && batchIndex < 0)
    {
        message = "Batch index must not be negative.";
        return false;
    }

    if (request.TotalBatches is int totalBatches && totalBatches <= 0)
    {
        message = "Total batches must be greater than zero.";
        return false;
    }

    foreach (var entry in request.Entries)
    {
        if (!ValidateRequiredField("File name", entry.FileName, 260, out message) ||
            !ValidateRequiredField("Full path", entry.FullPath, 400, out message) ||
            !ValidateRequiredField("Root label", entry.RootLabel, 128, out message) ||
            !ValidateRequiredField("Root path", entry.RootPath, 400, out message) ||
            !ValidateOptionalField("Extension", entry.Extension, 32, out message) ||
            !ValidateOptionalField("Drive letter", entry.DriveLetter, 16, out message))
        {
            return false;
        }

        if (entry.SizeBytes < 0)
        {
            message = "File size must not be negative.";
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

static List<RecoveryApprovedLocationRecord> NormalizeApprovedLocations(List<RecoveryApprovedLocationRecord>? locations)
{
    return (locations ?? new List<RecoveryApprovedLocationRecord>())
        .Select(location => new RecoveryApprovedLocationRecord(
            NormalizeOptionalValue(location.Label, 120),
            NormalizeRecoveryPath(location.FullPath),
            NormalizeOptionalValue(location.DriveLetter, 16),
            NormalizeOptionalValue(location.LocationType, 40)))
        .DistinctBy(location => location.FullPath, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static List<RecoveryApprovedLocationRecord> FilterSupportedApprovedLocations(List<RecoveryApprovedLocationRecord>? locations)
{
    return NormalizeApprovedLocations(locations)
        .Where(location => IsSupportedApprovedLocation(location.FullPath))
        .ToList();
}

static List<RecoveryApprovedLocationRecord> DeserializeApprovedLocations(string? locationsJson)
{
    if (string.IsNullOrWhiteSpace(locationsJson))
    {
        return new List<RecoveryApprovedLocationRecord>();
    }

    try
    {
        return NormalizeApprovedLocations(
            JsonSerializer.Deserialize<List<RecoveryApprovedLocationRecord>>(locationsJson) ??
            new List<RecoveryApprovedLocationRecord>());
    }
    catch
    {
        return new List<RecoveryApprovedLocationRecord>();
    }
}

static List<RecoveryFileRecord> NormalizeRecoveryFileEntries(List<RecoveryFileRecord>? entries)
{
    return (entries ?? new List<RecoveryFileRecord>())
        .Select(entry => new RecoveryFileRecord(
            NormalizeOptionalValue(entry.FileName, 260),
            NormalizeRecoveryPath(entry.FullPath),
            NormalizeOptionalValue(entry.RootLabel, 128),
            NormalizeRecoveryPath(entry.RootPath),
            NormalizeOptionalValue(entry.Extension, 32),
            entry.SizeBytes < 0 ? 0 : entry.SizeBytes,
            NormalizeOptionalValueOrEmpty(entry.LastModified, 64),
            entry.IsDirectory,
            NormalizeOptionalValue(entry.DriveLetter, 16)))
        .DistinctBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static List<RecoveryFileRecord> FilterRecoveryEntriesWithinApprovedLocations(
    IReadOnlyList<RecoveryFileRecord> entries,
    IReadOnlyList<RecoveryApprovedLocationRecord> approvedLocations)
{
    var approvedRoots = approvedLocations
        .Select(location => NormalizeRecoveryPath(location.FullPath))
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    return entries
        .Select(entry =>
        {
            var normalizedPath = NormalizeRecoveryPath(entry.FullPath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || IsUnsafeRecoveryPath(normalizedPath))
            {
                return null;
            }

            var approvedRoot = approvedRoots.FirstOrDefault(root => IsEntryWithinApprovedRoot(normalizedPath, root));
            if (string.IsNullOrWhiteSpace(approvedRoot))
            {
                return null;
            }

            var normalizedRootPath = NormalizeRecoveryPath(entry.RootPath);
            if (string.IsNullOrWhiteSpace(normalizedRootPath) ||
                !IsEntryWithinApprovedRoot(normalizedPath, normalizedRootPath))
            {
                normalizedRootPath = BuildRootPathForEntry(normalizedPath);
            }

            var normalizedRootLabel = NormalizeOptionalValue(entry.RootLabel, 128);
            if (string.Equals(normalizedRootLabel, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                normalizedRootLabel = BuildRootLabelForEntry(normalizedRootPath);
            }

            return new RecoveryFileRecord(
                entry.FileName,
                normalizedPath,
                normalizedRootLabel,
                normalizedRootPath,
                entry.Extension,
                entry.SizeBytes,
                entry.LastModified,
                entry.IsDirectory,
                entry.DriveLetter);
        })
        .Where(entry => entry is not null)
        .Select(entry => entry!)
        .DistinctBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static string BuildRootPathForEntry(string fullPath)
{
    var normalizedPath = NormalizeRecoveryPath(fullPath);
    if (string.IsNullOrWhiteSpace(normalizedPath))
    {
        return string.Empty;
    }

    var segments = normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0)
    {
        return normalizedPath;
    }

    if (segments[0].Length == 2 &&
        segments[0][1] == ':' &&
        !segments[0].Equals("C:", StringComparison.OrdinalIgnoreCase))
    {
        return segments[0].ToUpperInvariant() + "\\";
    }

    if (segments.Length >= 4 &&
        segments[0].Equals("C:", StringComparison.OrdinalIgnoreCase) &&
        segments[1].Equals("Users", StringComparison.OrdinalIgnoreCase))
    {
        var folderIndex = segments[2].StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) ? 4 : 3;
        if (segments.Length > folderIndex)
        {
            var candidate = segments[folderIndex];
            if (candidate.Equals("Desktop", StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals("Documents", StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals("Downloads", StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals("Pictures", StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals("Videos", StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals("Music", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join("\\", segments.Take(folderIndex + 1));
            }
        }
    }

    if (segments[0].Length == 2 && segments[0][1] == ':')
    {
        return segments[0].ToUpperInvariant() + "\\";
    }

    return normalizedPath;
}

static string BuildRootLabelForEntry(string rootPath)
{
    var normalizedRootPath = NormalizeRecoveryPath(rootPath);
    if (string.IsNullOrWhiteSpace(normalizedRootPath))
    {
        return "Unknown";
    }

    var trimmed = normalizedRootPath.TrimEnd('\\');
    var segments = trimmed.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0)
    {
        return normalizedRootPath;
    }

    if (segments.Length == 1 && segments[0].Length == 2 && segments[0][1] == ':')
    {
        return segments[0].ToUpperInvariant();
    }

    return segments[^1];
}

static bool PathStartsWithRoot(string fullPath, string root)
{
    if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (!root.EndsWith('\\'))
    {
        root += "\\";
    }

    return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
}

static bool IsEntryWithinApprovedRoot(string fullPath, string approvedRoot)
{
    if (IsKnownRecoveryFolderToken(approvedRoot))
    {
        return approvedRoot.ToUpperInvariant() switch
        {
            "%FMD_DESKTOP%" => IsAllowedRecoveryUserFolderFamily(fullPath, "Desktop"),
            "%FMD_DOCUMENTS%" => IsAllowedRecoveryUserFolderFamily(fullPath, "Documents"),
            "%FMD_DOWNLOADS%" => IsAllowedRecoveryUserFolderFamily(fullPath, "Downloads"),
            "%FMD_PICTURES%" => IsAllowedRecoveryUserFolderFamily(fullPath, "Pictures"),
            "%FMD_VIDEOS%" => IsAllowedRecoveryUserFolderFamily(fullPath, "Videos"),
            "%FMD_MUSIC%" => IsAllowedRecoveryUserFolderFamily(fullPath, "Music"),
            _ => false,
        };
    }

    return PathStartsWithRoot(fullPath, approvedRoot);
}

static bool IsAllowedApprovedUserFolderPath(string normalizedPath)
{
    return IsAllowedApprovedUserFolderFamilyRoot(normalizedPath, "Desktop") ||
           IsAllowedApprovedUserFolderFamilyRoot(normalizedPath, "Documents") ||
           IsAllowedApprovedUserFolderFamilyRoot(normalizedPath, "Downloads") ||
           IsAllowedApprovedUserFolderFamilyRoot(normalizedPath, "Pictures") ||
           IsAllowedApprovedUserFolderFamilyRoot(normalizedPath, "Videos") ||
           IsAllowedApprovedUserFolderFamilyRoot(normalizedPath, "Music");
}

static bool IsAllowedApprovedDriveRoot(string normalizedPath)
{
    return Regex.IsMatch(normalizedPath, "^[D-Z]:\\\\$", RegexOptions.CultureInvariant);
}

static bool IsSupportedApprovedLocation(string? path)
{
    var normalizedPath = NormalizeRecoveryPath(path);
    if (string.IsNullOrWhiteSpace(normalizedPath))
    {
        return false;
    }

    if (IsKnownRecoveryFolderToken(normalizedPath))
    {
        return true;
    }

    if (IsUnsafeRecoveryPath(normalizedPath))
    {
        return false;
    }

    return IsAllowedApprovedUserFolderPath(normalizedPath) ||
           IsAllowedApprovedDriveRoot(normalizedPath);
}

static bool IsAllowedApprovedUserFolderFamilyRoot(string normalizedPath, string folderName)
{
    var segments = SplitPathSegments(normalizedPath);
    if (segments.Length < 4 ||
        !segments[0].Equals("C:", StringComparison.OrdinalIgnoreCase) ||
        !segments[1].Equals("Users", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (segments.Length == 4 &&
        segments[3].Equals(folderName, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return segments.Length == 5 &&
           segments[3].StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) &&
           segments[4].Equals(folderName, StringComparison.OrdinalIgnoreCase);
}

static bool IsAllowedRecoveryUserFolderFamily(string normalizedPath, string folderName)
{
    var segments = SplitPathSegments(normalizedPath);
    if (segments.Length < 4 ||
        !segments[0].Equals("C:", StringComparison.OrdinalIgnoreCase) ||
        !segments[1].Equals("Users", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (segments[3].Equals(folderName, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return segments.Length >= 5 &&
           segments[3].StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) &&
           segments[4].Equals(folderName, StringComparison.OrdinalIgnoreCase);
}

static bool IsUnsafeRecoveryPath(string normalizedPath)
{
    if (string.IsNullOrWhiteSpace(normalizedPath))
    {
        return true;
    }

    if (!Regex.IsMatch(normalizedPath, @"^[A-Z]:\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
    {
        return true;
    }

    if (normalizedPath.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase) ||
        normalizedPath.StartsWith(@"C:\Program Files", StringComparison.OrdinalIgnoreCase) ||
        normalizedPath.StartsWith(@"C:\Program Files (x86)", StringComparison.OrdinalIgnoreCase) ||
        normalizedPath.StartsWith(@"C:\ProgramData", StringComparison.OrdinalIgnoreCase) ||
        normalizedPath.StartsWith(@"C:\Recovery", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (normalizedPath.StartsWith(@"C:\Users\", StringComparison.OrdinalIgnoreCase) &&
        !IsAllowedRecoveryUserFolderPath(normalizedPath))
    {
        return true;
    }

    return false;
}

static bool IsAllowedRecoveryUserFolderPath(string normalizedPath)
{
    return IsAllowedRecoveryUserFolderFamily(normalizedPath, "Desktop") ||
           IsAllowedRecoveryUserFolderFamily(normalizedPath, "Documents") ||
           IsAllowedRecoveryUserFolderFamily(normalizedPath, "Downloads") ||
           IsAllowedRecoveryUserFolderFamily(normalizedPath, "Pictures") ||
           IsAllowedRecoveryUserFolderFamily(normalizedPath, "Videos") ||
           IsAllowedRecoveryUserFolderFamily(normalizedPath, "Music");
}

static string[] SplitPathSegments(string normalizedPath)
{
    return normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
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

static string NormalizeOptionalValueOrEmpty(string? value, int maxLength)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    var trimmed = value.Trim();
    return trimmed.Length <= maxLength
        ? trimmed
        : trimmed[..maxLength];
}

static string NormalizeRecoveryPath(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return string.Empty;
    }

    var normalized = path.Trim().Replace('/', '\\');
    if (IsKnownRecoveryFolderToken(normalized))
    {
        return normalized.ToUpperInvariant();
    }

    while (normalized.Contains(@"\\", StringComparison.Ordinal))
    {
        normalized = normalized.Replace(@"\\", @"\");
    }

    if (Regex.IsMatch(normalized, "^[A-Za-z]:$", RegexOptions.CultureInvariant))
    {
        return normalized.ToUpperInvariant() + "\\";
    }

    return normalized;
}

static bool IsKnownRecoveryFolderToken(string path)
{
    return path.Equals("%FMD_DESKTOP%", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("%FMD_DOCUMENTS%", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("%FMD_DOWNLOADS%", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("%FMD_PICTURES%", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("%FMD_VIDEOS%", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("%FMD_MUSIC%", StringComparison.OrdinalIgnoreCase);
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

static string ResolveContentType(string fileName, FileExtensionContentTypeProvider contentTypeProvider)
{
    var safeFileName = Path.GetFileName(fileName);
    if (!string.IsNullOrWhiteSpace(safeFileName) &&
        contentTypeProvider.TryGetContentType(safeFileName, out var contentType) &&
        !string.IsNullOrWhiteSpace(contentType))
    {
        return contentType;
    }

    return "application/octet-stream";
}

static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

record RegisterRequest(string? Email, string? Password);
record LoginRequest(string? Email, string? Password);

record AppUserRecord(
    Guid Id,
    string Email,
    string Token,
    string AgentSetupCode);

record OwnedDeviceRecord(
    Guid Id,
    Guid UserId,
    string HardwareDeviceId,
    string DeviceName);

record RecoverySettingsStorageRecord(
    bool Enabled,
    string ApprovedLocationsJson,
    string DeviceName,
    string LastSyncedAt,
    string SettingsUpdatedAt,
    string ScanRequestedAt);

record TransferJobStorageRecord(
    Guid Id,
    Guid UserId,
    Guid DeviceId,
    string JobType,
    string Status,
    string RequestedFileName,
    string StorageKey,
    string StorageFileName);

record TransferJobOwnershipValidation(
    NpgsqlConnection? Connection,
    TransferJobStorageRecord? Job,
    IResult? ErrorResult);

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

record RecoverySettingsRequest(
    string? SetupCode,
    string? AgentSetupCode,
    string? DeviceId,
    string? DeviceName,
    bool Enabled,
    List<RecoveryApprovedLocationRecord>? ApprovedLocations);

record RecoverySettingsResponse(
    string DeviceId,
    string DeviceName,
    bool Enabled,
    List<RecoveryApprovedLocationRecord> ApprovedLocations,
    string LastSyncedAt,
    string UpdatedAt,
    string ScanRequestedAt);

record RecoveryApprovedLocationRecord(
    string? Label,
    string? FullPath,
    string? DriveLetter,
    string? LocationType);

record AgentRecoverySettingsRequest(
    string? SetupCode,
    string? AgentSetupCode,
    string? DeviceId);

record RecoveryScanRequest(
    string? DeviceId);

record ResetAgentRequest(
    string? DeviceId);

record RecoveryFileListRequest(
    string? SetupCode,
    string? AgentSetupCode,
    string? DeviceId,
    string? DeviceName,
    List<RecoveryFileRecord>? Entries,
    int? BatchIndex,
    int? TotalBatches,
    bool ReplaceExisting,
    bool IsFinalBatch);

record RecoveryInventoryResponse(
    string DeviceId,
    string DeviceName,
    bool Enabled,
    List<RecoveryApprovedLocationRecord> ApprovedLocations,
    int TotalFiles,
    string LastScanTime,
    List<RecoveryFileRecord> Files);

record RecoveryFileRecord(
    string? FileName,
    string? FullPath,
    string? RootLabel,
    string? RootPath,
    string? Extension,
    long SizeBytes,
    string? LastModified,
    bool IsDirectory,
    string? DriveLetter);

record DownloadFromDeviceRequest(
    string? DeviceId,
    string? FilePath);

record TransferJobRecord(
    string Id,
    string JobType,
    string Status,
    string RequestedFilePath,
    string RequestedFileName,
    string DestinationPath,
    string StorageFileName,
    string ErrorMessage,
    string CreatedAt,
    string UpdatedAt,
    string CompletedAt);

record AgentJobPollRequest(
    string? SetupCode,
    string? AgentSetupCode,
    string? DeviceId);

record AgentTransferJobRecord(
    string Id,
    string JobType,
    string RequestedFilePath,
    string DestinationPath,
    string StorageFileName);

record AgentJobUpdateRequest(
    string? SetupCode,
    string? AgentSetupCode,
    string? DeviceId,
    string? LocalPath,
    string? ErrorMessage);

