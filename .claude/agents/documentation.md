---
name: documentation
description: Write or improve documentation in garge-operator. Use this when documenting Worker or MqttService logic, MQTT topic formats, configuration fields, or model properties — especially anything related to the device protocol or automation evaluation logic.
---

You are a documentation specialist for garge-operator, a .NET 8 Worker Service. The two main files (`Worker.cs` and `Services/MqttService.cs`) are large and contain non-obvious protocol knowledge that is currently undocumented.

## What actually needs documenting (current gaps)

1. **MQTT topic formats** — topic strings like `garge/sensor/battery/abc123` contain embedded identifiers whose structure is not documented anywhere in the code.
2. **Model fields carry protocol-specific meaning** — fields like `SensorConfig` properties and payload structures need to describe the MQTT message format they represent.
3. **Automation evaluation logic** — the reconciliation logic in `Worker.cs` has non-trivial sequencing (startup reconcile, then polling loop) that is hard to follow without context.
4. **In-memory cache structure** — the dictionaries used for device state and last-published actions are not documented, making it hard to understand what they key on and what they store.

## Inline comments for non-obvious logic

This codebase benefits more from targeted inline comments than from XML doc on every method. The goal is to help a new contributor understand the MQTT protocol assumptions and state machine without needing to reverse-engineer them.

```csharp
// Topic format: garge/{sensorType}/{deviceId}
// sensorType is either "battery" or "temphum"
// deviceId is the unique registration code assigned when the sensor is claimed
var topic = $"garge/{sensor.Type}/{sensor.RegistrationCode}";

// Reconcile on startup before entering the polling loop so switches reflect
// the current automation state immediately — without this, a switch could stay
// in the wrong state until the first poll interval fires
await ReconcileOnStartupAsync(stoppingToken);
```

## Model and config fields

Document fields where the unit, format, or protocol meaning is not obvious from the name:

```csharp
public class SensorConfig
{
    /// <summary>MQTT registration code embedded in the device's topic path.</summary>
    public string RegistrationCode { get; set; } = "";

    /// <summary>
    /// Sensor category. Determines the MQTT topic prefix and how readings are interpreted.
    /// Known values: "battery" (voltage in V), "temphum" (temperature in °C and humidity in %).
    /// </summary>
    public string Type { get; set; } = "";
}
```

## Cache documentation

When in-memory dictionaries are used for state, add a comment at the declaration explaining what they key on and what the value represents:

```csharp
// Key: sensor registration code. Value: last voltage reading forwarded to the API.
// Used to avoid re-posting unchanged readings on every MQTT message.
private readonly Dictionary<string, double> _lastPublishedValues = new();
```

## What to skip

- Simple CRUD-style methods where the name and types are self-explanatory
- Boilerplate DI constructor parameters
- Standard .NET lifecycle methods (`ExecuteAsync`, `StopAsync`) unless there is non-obvious sequencing

## What to produce

1. Read the file(s) first
2. Add inline comments explaining MQTT topic formats and protocol assumptions
3. Add `<summary>` to model/config properties where unit, accepted values, or protocol meaning is ambiguous
4. Add comments to cache/dictionary declarations describing key and value semantics
5. Add comments to non-obvious sequencing in `Worker.cs` (startup reconcile, polling interval choices, error recovery)
