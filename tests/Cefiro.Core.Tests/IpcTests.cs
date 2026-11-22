using System.Text.Json;
using Cefiro.Core.Runtime;
using Cefiro.Demo;
using Cefiro.Ipc;
using Cefiro.Ipc.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cefiro.Core.Tests;

public class IpcTests
{
    private sealed class EchoHandler : IRequestHandler, IEventSource
    {
        public event EventHandler<IpcEvent>? EventPublished;

        public Task<JsonElement?> HandleAsync(string method, JsonElement? payload, CancellationToken cancellationToken)
        {
            if (method == "boom")
                throw new InvalidOperationException("fallo a propósito");

            return Task.FromResult<JsonElement?>(CefiroJson.ToElement(new { method, echo = payload?.GetRawText() }));
        }

        public void Raise(string name, object payload) =>
            EventPublished?.Invoke(this, new IpcEvent(name, CefiroJson.ToElement(payload)));
    }

    [Fact]
    public async Task Pipe_round_trip_requests_errors_and_events()
    {
        var pipeName = $"cefiro-test-{Guid.NewGuid():N}";
        var handler = new EchoHandler();
        await using var server = new PipeServer(pipeName, handler);
        handler.EventPublished += (_, e) => server.Publish(e);
        server.Start();

        await using var client = new PipeConnection(pipeName);
        var events = new List<IpcEvent>();
        var gotEvent = new TaskCompletionSource();
        client.EventReceived += (_, e) => { events.Add(e); gotEvent.TrySetResult(); };
        await client.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(await client.WaitForConnectionAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var result = await client.InvokeAsync("hola", CefiroJson.ToElement(new { x = 1 }), TestContext.Current.CancellationToken);
        Assert.Equal("hola", result!.Value.GetProperty("method").GetString());

        var error = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            client.InvokeAsync("boom", null, TestContext.Current.CancellationToken));
        Assert.Contains("fallo a propósito", error.Message);

        // Espera a que el servidor registre al cliente antes de publicar.
        for (var i = 0; i < 50 && server.ClientCount == 0; i++)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        handler.Raise("ping", new { n = 7 });
        await gotEvent.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("ping", events[0].Name);
    }

    [Fact]
    public async Task Runtime_with_demo_hardware_serves_catalog_history_and_frames()
    {
        var options = new RuntimeOptions
        {
            DemoMode = true,
            DataDirectory = Path.Combine(Path.GetTempPath(), "cefiro-tests", Guid.NewGuid().ToString("N")),
            TickInterval = TimeSpan.FromMilliseconds(100),
        };

        await using var runtime = new CefiroRuntime(new DemoHardwareBackend(), options, NullLogger<CefiroRuntime>.Instance);
        await using var api = new CefiroApi(new InProcessConnection(runtime, runtime));
        var frames = 0;
        api.SensorFrame += (_, _) => Interlocked.Increment(ref frames);
        await api.StartAsync(TestContext.Current.CancellationToken);
        await runtime.StartAsync(TestContext.Current.CancellationToken);

        var hello = await api.HelloAsync("test", TestContext.Current.CancellationToken);
        Assert.True(hello.DemoMode);

        var catalog = await api.GetCatalogAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(catalog.Sensors);
        Assert.NotNull(catalog.Keys.CpuTemperature);
        Assert.NotNull(catalog.Keys.GpuTemperature);

        // Igual que en el PC real: la bomba en CPU_FAN1 y el radiador en PUMP_FAN1.
        Assert.Equal(FanRole.Pump, catalog.FanChannels.Single(f => f.Header == "CPU_FAN1").DetectedRole);
        Assert.Equal(FanRole.Radiator, catalog.FanChannels.Single(f => f.Header == "PUMP_FAN1").DetectedRole);

        await Task.Delay(700, TestContext.Current.CancellationToken);
        Assert.True(Volatile.Read(ref frames) >= 3, $"tramas recibidas: {frames}");

        var history = await api.GetHistoryAsync(
            new HistoryRequest([catalog.Keys.CpuTemperature!], HistoryRange.Last60Seconds), TestContext.Current.CancellationToken);
        Assert.Single(history.Series);
        Assert.Contains(history.Series[0].Values, v => v is > 20 and < 110);

        var status = await api.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.True(status.HardwareReady);
    }
}
