using Cefiro.Core.Hardware;
using Cefiro.Core.Runtime;
using Cefiro.Demo;
using Cefiro.Hardware.Lhm;
using Cefiro.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

// Uso:
//   Cefiro.Service.exe            → como servicio de Windows (lo instala el instalador)
//   Cefiro.Service.exe --console  → en una consola (desarrollo; para el hardware real, como administrador)
//   Cefiro.Service.exe --demo     → hardware simulado, sin tocar nada real
var demo = args.Contains("--demo");
var pipeName = GetArg(args, "--pipe") ?? Cefiro.Ipc.Contracts.IpcConstants.PipeName;

var dataDirectory = demo
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cefiro", "demo")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Cefiro");
Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(dataDirectory, "logs", "servicio-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

CefiroRuntime? runtimeForCrash = null;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    // Pase lo que pase, los ventiladores vuelven a la BIOS antes de que el proceso muera.
    runtimeForCrash?.EmergencyRelease();
    Log.Fatal(e.ExceptionObject as Exception, "Excepción no controlada: el servicio se cierra");
    Log.CloseAndFlush();
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Log.Error(e.Exception, "Excepción no observada en una tarea");
    e.SetObserved();
};

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.AddSerilog(Log.Logger, dispose: false);
    builder.Services.AddWindowsService(options => options.ServiceName = "Cefiro");
    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

    builder.Services.AddSingleton(new RuntimeOptions { DemoMode = demo, DataDirectory = dataDirectory });
    builder.Services.AddSingleton(new PipeSettings(pipeName));
    builder.Services.AddSingleton<IHardwareBackend>(sp => demo
        ? new DemoHardwareBackend()
        : new LhmHardwareBackend(sp.GetRequiredService<ILogger<LhmHardwareBackend>>()));
    builder.Services.AddSingleton<CefiroRuntime>();
    builder.Services.AddHostedService<CefiroHostedService>();

    var host = builder.Build();
    runtimeForCrash = host.Services.GetRequiredService<CefiroRuntime>();

    Log.Information("Céfiro {Version} arrancando (demo: {Demo}, pipe: {Pipe}, datos: {Data})",
        runtimeForCrash.Options.ServiceVersion, demo, pipeName, dataDirectory);

    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    runtimeForCrash?.EmergencyRelease();
    Log.Fatal(ex, "El servicio no pudo arrancar");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

static string? GetArg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
