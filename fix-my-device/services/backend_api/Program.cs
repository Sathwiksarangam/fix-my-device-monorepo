using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Npgsql;
using NpgsqlTypes;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFlutterApp", policy =>
    {
        policy.AllowAnyOrigin()
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
var schemaCache = new ConcurrentDictionary<string, TableSchema>(StringComparer.OrdinalIgnoreCase);

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
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { message = "Email and password are required." });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var usersSchema = await GetSchemaAsync(connection, "app_users", schemaCache);
        var usersColumns = ResolveUserColumns(usersSchema);

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var passwordHash = request.Password.Trim();
        var token = Guid.NewGuid().ToString("N");
        var agentSetupCode = GenerateAgentSetupCode();
        var createdAt = DateTimeOffset.UtcNow;
        var userId = CreateIdString();

        await using var existsCommand = new NpgsqlCommand(
            $"select 1 from {usersSchema.QualifiedName} where lower({usersColumns.Email.Sql}) = @email limit 1",
            connection);
        existsCommand.Parameters.AddWithValue("email", normalizedEmail);

        var existingUser = await existsCommand.ExecuteScalarAsync();
        if (existingUser is not null)
        {
            return Results.Conflict(new { message = "User already exists." });
        }

        var insertColumns = new[]
        {
            usersColumns.Id,
            usersColumns.Email,
            usersColumns.PasswordHash,
            usersColumns.Token,
            usersColumns.AgentSetupCode,
            usersColumns.CreatedAt,
        };

        var insertSql = $"insert into {usersSchema.QualifiedName} ({string.Join(", ", insertColumns.Select(column => column.Sql))}) " +
                        $"values ({string.Join(", ", insertColumns.Select(column => $"@{column.ParameterName}"))})";

        await using var insertCommand = new NpgsqlCommand(insertSql, connection);
        AddColumnParameter(insertCommand, usersColumns.Id, userId);
        AddColumnParameter(insertCommand, usersColumns.Email, normalizedEmail);
        AddColumnParameter(insertCommand, usersColumns.PasswordHash, passwordHash);
        AddColumnParameter(insertCommand, usersColumns.Token, token);
        AddColumnParameter(insertCommand, usersColumns.AgentSetupCode, agentSetupCode);
        AddColumnParameter(insertCommand, usersColumns.CreatedAt, createdAt);
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
        return Results.Problem(title: "Register failed", detail: ex.ToString(), statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/auth/login", async (LoginRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { message = "Email and password are required." });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var usersSchema = await GetSchemaAsync(connection, "app_users", schemaCache);
        var usersColumns = ResolveUserColumns(usersSchema);

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var passwordHash = request.Password.Trim();

        var selectSql = $"select " +
                        $"{usersColumns.Id.Sql} as id, " +
                        $"{usersColumns.Email.Sql} as email, " +
                        $"{usersColumns.Token.Sql} as token, " +
                        $"{usersColumns.AgentSetupCode.Sql} as agent_setup_code " +
                        $"from {usersSchema.QualifiedName} " +
                        $"where lower({usersColumns.Email.Sql}) = @email and {usersColumns.PasswordHash.Sql} = @password_hash limit 1";

        await using var selectCommand = new NpgsqlCommand(selectSql, connection);
        selectCommand.Parameters.AddWithValue("email", normalizedEmail);
        selectCommand.Parameters.AddWithValue("password_hash", passwordHash);

        await using var reader = await selectCommand.ExecuteReaderAsync(CommandBehavior.SingleRow);
        if (!await reader.ReadAsync())
        {
            return Results.Unauthorized();
        }

        var userId = reader["id"]?.ToString() ?? string.Empty;
        var email = reader["email"]?.ToString() ?? normalizedEmail;
        var token = reader["token"]?.ToString() ?? string.Empty;
        var agentSetupCode = reader["agent_setup_code"]?.ToString() ?? string.Empty;
        await reader.CloseAsync();

        if (string.IsNullOrWhiteSpace(token))
        {
            token = Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(agentSetupCode))
        {
            agentSetupCode = GenerateAgentSetupCode();
        }

        var updateSql = $"update {usersSchema.QualifiedName} set " +
                        $"{usersColumns.Token.Sql} = @token, " +
                        $"{usersColumns.AgentSetupCode.Sql} = @agent_setup_code " +
                        $"where {usersColumns.Id.Sql} = @id";

        await using var updateCommand = new NpgsqlCommand(updateSql, connection);
        AddColumnParameter(updateCommand, usersColumns.Token, token);
        AddColumnParameter(updateCommand, usersColumns.AgentSetupCode, agentSetupCode);
        AddColumnParameter(updateCommand, usersColumns.Id, userId);
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
        return Results.Problem(title: "Login failed", detail: ex.ToString(), statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/agent/setup-code", async (HttpRequest request) =>
{
    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var usersSchema = await GetSchemaAsync(connection, "app_users", schemaCache);
        var usersColumns = ResolveUserColumns(usersSchema);
        var user = await TryGetAuthorizedUserAsync(request, connection, usersSchema, usersColumns);

        if (user is null)
        {
            return Results.Unauthorized();
        }

        var agentSetupCode = string.IsNullOrWhiteSpace(user.AgentSetupCode)
            ? GenerateAgentSetupCode()
            : user.AgentSetupCode;

        if (!string.Equals(agentSetupCode, user.AgentSetupCode, StringComparison.Ordinal))
        {
            var updateSql = $"update {usersSchema.QualifiedName} set {usersColumns.AgentSetupCode.Sql} = @agent_setup_code where {usersColumns.Id.Sql} = @id";
            await using var updateCommand = new NpgsqlCommand(updateSql, connection);
            AddColumnParameter(updateCommand, usersColumns.AgentSetupCode, agentSetupCode);
            AddColumnParameter(updateCommand, usersColumns.Id, user.Id);
            await updateCommand.ExecuteNonQueryAsync();
        }

        return Results.Ok(new { agentSetupCode });
    }
    catch (Exception ex)
    {
        Console.WriteLine("Get setup code failed:");
        Console.WriteLine(ex);
        return Results.Problem(title: "Get setup code failed", detail: ex.ToString(), statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/devices", async (HttpRequest request) =>
{
    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var usersSchema = await GetSchemaAsync(connection, "app_users", schemaCache);
        var usersColumns = ResolveUserColumns(usersSchema);
        var user = await TryGetAuthorizedUserAsync(request, connection, usersSchema, usersColumns);

        if (user is null)
        {
            return Results.Unauthorized();
        }

        var devicesSchema = await GetSchemaAsync(connection, "devices", schemaCache);
        var devicesColumns = ResolveDeviceColumns(devicesSchema);
        var devices = new List<DeviceRecord>();

        var selectSql = $"select " +
                        $"{devicesColumns.Id.Sql} as id, " +
                        $"{devicesColumns.DeviceName.Sql} as device_name, " +
                        $"{devicesColumns.Processor.Sql} as processor, " +
                        $"{devicesColumns.ProcessorSpeed.Sql} as processor_speed, " +
                        $"{devicesColumns.InstalledRam.Sql} as installed_ram, " +
                        $"{devicesColumns.UsableRam.Sql} as usable_ram, " +
                        $"{devicesColumns.GraphicsCard.Sql} as graphics_card, " +
                        $"{devicesColumns.GraphicsMemory.Sql} as graphics_memory, " +
                        $"{devicesColumns.TotalStorage.Sql} as total_storage, " +
                        $"{devicesColumns.UsedStorage.Sql} as used_storage, " +
                        $"{devicesColumns.FreeStorage.Sql} as free_storage, " +
                        $"{devicesColumns.DeviceId.Sql} as device_id, " +
                        $"{devicesColumns.ProductId.Sql} as product_id, " +
                        $"{devicesColumns.SystemType.Sql} as system_type, " +
                        $"{devicesColumns.WindowsEdition.Sql} as windows_edition, " +
                        $"{devicesColumns.WindowsVersion.Sql} as windows_version, " +
                        $"{devicesColumns.OsBuild.Sql} as os_build, " +
                        $"{devicesColumns.InstalledOn.Sql} as installed_on, " +
                        $"{devicesColumns.Status.Sql} as status, " +
                        $"{devicesColumns.LastSeenAt.Sql} as last_seen_at, " +
                        $"{devicesColumns.DrivesJson.Sql} as drives_json " +
                        $"from {devicesSchema.QualifiedName} " +
                        $"where {devicesColumns.UserId.Sql} = @user_id " +
                        $"order by {devicesColumns.LastSeenAt.Sql} desc nulls last";

        await using var command = new NpgsqlCommand(selectSql, connection);
        AddColumnParameter(command, devicesColumns.UserId, user.Id);

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
        return Results.Problem(title: "Get devices failed", detail: ex.ToString(), statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/devices/system-info-by-code", async (DeviceSystemInfoRequest incomingDevice) =>
{
    if (incomingDevice is null)
    {
        return Results.BadRequest(new { message = "Device payload is required." });
    }

    var setupCode = NormalizeSetupCode(incomingDevice.SetupCode ?? incomingDevice.AgentSetupCode);
    if (string.IsNullOrWhiteSpace(setupCode))
    {
        return Results.BadRequest(new { message = "Agent setup code is required." });
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var usersSchema = await GetSchemaAsync(connection, "app_users", schemaCache);
        var usersColumns = ResolveUserColumns(usersSchema);
        var devicesSchema = await GetSchemaAsync(connection, "devices", schemaCache);
        var devicesColumns = ResolveDeviceColumns(devicesSchema);

        var user = await FindUserBySetupCodeAsync(connection, usersSchema, usersColumns, setupCode);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var normalizedDeviceId = ValueOrUnknown(incomingDevice.DeviceId);
        var existingDeviceId = await FindExistingDeviceIdAsync(connection, devicesSchema, devicesColumns, user.Id, normalizedDeviceId);
        var storedDeviceId = existingDeviceId ?? CreateIdString();
        var drives = NormalizeDrives(incomingDevice.Drives);
        var drivesJson = JsonSerializer.Serialize(drives);
        var lastSeenAt = DateTimeOffset.UtcNow;
        var status = string.IsNullOrWhiteSpace(incomingDevice.Status) ? "Online" : incomingDevice.Status.Trim();

        if (string.IsNullOrWhiteSpace(existingDeviceId))
        {
            var insertColumns = new[]
            {
                devicesColumns.Id,
                devicesColumns.UserId,
                devicesColumns.DeviceName,
                devicesColumns.Processor,
                devicesColumns.ProcessorSpeed,
                devicesColumns.InstalledRam,
                devicesColumns.UsableRam,
                devicesColumns.GraphicsCard,
                devicesColumns.GraphicsMemory,
                devicesColumns.TotalStorage,
                devicesColumns.UsedStorage,
                devicesColumns.FreeStorage,
                devicesColumns.DeviceId,
                devicesColumns.ProductId,
                devicesColumns.SystemType,
                devicesColumns.WindowsEdition,
                devicesColumns.WindowsVersion,
                devicesColumns.OsBuild,
                devicesColumns.InstalledOn,
                devicesColumns.Status,
                devicesColumns.LastSeenAt,
                devicesColumns.DrivesJson,
            };

            var insertSql = $"insert into {devicesSchema.QualifiedName} ({string.Join(", ", insertColumns.Select(column => column.Sql))}) " +
                            $"values ({string.Join(", ", insertColumns.Select(column => $"@{column.ParameterName}"))})";

            await using var insertCommand = new NpgsqlCommand(insertSql, connection);
            AddDeviceParameters(insertCommand, devicesColumns, storedDeviceId, user.Id, incomingDevice, normalizedDeviceId, status, lastSeenAt, drivesJson);
            await insertCommand.ExecuteNonQueryAsync();
        }
        else
        {
            var updateColumns = new[]
            {
                devicesColumns.DeviceName,
                devicesColumns.Processor,
                devicesColumns.ProcessorSpeed,
                devicesColumns.InstalledRam,
                devicesColumns.UsableRam,
                devicesColumns.GraphicsCard,
                devicesColumns.GraphicsMemory,
                devicesColumns.TotalStorage,
                devicesColumns.UsedStorage,
                devicesColumns.FreeStorage,
                devicesColumns.DeviceId,
                devicesColumns.ProductId,
                devicesColumns.SystemType,
                devicesColumns.WindowsEdition,
                devicesColumns.WindowsVersion,
                devicesColumns.OsBuild,
                devicesColumns.InstalledOn,
                devicesColumns.Status,
                devicesColumns.LastSeenAt,
                devicesColumns.DrivesJson,
            };

            var updateSql = $"update {devicesSchema.QualifiedName} set " +
                            string.Join(", ", updateColumns.Select(column => $"{column.Sql} = @{column.ParameterName}")) +
                            $" where {devicesColumns.Id.Sql} = @id and {devicesColumns.UserId.Sql} = @user_id";

            await using var updateCommand = new NpgsqlCommand(updateSql, connection);
            AddDeviceParameters(updateCommand, devicesColumns, storedDeviceId, user.Id, incomingDevice, normalizedDeviceId, status, lastSeenAt, drivesJson);
            await updateCommand.ExecuteNonQueryAsync();
        }

        return Results.Ok(new
        {
            message = "System info saved successfully",
            device = new DeviceRecord(
                storedDeviceId,
                ValueOrUnknown(incomingDevice.DeviceName),
                ValueOrUnknown(incomingDevice.Processor),
                ValueOrUnknown(incomingDevice.ProcessorSpeed),
                ValueOrUnknown(incomingDevice.InstalledRam),
                ValueOrUnknown(incomingDevice.UsableRam),
                ValueOrUnknown(incomingDevice.GraphicsCard),
                ValueOrUnknown(incomingDevice.GraphicsMemory),
                ValueOrUnknown(incomingDevice.TotalStorage),
                ValueOrUnknown(incomingDevice.UsedStorage),
                ValueOrUnknown(incomingDevice.FreeStorage),
                normalizedDeviceId,
                ValueOrUnknown(incomingDevice.ProductId),
                ValueOrUnknown(incomingDevice.SystemType),
                ValueOrUnknown(incomingDevice.WindowsEdition),
                ValueOrUnknown(incomingDevice.WindowsVersion),
                ValueOrUnknown(incomingDevice.OsBuild),
                ValueOrUnknown(incomingDevice.InstalledOn),
                status,
                lastSeenAt.ToString("O"),
                drives),
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine("System info save failed:");
        Console.WriteLine(ex);
        return Results.Problem(title: "Device save failed", detail: ex.ToString(), statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Run();

static async Task<TableSchema> GetSchemaAsync(
    NpgsqlConnection connection,
    string tableName,
    ConcurrentDictionary<string, TableSchema> schemaCache)
{
    if (schemaCache.TryGetValue(tableName, out var cachedSchema))
    {
        return cachedSchema;
    }

    const string schemaSql = """
        select column_name, data_type, udt_name
        from information_schema.columns
        where table_schema = 'public' and table_name = @table_name
        order by ordinal_position
        """;

    await using var command = new NpgsqlCommand(schemaSql, connection);
    command.Parameters.AddWithValue("table_name", tableName);

    var columns = new List<ColumnInfo>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        columns.Add(new ColumnInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2)));
    }

    if (columns.Count == 0)
    {
        throw new InvalidOperationException($"Table '{tableName}' was not found in public schema.");
    }

    var schema = new TableSchema("public", tableName, columns);
    schemaCache[tableName] = schema;
    return schema;
}

static UserColumns ResolveUserColumns(TableSchema schema)
{
    return new UserColumns(
        schema.RequireColumn("id"),
        schema.RequireColumn("email"),
        schema.RequireColumn("password_hash", "passwordHash"),
        schema.RequireColumn("token"),
        schema.RequireColumn("agent_setup_code", "agentSetupCode"),
        schema.RequireColumn("created_at", "createdAt"));
}

static DeviceColumns ResolveDeviceColumns(TableSchema schema)
{
    return new DeviceColumns(
        schema.RequireColumn("id"),
        schema.RequireColumn("user_id", "userId"),
        schema.RequireColumn("device_name", "deviceName"),
        schema.RequireColumn("processor"),
        schema.RequireColumn("processor_speed", "processorSpeed"),
        schema.RequireColumn("installed_ram", "installedRam"),
        schema.RequireColumn("usable_ram", "usableRam"),
        schema.RequireColumn("graphics_card", "graphicsCard"),
        schema.RequireColumn("graphics_memory", "graphicsMemory"),
        schema.RequireColumn("total_storage", "totalStorage"),
        schema.RequireColumn("used_storage", "usedStorage"),
        schema.RequireColumn("free_storage", "freeStorage"),
        schema.RequireColumn("device_id", "deviceId"),
        schema.RequireColumn("product_id", "productId"),
        schema.RequireColumn("system_type", "systemType"),
        schema.RequireColumn("windows_edition", "windowsEdition"),
        schema.RequireColumn("windows_version", "windowsVersion"),
        schema.RequireColumn("os_build", "osBuild"),
        schema.RequireColumn("installed_on", "installedOn"),
        schema.RequireColumn("status"),
        schema.RequireColumn("last_seen_at", "lastSeenAt"),
        schema.RequireColumn("drives_json", "drivesJson"));
}

static async Task<AppUserRecord?> TryGetAuthorizedUserAsync(
    HttpRequest request,
    NpgsqlConnection connection,
    TableSchema usersSchema,
    UserColumns usersColumns)
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
    if (string.IsNullOrWhiteSpace(token))
    {
        return null;
    }

    var selectSql = $"select " +
                    $"{usersColumns.Id.Sql} as id, " +
                    $"{usersColumns.Email.Sql} as email, " +
                    $"{usersColumns.Token.Sql} as token, " +
                    $"{usersColumns.AgentSetupCode.Sql} as agent_setup_code " +
                    $"from {usersSchema.QualifiedName} where {usersColumns.Token.Sql} = @token limit 1";

    await using var command = new NpgsqlCommand(selectSql, connection);
    AddColumnParameter(command, usersColumns.Token, token);

    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
    return await reader.ReadAsync()
        ? new AppUserRecord(
            reader["id"]?.ToString() ?? string.Empty,
            reader["email"]?.ToString() ?? string.Empty,
            reader["token"]?.ToString() ?? string.Empty,
            reader["agent_setup_code"]?.ToString() ?? string.Empty)
        : null;
}

static async Task<AppUserRecord?> FindUserBySetupCodeAsync(
    NpgsqlConnection connection,
    TableSchema usersSchema,
    UserColumns usersColumns,
    string setupCode)
{
    var selectSql = $"select " +
                    $"{usersColumns.Id.Sql} as id, " +
                    $"{usersColumns.Email.Sql} as email, " +
                    $"{usersColumns.Token.Sql} as token, " +
                    $"{usersColumns.AgentSetupCode.Sql} as agent_setup_code " +
                    $"from {usersSchema.QualifiedName} where upper({usersColumns.AgentSetupCode.Sql}) = @setup_code limit 1";

    await using var command = new NpgsqlCommand(selectSql, connection);
    command.Parameters.AddWithValue("setup_code", setupCode);

    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
    return await reader.ReadAsync()
        ? new AppUserRecord(
            reader["id"]?.ToString() ?? string.Empty,
            reader["email"]?.ToString() ?? string.Empty,
            reader["token"]?.ToString() ?? string.Empty,
            reader["agent_setup_code"]?.ToString() ?? string.Empty)
        : null;
}

static async Task<string?> FindExistingDeviceIdAsync(
    NpgsqlConnection connection,
    TableSchema devicesSchema,
    DeviceColumns devicesColumns,
    string userId,
    string deviceId)
{
    var selectSql = $"select {devicesColumns.Id.Sql} from {devicesSchema.QualifiedName} " +
                    $"where {devicesColumns.UserId.Sql} = @user_id and {devicesColumns.DeviceId.Sql} = @device_id limit 1";

    await using var command = new NpgsqlCommand(selectSql, connection);
    AddColumnParameter(command, devicesColumns.UserId, userId);
    AddColumnParameter(command, devicesColumns.DeviceId, deviceId);

    var result = await command.ExecuteScalarAsync();
    return result?.ToString();
}

static void AddDeviceParameters(
    NpgsqlCommand command,
    DeviceColumns devicesColumns,
    string storedDeviceId,
    string userId,
    DeviceSystemInfoRequest incomingDevice,
    string normalizedDeviceId,
    string status,
    DateTimeOffset lastSeenAt,
    string drivesJson)
{
    AddColumnParameter(command, devicesColumns.Id, storedDeviceId);
    AddColumnParameter(command, devicesColumns.UserId, userId);
    AddColumnParameter(command, devicesColumns.DeviceName, ValueOrUnknown(incomingDevice.DeviceName));
    AddColumnParameter(command, devicesColumns.Processor, ValueOrUnknown(incomingDevice.Processor));
    AddColumnParameter(command, devicesColumns.ProcessorSpeed, ValueOrUnknown(incomingDevice.ProcessorSpeed));
    AddColumnParameter(command, devicesColumns.InstalledRam, ValueOrUnknown(incomingDevice.InstalledRam));
    AddColumnParameter(command, devicesColumns.UsableRam, ValueOrUnknown(incomingDevice.UsableRam));
    AddColumnParameter(command, devicesColumns.GraphicsCard, ValueOrUnknown(incomingDevice.GraphicsCard));
    AddColumnParameter(command, devicesColumns.GraphicsMemory, ValueOrUnknown(incomingDevice.GraphicsMemory));
    AddColumnParameter(command, devicesColumns.TotalStorage, ValueOrUnknown(incomingDevice.TotalStorage));
    AddColumnParameter(command, devicesColumns.UsedStorage, ValueOrUnknown(incomingDevice.UsedStorage));
    AddColumnParameter(command, devicesColumns.FreeStorage, ValueOrUnknown(incomingDevice.FreeStorage));
    AddColumnParameter(command, devicesColumns.DeviceId, normalizedDeviceId);
    AddColumnParameter(command, devicesColumns.ProductId, ValueOrUnknown(incomingDevice.ProductId));
    AddColumnParameter(command, devicesColumns.SystemType, ValueOrUnknown(incomingDevice.SystemType));
    AddColumnParameter(command, devicesColumns.WindowsEdition, ValueOrUnknown(incomingDevice.WindowsEdition));
    AddColumnParameter(command, devicesColumns.WindowsVersion, ValueOrUnknown(incomingDevice.WindowsVersion));
    AddColumnParameter(command, devicesColumns.OsBuild, ValueOrUnknown(incomingDevice.OsBuild));
    AddColumnParameter(command, devicesColumns.InstalledOn, ValueOrUnknown(incomingDevice.InstalledOn));
    AddColumnParameter(command, devicesColumns.Status, status);
    AddColumnParameter(command, devicesColumns.LastSeenAt, lastSeenAt);
    AddColumnParameter(command, devicesColumns.DrivesJson, drivesJson);
}

static void AddColumnParameter(NpgsqlCommand command, ColumnRef column, object? value)
{
    var parameter = new NpgsqlParameter(column.ParameterName, GetColumnDbType(column.Column))
    {
        Value = ConvertValueForColumn(column.Column, value),
    };
    command.Parameters.Add(parameter);
}

static object ConvertValueForColumn(ColumnInfo column, object? value)
{
    if (value is null)
    {
        return DBNull.Value;
    }

    if (value is string stringValue)
    {
        if (column.IsUuid)
        {
            return Guid.Parse(stringValue);
        }

        if (column.IsTimestamp)
        {
            return DateTimeOffset.Parse(stringValue);
        }

        return stringValue;
    }

    if (value is Guid guidValue)
    {
        return column.IsUuid ? guidValue : guidValue.ToString();
    }

    if (value is DateTimeOffset dtoValue)
    {
        return column.IsTimestamp ? dtoValue : dtoValue.ToString("O");
    }

    if (value is DateTime dtValue)
    {
        return column.IsTimestamp ? dtValue : dtValue.ToString("O");
    }

    return value;
}

static NpgsqlDbType GetColumnDbType(ColumnInfo column)
{
    if (column.IsUuid)
    {
        return NpgsqlDbType.Uuid;
    }

    if (column.IsJsonb)
    {
        return NpgsqlDbType.Jsonb;
    }

    if (column.IsJson)
    {
        return NpgsqlDbType.Json;
    }

    if (column.IsTimestampTz)
    {
        return NpgsqlDbType.TimestampTz;
    }

    if (column.IsTimestamp)
    {
        return NpgsqlDbType.Timestamp;
    }

    return NpgsqlDbType.Text;
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
            ValueOrUnknown(drive.DriveLetter),
            ValueOrUnknown(drive.DriveType),
            ValueOrUnknown(drive.FileSystem),
            ValueOrUnknown(drive.VolumeLabel),
            ValueOrUnknown(drive.TotalSize),
            ValueOrUnknown(drive.UsedSpace),
            ValueOrUnknown(drive.FreeSpace)))
        .ToList();
}

static string GenerateAgentSetupCode()
{
    var raw = Guid.NewGuid().ToString("N").ToUpperInvariant();
    return $"FMD-{raw[..4]}-{raw[4..8]}";
}

static string NormalizeSetupCode(string? setupCode)
{
    return string.IsNullOrWhiteSpace(setupCode)
        ? string.Empty
        : setupCode.Trim().ToUpperInvariant();
}

static string ValueOrUnknown(string? value)
{
    return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
}

static string CreateIdString() => Guid.NewGuid().ToString();

record RegisterRequest(string? Email, string? Password);
record LoginRequest(string? Email, string? Password);
record AppUserRecord(string Id, string Email, string Token, string AgentSetupCode);

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

sealed record ColumnInfo(string Name, string DataType, string UdtName)
{
    public bool IsUuid => string.Equals(UdtName, "uuid", StringComparison.OrdinalIgnoreCase);
    public bool IsJsonb => string.Equals(UdtName, "jsonb", StringComparison.OrdinalIgnoreCase);
    public bool IsJson => IsJsonb || string.Equals(UdtName, "json", StringComparison.OrdinalIgnoreCase);
    public bool IsTimestampTz => string.Equals(UdtName, "timestamptz", StringComparison.OrdinalIgnoreCase);
    public bool IsTimestamp => IsTimestampTz || string.Equals(UdtName, "timestamp", StringComparison.OrdinalIgnoreCase);
}

sealed record ColumnRef(ColumnInfo Column)
{
    public string Sql => SqlIdentifier.Quote(Column.Name);
    public string ParameterName => Column.Name.Replace(' ', '_').Replace('-', '_');
}

sealed class TableSchema
{
    private readonly Dictionary<string, ColumnInfo> columnsByLookup;

    public TableSchema(string schemaName, string tableName, IReadOnlyList<ColumnInfo> columns)
    {
        SchemaName = schemaName;
        TableName = tableName;
        Columns = columns;
        columnsByLookup = columns.ToDictionary(
            column => column.Name,
            column => column,
            StringComparer.OrdinalIgnoreCase);
    }

    public string SchemaName { get; }
    public string TableName { get; }
    public IReadOnlyList<ColumnInfo> Columns { get; }
    public string QualifiedName => $"{SqlIdentifier.Quote(SchemaName)}.{SqlIdentifier.Quote(TableName)}";

    public ColumnRef RequireColumn(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (columnsByLookup.TryGetValue(candidate, out var column))
            {
                return new ColumnRef(column);
            }
        }

        throw new InvalidOperationException(
            $"Table '{TableName}' is missing required column. Expected one of: {string.Join(", ", candidates)}");
    }
}

static class SqlIdentifier
{
    public static string Quote(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }
}

sealed record UserColumns(
    ColumnRef Id,
    ColumnRef Email,
    ColumnRef PasswordHash,
    ColumnRef Token,
    ColumnRef AgentSetupCode,
    ColumnRef CreatedAt);

sealed record DeviceColumns(
    ColumnRef Id,
    ColumnRef UserId,
    ColumnRef DeviceName,
    ColumnRef Processor,
    ColumnRef ProcessorSpeed,
    ColumnRef InstalledRam,
    ColumnRef UsableRam,
    ColumnRef GraphicsCard,
    ColumnRef GraphicsMemory,
    ColumnRef TotalStorage,
    ColumnRef UsedStorage,
    ColumnRef FreeStorage,
    ColumnRef DeviceId,
    ColumnRef ProductId,
    ColumnRef SystemType,
    ColumnRef WindowsEdition,
    ColumnRef WindowsVersion,
    ColumnRef OsBuild,
    ColumnRef InstalledOn,
    ColumnRef Status,
    ColumnRef LastSeenAt,
    ColumnRef DrivesJson);
