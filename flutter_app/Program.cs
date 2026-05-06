using FixMyDeviceAgent.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5055");

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<WindowsDeviceInfoService>();

var app = builder.Build();

app.UseCors();

app.MapGet("/api/device-info", (WindowsDeviceInfoService service) =>
{
    return Results.Ok(service.GetDeviceInfo());
});

app.Run();
