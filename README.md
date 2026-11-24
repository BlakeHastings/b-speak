# b-speak

Talk with your coding agents and hear them talk back, so you can keep driving them while your hands are busy. A resident host owns the headset, speech to text and text to speech. Agents in any harness with a shell use the `bspeak` CLI to wait for what you say and to speak. Several agents can share the room: they answer when addressed and take turns speaking.

The design is in [docs/architecture.md](docs/architecture.md), and drawn out in [docs/model.html](docs/model.html).

## Install

Requires the .NET 10 SDK and a headset (the mic must not hear the agent's voice; see `audio.muteMicWhilePlaying` for speakers).

```sh
scripts/install.sh      # publishes to ~/.bspeak/bin, adds ~/bin/bspeak, installs the Claude Code skill
bspeak model pull       # downloads Silero VAD, Whisper small.en, Phonon (Gradium's ptts runner) and Kokoro (about 900 MB, once)
bspeak selftest --play  # speaks a sentence, plays it, and transcribes it back
```

## Use it

In a Claude Code session, say something like "let's talk using b-speak". The `b-speak` skill has the agent join the room, say hello, and keep a background listener running. Then just talk. Start a sentence with an agent's name ("Atlas, run the tests") when more than one is in the room. Say "stop listening" to pause and "start listening" to resume; you hear a tone each time.

Other harnesses: have the agent run `bspeak brief get` and follow it.

Without a mic, or to test: `bspeak utterance post "Atlas, what's the status?"` types as if you had said it, and `bspeak serve --no-audio` prints speech instead of playing it.

## Configure

`~/.bspeak/config.json` is written on first run. Edit it and restart the host (`bspeak host delete`; the next command starts it again).

| Setting | Default | |
|---|---|---|
| `stt.provider`, `stt.model` | `whisper`, `small.en` | Or `large-v3-turbo` for accuracy; `openai` with `stt.endpoint` for a remote `/v1/audio/transcriptions` |
| `tts.providers` | `["phonon", "kokoro"]` | Tried in order: the first that runs on this machine speaks, and the next takes over if it fails. `openai` with `tts.openAi.endpoint` adds any `/v1/audio/speech` server (Gradium's streaming `ptts-openai-server`, a LiteLLM gateway) |
| `tts.phonon.voices`, `tts.kokoro.voices` | six built-in voices each | Phonon names come from Gradium's catalog; voices map across providers by position |
| `audio.inputDevice`, `audio.outputDevice` | `default` | A name fragment or index from `bspeak device list` |
| `audio.endOfTurnSilenceMs` | `750` | How long a pause ends your turn |
| `audio.enabled` | `true` | `false` runs without devices |

The host log is `~/.bspeak/host.log`; `bspeak host get` shows audio state, devices and who is in the room.

## Layout

| Project | Role |
|---|---|
| `src/BSpeak.Domain` | Room and Speech aggregates, events, rules (addressing, control phrases, spoken text) |
| `src/BSpeak.Application` | `RoomService`: commands, policies, waits, ports, the agent brief |
| `src/BSpeak.Infrastructure.Speech` | Silero VAD, Whisper and remote STT, Kokoro and remote TTS, model downloads |
| `src/BSpeak.Infrastructure.Audio` | PortAudio capture and render, endpointer, resampling, the capture and playback pipelines |
| `src/BSpeak.Host` | The resident host: config, HTTP API, audio runtime |
| `src/BSpeak.Cli` | The `bspeak` command |
| `skills/b-speak` | The Claude Code skill |

```sh
dotnet test          # domain, application and infrastructure tests
scripts/smoke.sh     # host and CLI end to end, audio off
```
