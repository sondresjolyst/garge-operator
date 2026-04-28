# garge-operator

.NET 8 Worker Service that bridges MQTT device communication with garge-api. Receives sensor readings from the MQTT broker, forwards them to the API, and publishes switch commands back to physical devices when automation rules trigger.

## How it works

1. Connects to the MQTT broker and subscribes to sensor topics (battery voltage, temperature, humidity).
2. Forwards incoming sensor data to garge-api via HTTP.
3. Exposes a `/webhook` endpoint — garge-api calls this when an automation rule fires.
4. Publishes the resulting switch command (on/off) back to the MQTT broker.

On startup it reconciles device state: any rules that should be active are re-enforced, and expired timers are reset.

## Tech stack

- **.NET 8 Worker Service**
- **MQTTnet v4** — managed MQTT client with auto-reconnection
- **HttpClientFactory** — HTTP calls to garge-api
- **Serilog** — structured logging

## Configuration

Set the following in `appsettings.json` or via environment variables:

| Key | Description |
|-----|-------------|
| `Api:BaseUrl` | URL to garge-api |
| `Api:Username` / `Api:Password` | Credentials for authenticating with the API |
| `Mqtt:Host` | MQTT broker host |
| `Mqtt:Username` / `Mqtt:Password` | MQTT broker credentials |

Use [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) for local development — never commit credentials to `appsettings.json`.

## Running

```bash
dotnet run
```