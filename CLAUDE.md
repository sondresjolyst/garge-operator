# garge-operator

.NET 10 Worker Service that bridges MQTT device communication with garge-api. Polls automation rules, manages MQTT device connections, and forwards sensor data to the API.

## Domain Context

Garge is a smart garage system for vehicles (motorcycles etc.). Two sensor types exist today:
- **Battery sensor** — measures vehicle battery voltage (e.g., trigger charger socket when below 12.5V)
- **Temp/humidity sensor** — measures garage environment (e.g., trigger dehumidifier socket when humidity exceeds 50%)

Switches are controllable power sockets attached to devices like battery chargers or dehumidifiers. The operator receives raw sensor readings via MQTT and forwards them to garge-api, which evaluates automation rules and sends switch commands back through the operator to the MQTT broker.

## Tech Stack

| Concern | Library |
|---|---|
| Framework | .NET 10 Worker Service (IHostedService) |
| Language | C# (nullable reference types) |
| MQTT | MQTTnet v4 (managed client) |
| HTTP | HttpClientFactory → garge-api |
| Logging | Serilog |
| Config | appsettings.json + User Secrets |

## Architecture

Two primary classes drive all behavior:

- **`Worker.cs`** — The orchestrator. Runs the main background loop: authenticates with garge-api, polls automation rules, and reconciles device state on startup. Keep it thin — it coordinates, it does not implement.
- **`Services/MqttService.cs`** — Handles everything MQTT: broker connection, topic subscriptions, device discovery, sensor data collection, and switch command publishing.

Supporting pieces:
```
Models/    # Domain models: Sensor, Switch, SensorData, payload types
Dtos/      # Types for API request/response shapes
Services/  # Additional services registered in Program.cs
```

## Architecture Rules

### Separation of Concerns
- `Worker.cs` calls into `MqttService` — it does not contain MQTT logic itself.
- All MQTT operations (connect, subscribe, publish, device state tracking) belong in `MqttService`.
- Additional services go in `Services/` and are registered in `Program.cs` via DI.

### API Communication
- Always use `HttpClientFactory` for HTTP calls to garge-api. Never instantiate `HttpClient` directly.
- API base URL and credentials come from configuration — not hardcoded.
- Authenticate once at startup and refresh tokens when they expire. Do not re-authenticate on every request.

### State Management
- Device state and caches (last published actions, electricity price cache) live in in-memory dictionaries.
- There is no local persistent state — garge-api and the MQTT broker are the sources of truth.
- Keep shared state minimal and clearly owned by one class.

### MQTT Patterns
- Use the MQTTnet managed client for automatic reconnection handling.
- Subscribe to device topics on initial connection and resubscribe after reconnect.
- **Sensor data flow**: MQTT message → `MqttService` handler → HTTP POST to garge-api.
- **Switch command flow**: automation rule / webhook trigger → `Worker` → `MqttService.PublishAsync` → MQTT broker → device.

### Logging
- Serilog only. Inject `ILogger<T>` via DI — never use `Console.WriteLine`.
- Use structured logging with named placeholders: `_logger.LogError(ex, "Failed to publish to {Topic}", topic)`.
- Log MQTT connection events, device discoveries, and automation reconciliation at Information level.

## Naming Conventions

| Thing | Convention | Example |
|---|---|---|
| Models | Singular PascalCase | `Sensor.cs`, `SwitchConfig.cs` |
| Payload types | `<Domain>Payload.cs` | `WebhookPayload.cs`, `BatteryHealthPayload.cs` |
| DTOs | `<Action><Domain>Dto.cs` | `CreateSensorDataDto.cs` |

## What to Avoid
- Do not access the database directly — all data goes through garge-api HTTP calls.
- Do not put MQTT logic in `Worker.cs` — it belongs in `MqttService`.
- Do not use `new HttpClient()` — always use `HttpClientFactory`.
- Do not use `Console.WriteLine` — use Serilog.
- Do not commit secrets to `appsettings.json` — use User Secrets for development and environment variables in production.
- Do not ignore nullable reference warnings — handle nulls explicitly.
