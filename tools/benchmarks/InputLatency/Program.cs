using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using Xas.Core;
using Xas.Core.Protocol;
using Xas.Input;

// Synthetic TCP loopback benchmark. It exercises persistent, independently
// framed realtime and bulk lanes; it does not measure native input injection.
var duration = TimeSpan.FromSeconds(10);
var bulkBytesPerSecond = 20 * 1024 * 1024;
if (args.Length > 0 && double.TryParse(args[0], out var seconds) && seconds > 0)
    duration = TimeSpan.FromSeconds(seconds);
if (args.Length > 1 && int.TryParse(args[1], out var mbps) && mbps > 0)
    bulkBytesPerSecond = mbps * 1024 * 1024;

Console.WriteLine("Synthetic TCP loopback only; excludes OS input capture, remote network, and hardware injection.");
Console.WriteLine($"Duration/rate: {duration.TotalSeconds:0.#}s per case; bulk target {bulkBytesPerSecond / 1024 / 1024} MiB/s");
foreach (var rate in new[] { 125, 500, 1000 })
{
    var result = await RunCaseAsync(rate, duration, bulkBytesPerSecond);
    Console.WriteLine($"{rate,4} Hz  sent={result.Sent} echoed={result.Echoed} missing={result.Missing} " +
                      $"queueDrops={result.QueueDrops} bulk={result.BulkMiBps:0.0} MiB/s " +
                      $"RTT p50/p95/p99={result.P50:0.###}/{result.P95:0.###}/{result.P99:0.###} ms");
}

static async Task<CaseResult> RunCaseAsync(int rate, TimeSpan duration, int bulkBytesPerSecond)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start(2);
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var cts = new CancellationTokenSource(duration + TimeSpan.FromSeconds(3));
    var serverTask = RunServerAsync(listener, cts.Token);
    using var realtimeClient = new TcpClient();
    using var bulkClient = new TcpClient();
    await realtimeClient.ConnectAsync(IPAddress.Loopback, port, cts.Token);
    await bulkClient.ConnectAsync(IPAddress.Loopback, port, cts.Token);
    var realtime = new MultiplexedProtocolPeer(new BinaryFrameConnection(realtimeClient.GetStream(), leaveOpen: true),
        (_, _) => ValueTask.FromResult(new ProtocolMessage(MessageKind.Response, 0, 0, "", [])));
    var bulk = new MultiplexedProtocolPeer(new BinaryFrameConnection(bulkClient.GetStream(), leaveOpen: true),
        (_, _) => ValueTask.FromResult(new ProtocolMessage(MessageKind.Response, 0, 0, "", [])));

    var echoes = new ConcurrentDictionary<int, long>();
    realtime.MessageReceived += message =>
    {
        if (message.Method == "bench.echo" && message.Payload.Length == 16 + InputWire.EventBytes)
        {
            var id = BinaryPrimitives.ReadInt32BigEndian(message.Payload);
            var sent = BinaryPrimitives.ReadInt64BigEndian(message.Payload.AsSpan(4));
            echoes.TryAdd(id, Stopwatch.GetTimestamp() - sent);
        }
        return ValueTask.CompletedTask;
    };

    var sampleCount = (int)(rate * duration.TotalSeconds);
    var interval = Stopwatch.Frequency / (double)rate;
    var start = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var generator = Task.Run(async () =>
    {
        for (var i = 0; i < sampleCount; i++)
        {
            var due = start + (long)(i * interval);
            await DelayUntilAsync(due, cts.Token);
            var payload = new byte[16 + InputWire.EventBytes];
            BinaryPrimitives.WriteInt32BigEndian(payload, i);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(4), Stopwatch.GetTimestamp());
            InputWire.Encode([new InputEvent(InputEventKind.MoveAbsolute, X: i, Y: i)])
                .CopyTo(payload.AsSpan(16));
            await realtime.SendAsync(new ProtocolMessage(MessageKind.StreamData, 0, 1, "bench.input", payload), cts.Token);
        }
        completed.TrySetResult();
    }, cts.Token);

    var bulkPayload = new byte[64 * 1024];
    Random.Shared.NextBytes(bulkPayload);
    long bulkSent = 0;
    var bulkStop = new CancellationTokenSource(duration);
    var bulkTask = Task.Run(async () =>
    {
        var timer = Stopwatch.StartNew();
        while (!bulkStop.IsCancellationRequested)
        {
            try
            {
                await bulk.SendAsync(new ProtocolMessage(MessageKind.StreamData, 0, 2, "bench.bulk", bulkPayload), bulkStop.Token);
                bulkSent += bulkPayload.Length;
                var targetSeconds = bulkSent / (double)bulkBytesPerSecond;
                var wait = targetSeconds - timer.Elapsed.TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), bulkStop.Token);
            }
            catch (OperationCanceledException) { break; }
        }
    }, CancellationToken.None);

    await generator;
    await completed.Task;
    await Task.Delay(200);
    bulkStop.Cancel();
    try { await bulkTask; } catch (OperationCanceledException) { }
    cts.Cancel();
    try { await serverTask; } catch (OperationCanceledException) { }
    var samples = echoes.Values.Select(ticks => ticks * 1000d / Stopwatch.Frequency).Order().ToArray();
    await realtime.DisposeAsync();
    await bulk.DisposeAsync();
    return new CaseResult(sampleCount, samples.Length, sampleCount - samples.Length, "n/a (no app queue)",
        bulkSent / 1024d / 1024d / duration.TotalSeconds,
        Percentile(samples, .50), Percentile(samples, .95), Percentile(samples, .99));
}

static async Task RunServerAsync(TcpListener listener, CancellationToken token)
{
    using (listener)
    {
        var tasks = new List<Task>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                var isRealtime = i == 0;
                tasks.Add(ServeAsync(client, isRealtime, token));
            }
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { foreach (var task in tasks) { try { await task; } catch { } } }
    }
}

static async Task ServeAsync(TcpClient client, bool realtime, CancellationToken token)
{
    await using var connection = new BinaryFrameConnection(client.GetStream(), leaveOpen: true);
    try
    {
        while (!token.IsCancellationRequested)
        {
            var message = await connection.ReceiveAsync(token);
            if (message is null) break;
            if (realtime && message.Method == "bench.input")
                await connection.SendAsync(message with { Method = "bench.echo" }, token);
        }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    catch (IOException) { }
    finally { client.Dispose(); }
}

static async Task DelayUntilAsync(long timestamp, CancellationToken token)
{
    while (true)
    {
        var remaining = timestamp - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;
        var ms = remaining * 1000d / Stopwatch.Frequency;
        if (ms > 2) await Task.Delay(TimeSpan.FromMilliseconds(ms - 1), token);
        else Thread.SpinWait(100);
    }
}

static double Percentile(double[] samples, double percentile) => samples.Length == 0 ? double.NaN :
    samples[(int)Math.Ceiling(percentile * samples.Length) - 1];

sealed record CaseResult(int Sent, int Echoed, int Missing, string QueueDrops, double BulkMiBps,
    double P50, double P95, double P99);
