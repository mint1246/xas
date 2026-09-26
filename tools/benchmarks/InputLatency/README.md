# Input lane latency benchmark

Run from the repository root:

```powershell
$env:APPDATA = (Resolve-Path .nuget-appdata).Path
$env:NUGET_PACKAGES = (Join-Path (Resolve-Path .nuget-user).Path 'NuGet/packages')
dotnet run --project tools/benchmarks/InputLatency/InputLatency.csproj -- [seconds-per-rate] [bulk-MiB-per-second]
```

The harness opens two persistent loopback TCP connections. One carries timestamped, encoded `MoveAbsolute` input frames at 125, 500, and 1000 Hz and receives an immediate echo; the other carries bulk frames concurrently. It reports round-trip p50/p95/p99, missing echoes, and measured bulk throughput. `queueDrops` is reported as unavailable because this harness has no intermediate bounded event queue: each event is sent directly, with backpressure applied by the awaited socket write.

This measures synthetic same-host transport and protocol framing only. It does not include Windows capture, Linux input injection, remote network conditions, or hardware/display latency. Use the real paired daemons on Windows and Linux for end-to-end input latency.
