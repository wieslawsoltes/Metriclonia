# Metriclonia

Metriclonia is a pair of Avalonia desktop applications that demonstrate real-time metric collection and visualization over UDP. The solution contains:

- **Metriclonia.Monitor** – a metrics dashboard that listens for JSON metric samples and renders them on a timeline.
- **Metriclonia.Producer** – a sample application that publishes the built-in Avalonia diagnostics metrics to the monitor.

Both projects target .NET 9.0 (see `global.json`) and share a single solution file `Metriclonia.sln`.

## Prerequisites

- .NET 9.0 SDK (preview builds work as specified in `global.json`).
- A desktop environment supported by Avalonia.

## Building

```bash
dotnet build Metriclonia.sln
```

## Running the monitor

```bash
dotnet run --project Metriclonia.Monitor
```

The monitor binds to UDP port `5005` by default. Override the port by setting `METRICLONIA_METRICS_PORT` before launching:

```bash
export METRICLONIA_METRICS_PORT=6000
```

On start the console prints detailed logging (minimum level `Trace`) including ingest and render diagnostics. The UI exposes the active series in the left pane and a timeline view on the right.

## Running the producer

```bash
dotnet run --project Metriclonia.Producer
```

The producer connects to `127.0.0.1:5005` by default. Override with environment variables:

- `METRICLONIA_METRICS_HOST` – monitor host (default `127.0.0.1`).
- `METRICLONIA_METRICS_PORT` – monitor port (default `5005`).

Once running, the producer streams the Avalonia diagnostics metrics (`MeterListener`) over UDP using JSON by default. Set `METRICLONIA_PAYLOAD_ENCODING` to `binary` to switch to the compact CBOR payload.

## Metric format

Samples follow the schema in `Metriclonia.Contracts/Monitoring/MetricSample.cs` and are serialized with camelCase property names (e.g., `timestamp`, `meterName`, `instrumentName`, `value`). Non-finite values (`NaN`, `Infinity`) are dropped by the monitor to keep the plot stable.

## Transport configuration

Both the monitor and producer honour the optional environment variable `METRICLONIA_PAYLOAD_ENCODING`:

- `json` (default) – UTF-8 JSON envelopes produced via `Metriclonia.Contracts.Serialization.JsonEnvelopeSerializer`.
- `binary`/`cbor` – compact CBOR envelopes produced via `Metriclonia.Contracts.Serialization.BinaryEnvelopeSerializer`.

When configured, the producer sends using the selected encoding and the monitor prefers the same format while still accepting either when probing incoming packets.

## Logging

`Metriclonia.Monitor` uses a custom console logger (`Infrastructure/Log.cs`) configured for `Trace` level output. Key messages to watch while debugging:

- `Flush tick` / `Flush appended …` – the dispatcher timer drained queued samples.
- `Renderer drawing …` / `Renderer skipped …` – the timeline control attempted to render a series.
- `Render pass bounds=…` – the Avalonia control entered its render loop.

You can adjust logging by editing `Infrastructure/Log.cs` or by replacing the logger factory.

## Troubleshooting

- **No series in the UI:** Verify the monitor console shows `Flushed` messages. If not, confirm UDP packets are reaching the selected port.
- **Series listed but plot is blank:** Check for `Renderer skipped` logs; non-finite values or a too-narrow visibility window can be the cause.
- **Producer cannot connect:** Ensure `METRICLONIA_METRICS_HOST` points to the machine running the monitor and that the port is accessible (firewall).
- **Installing side-by-side:** When running both apps from source, start the monitor first so the producer can resolve the destination socket.

## License

Licensed under the GNU Affero General Public License v3.0. See [LICENSE](LICENSE) for full terms.
