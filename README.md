# b-speak

Talk with your coding agents and hear them talk back, so you can keep driving them while your hands are busy: doing the dishes, walking around, away from the keyboard. A small local host owns your headset, speech to text and text to speech. Agents in any harness with a shell (Claude Code, Codex, OpenCode, Cursor, Gemini CLI, your own scripts) use the `bspeak` command to hear what you say and to speak. Several agents can share the room: they answer when addressed and take turns.

## Get started

Paste this to your coding agent:

```
Install b-speak for me by following https://raw.githubusercontent.com/BlakeHastings/b-speak/main/INSTALL.md
```

It installs the `bspeak` command, downloads the voice models, checks they work, and gives itself the b-speak skill if your harness supports skills. Then start a new session and say something like "let's talk using b-speak".

You need a **headset**, so the microphone does not hear the agent's voice. Windows, macOS (Apple silicon) and Linux (x64) are supported; Windows is the most tested. Docker is optional and gives the fastest voice.

### Install it yourself

- Windows (PowerShell): `irm https://raw.githubusercontent.com/BlakeHastings/b-speak/main/install.ps1 | iex`
- macOS or Linux: `curl -fsSL https://raw.githubusercontent.com/BlakeHastings/b-speak/main/install.sh | sh`

Then `bspeak setup` to download the models (about 900 MB, once) and `bspeak selftest --play` to hear it. Claude Code users: `bspeak skill install --claude`.

### Updates

The host checks for a new release once a day. When there is one, the agent's first command in a voice session (`bspeak brief get`) tells it to ask you; say yes and it runs `bspeak update`, which installs the new version and refreshes the skill, then carries on. You can also run `bspeak update` yourself, or rerun the install line.

## Use it

Start a sentence with an agent's name ("Atlas, run the tests") when more than one is in the room. Talk over an agent to interrupt it. Say "stop listening" to pause the microphone and "start listening" to resume; you hear a tone each time. Ask an agent to turn the volume down or up, or to use a different voice; it changes them for you, and both are remembered.

To see the room, run `bspeak room tui` in a spare terminal. It shows who is in the room, who is talking, who is waiting to speak and what they want to say, and a running log of what was said. Space pauses or resumes listening, `+` and `-` change the volume, and `q` quits.

The usual setup pairs a quick conversational agent with a deeper one working in the background, so the conversation never waits on a long tool call. The skill and `bspeak brief get` describe it.

Without a mic, or to test: `bspeak utterance post "Atlas, what's the status?"` types as if you had said it, and `bspeak serve --no-audio` prints speech instead of playing it.

## Configure

`~/.bspeak/config.json` is written on first run. Edit it and restart the host (`bspeak host delete`; the next command starts it again).

| Setting | Default | |
|---|---|---|
| `stt.provider`, `stt.model` | `whisper`, `small.en` | Or `large-v3-turbo` for accuracy; `openai` with `stt.endpoint` for a remote `/v1/audio/transcriptions` |
| `tts.providers` | `["phonon-server", "phonon", "kokoro"]` | Tried in order: the first that runs on this machine speaks, and the next takes over if it fails. `phonon-server` runs Gradium's streaming server in Docker for you (container `bspeak-phonon`, left running between hosts; `docker rm -f bspeak-phonon` removes it); `phonon` is their CLI; Kokoro runs anywhere. `openai` with `tts.openAi.endpoint` adds any `/v1/audio/speech` server (Gradium's streaming `ptts-openai-server`, a LiteLLM gateway) |
| `tts.phonon.voices`, `tts.kokoro.voices` | six built-in voices each | Phonon names come from Gradium's catalog; voices map across providers by position |
| `audio.inputDevice`, `audio.outputDevice` | `default` | A name fragment or index from `bspeak device list` |
| `audio.endOfTurnSilenceMs` | `750` | How long a pause ends your turn |
| `audio.keepAliveDb` | `-80` | A faint noise floor between speeches so wireless headsets do not power down and clip the next sentence; `null` turns it off |
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

The design is in [docs/architecture.md](docs/architecture.md). To build and install from a clone (needs the .NET 10 SDK):

```sh
scripts/install-from-source.sh   # publishes to ~/.bspeak/bin and installs the Claude Code skill
dotnet test                      # domain, application and infrastructure tests
scripts/smoke.sh                 # host and CLI end to end, audio off
```

Releases: push a tag like `v0.2.0`; the release workflow builds every platform and publishes it, and installed copies offer the update.

## License

b-speak is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. See [LICENSE](LICENSE).

Copyright (C) 2026 Blake Hastings.

### Third-party components

b-speak downloads models and tools at runtime rather than shipping them. Each keeps its own license:

| Component | Used for | License |
|---|---|---|
| Phonon 31M English, Gradium `ptts` and `ptts-openai-server` | Text to speech | Apache-2.0 (model); MIT or Apache-2.0 (runtime) |
| Kokoro-82M and KokoroSharp | Text to speech fallback | Apache-2.0 (model); MIT (engine) |
| espeak-ng (bundled by KokoroSharp in the publish output) | Phonemes for Kokoro | GPL-3.0 |
| Whisper and Whisper.net / whisper.cpp | Speech to text | MIT |
| Silero VAD | Voice activity detection | MIT |
| PortAudio and PortAudioSharp2 | Audio devices | MIT-style / Apache-2.0 |
| NAudio.Core | Resampling | MIT |
| ONNX Runtime | Model inference | MIT |
