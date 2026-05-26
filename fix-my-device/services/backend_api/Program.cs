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
                @created_at,
                @updated_at
            )
            on conflict (user_id, device_id)
            do update set
                device_name = excluded.device_name,
                enabled = excluded.enabled,
                approved_locations = excluded.approved_locations,
                last_synced_at = excluded.last_synced_at,
                updated_at = excluded.updated_at
            """,
            connection);

        upsertCommand.Parameters.AddWithValue("id", Guid.NewGuid());
        upsertCommand.Parameters.AddWithValue("user_id", user.Id);
        upsertCommand.Parameters.AddWithValue("device_id", device.Id);
        upsertCommand.Parameters.AddWithValue("device_name", NormalizeOptionalValue(request.DeviceName, 200));
        upsertCommand.Parameters.AddWithValue("enabled", request.Enabled);
        upsertCommand.Parameters.Add(new NpgsqlParameter("approved_locations", NpgsqlDbType.Jsonb) { Value = approvedLocationsJson });
        upsertCommand.Parameters.AddWithValue("last_synced_at", now);
        upsertCommand.Parameters.AddWithValue("created_at", now);
        upsertCommand.Parameters.AddWithValue("updated_at", now);
        await upsertCommand.ExecuteNonQueryAsync();

        return Results.Ok(new
        {
            message = "Emergency recovery settings saved successfully.",
            enabled = request.Enabled,
            approvedLocations,
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
                string.Empty));
        }

        var enabled = reader["enabled"] is bool enabledValue && enabledValue;
        var approvedLocations = DeserializeApprovedLocations(reader["approved_locations"]?.ToString());
        var lastSyncedAt = ToIsoString(reader["last_synced_at"]);

        return Results.Ok(new RecoverySettingsResponse(
            device.Id.ToString(),
            device.DeviceName,
            enabled,
            approvedLocations,
            lastSyncedAt));
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

app.MapPost("/api/recovery/upload", async (RecoveryFileListRequest request) =>
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
            await using var insertCommand = new NpgsqlCommand(
                """
                insert into recovery_file_listings (
                    id,
                    user_id,
                    device_id,
                    file_name,
                    full_path,
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
            insertCommand.Parameters.AddWithValue("extension", entry.Extension ?? string.Empty);
            insertCommand.Parameters.AddWithValue("size_bytes", entry.SizeBytes);
            insertCommand.Parameters.AddWithValue("last_modified_at",
                string.IsNullOrWhiteSpace(entry.LastModified)
                    ? DBNull.Value
                    : DateTimeOffset.Parse(entry.LastModified));
            insertCommand.Parameters.AddWithValue("is_directory", entry.IsDirectory);
            insertCommand.Parameters.AddWithValue("drive_letter", entry.DriveLetter ?? string.Empty);
            insertCommand.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
            insertCommand.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
            await insertCommand.ExecuteNonQueryAsync();
        }

        await using (var updateSettingsCommand = new NpgsqlCommand(
            """
            update recovery_settings
            set device_name = @device_name,
                last_synced_at = @last_synced_at,
                updated_at = @updated_at
            where user_id = @user_id
              and device_id = @device_id
            """,
            connection,
            transaction))
        {
            updateSettingsCommand.Parameters.AddWithValue("device_name", NormalizeOptionalValue(request.DeviceName, 200));
            updateSettingsCommand.Parameters.AddWithValue("last_synced_at", DateTimeOffset.UtcNow);
            updateSettingsCommand.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
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
});

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
        select enabled, approved_locations
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
            reader["approved_locations"]?.ToString() ?? "[]")
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

    if (request.Entries.Count > 5000)
    {
        message = "Too many recovery file entries were submitted.";
        return false;
    }

    foreach (var entry in request.Entries)
    {
        if (!ValidateRequiredField("File name", entry.FileName, 260, out message) ||
            !ValidateRequiredField("Full path", entry.FullPath, 400, out message) ||
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
        .Where(entry =>
        {
            var normalizedPath = NormalizeRecoveryPath(entry.FullPath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || IsUnsafeRecoveryPath(normalizedPath))
            {
                return false;
            }

            return approvedRoots.Any(root => IsEntryWithinApprovedRoot(normalizedPath, root));
        })
        .DistinctBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
        .ToList();
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
    string ApprovedLocationsJson);

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
    string LastSyncedAt);

record RecoveryApprovedLocationRecord(
    string? Label,
    string? FullPath,
    string? DriveLetter,
    string? LocationType);

record RecoveryFileListRequest(
    string? SetupCode,
    string? AgentSetupCode,
    string? DeviceId,
    string? DeviceName,
    List<RecoveryFileRecord>? Entries);

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
    string? Extension,
    long SizeBytes,
    string? LastModified,
    bool IsDirectory,
    string? DriveLetter);
