using Betsi;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Serilog;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information("Starting Betsi Patient Flow MVP application...");

    var builder = WebApplication.CreateBuilder(args);

    // Add Serilog
    builder.Host.UseSerilog();

    // Add services
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // Add MediatR for command dispatch
    builder.Services.AddMediatR(cfg => 
        cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();
    app.MapControllers();

    Log.Information("Betsi Patient Flow MVP started successfully");
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "An unhandled exception occurred");
}
finally
{
    Log.CloseAndFlush();
}
