# b-speak

Talk with your coding agents and hear them talk back, so you can keep driving them while your hands are busy. A resident host owns the headset, speech to text and text to speech; agents in any harness with a shell use the `bspeak` CLI to wait for what you say and to speak. Several agents can share the room: they answer when addressed and take turns speaking.

The design is in [docs/architecture.md](docs/architecture.md), and drawn out in [docs/model.html](docs/model.html).

Status: the domain and application core are built and tested. The host, CLI and audio come next.

## Layout

| Project | Role |
|---|---|
| `src/BSpeak.Domain` | Room and Speech aggregates, events, rules (addressing, control phrases, spoken text) |
| `src/BSpeak.Application` | `RoomService`: commands, policies, waits, ports, the in-memory event log |
| `tests/BSpeak.Domain.Tests` | Domain rules, no audio |
| `tests/BSpeak.Application.Tests` | The room end to end with a fake clock and fake playback |

```sh
dotnet test
```

Requires the .NET 10 SDK.
