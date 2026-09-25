# garge-operator

Bridges MQTT devices and
[garge-api](https://github.com/sondresjolyst/garge-api). Forwards sensor
readings to the API, and publishes switch commands back to the devices when an
automation rule triggers.

## Stack

.NET 10 worker service, MQTTnet, SignalR client, Serilog.

## Quick start

```bash
dotnet restore
dotnet run
```

Needs a reachable MQTT broker and garge-api instance.

## Environment

Production reads these from the cluster secret.

| Variable | Used for |
| --- | --- |
| `Mqtt__Broker`, `Mqtt__Port`, `Mqtt__Username`, `Mqtt__Password` | MQTT broker |
| `Api__BaseUrl`, `Api__Email`, `Api__Password` | garge-api, signed in to obtain a JWT |

Both sections are bound with `ValidateOnStart`, so a missing value fails the
process at startup rather than on first use.

## How it runs

Two hosted services: `Worker` evaluates automation rules against sensor data,
and `OperatorHubClient` holds the SignalR connection to `/hubs/devices`.

`BackgroundServiceExceptionBehavior.StopHost` is set deliberately. An unhandled
exception in either service stops the process so Kubernetes reschedules it,
rather than leaving it running with a dead MQTT connection.

## Health

None. The process listens on no port, so there is nothing to probe and the chart
defines no probes. It fails by exiting, which is what gets it restarted.

## Deployment

Image [`sondresjo/garge-operator`](https://hub.docker.com/r/sondresjo/garge-operator)
on Docker Hub, chart `garge-operator` in
[garge](https://github.com/sondresjolyst/garge), applied by Flux from
[tumo-flux](https://github.com/sondresjolyst/tumo-flux) to `garge-dev` and
`garge-prod`.

The container runs as the non-root `app` user with a read-only root filesystem,
so anything written at runtime needs a volume: `/tmp` and `/home/app`.

A push to `main` builds the `dev` tag. A release-please release builds `vX.Y.Z`,
tags it `latest` and opens a chart bump against
[garge](https://github.com/sondresjolyst/garge). Cluster secrets are created by
[`scripts/garge/bootstrap.sh`](https://github.com/sondresjolyst/tumo-platform/blob/main/scripts/garge/bootstrap.sh)
in [tumo-platform](https://github.com/sondresjolyst/tumo-platform).

## License

Proprietary. Copyright (c) 2026 Sondre Sjølyst.
