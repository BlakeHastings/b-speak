# Architecture

Status: v1 is built: domain, application core, host, CLI, local and remote speech engines, and headset audio. Install with `scripts/install.sh`. The visual version of this document is `docs/model.html`.

## What b-speak is

A deterministic voice tool that lets you talk with coding agents and hear them talk back, so you can keep driving them while your hands and eyes are busy. The tool owns everything about audio: the microphone, voice detection, speech to text, text to speech, playback, and turn-taking. Agents only wait for "the user said X" and call "say Y". A short skill teaches agents how to speak like people instead of reading Markdown aloud.

Several agents can share one machine at once. They behave like a room full of people: each hears what you say, answers when it is addressed, and waits its turn to speak.

## Principles

1. **Deterministic first.** Every rule code can check lives in the tool: turn order, addressing, the listening toggle, and the ban on Markdown in speech. The skill covers judgment calls only, and stays short.
2. **Bring your own harness.** Agents use the CLI from any harness with a shell. An MCP server comes later as a second adapter over the same application layer.
3. **Layers are separate.** The domain knows nothing about audio devices, models, the CLI or MCP.
4. **Local by default, remote by choice.** Local models work out of the box with no endpoint to set up. Any OpenAI-compatible endpoint can replace them through config.

## Process model

Audio is real-time: the mic must stay open, voice detection must run continuously, and models must stay loaded. A CLI command that exits cannot do that, so b-speak runs a **resident host** per machine.

```
   Adapters         bspeak CLI (agents, scripts)        MCP server (later)
                              \                          /
                               v   local HTTP (/api)    v
   Host             bspeak serve: one process per machine
                    Application: command handlers, policies, queries
                    Domain: Room, Speech
                    Infrastructure: audio I/O, VAD, STT, TTS, model store
```

- `bspeak serve` starts the host. Any other command starts it automatically if it is not running, so agents never manage it.
- The host listens on localhost and writes its port to the profile folder. The CLI is a thin client over the host's HTTP API, the same split b-learn uses between its CLI and web server.
- The future MCP server is another client of the same API (or an in-process adapter). Nothing in the domain or application layer changes to add it.

## Layers and projects

| Project | Contents | Depends on |
|---|---|---|
| `BSpeak.Domain` | Aggregates, value objects, domain events, invariants | nothing |
| `BSpeak.Application` | `RoomService` (commands, policies, queries, waits), ports (playback, clock, event log), the in-memory event log | Domain |
| `BSpeak.Infrastructure.Audio` | PortAudio capture and playback, Silero VAD, resampling | Application |
| `BSpeak.Infrastructure.Speech` | Whisper and Kokoro (local), OpenAI-compatible STT and TTS (remote), model download | Application |
| `BSpeak.Host` | HTTP API and host lifetime | Application (+ Infrastructure for wiring) |
| `BSpeak.Cli` | The `bspeak` command, which also runs the host | Host |
| `skills/b-speak` | The agent skill (prose) | n/a |
| `tests/` | Domain tests (no audio) and integration tests (recorded WAV fixtures, fake devices) | |

Target framework: .NET 10.

## The audio pipeline is not the domain

Capture, voice detection and transcription form a streaming pipeline. Forcing it into commands and aggregates would be over-modeling. It sits behind ports in the application layer and emits two things into the domain: "the user started speaking" and "the user finished an utterance, here is the text". The domain models what is actually about decisions: who is in the room, who holds the floor, what is said, and what gets heard.

## Domain

### Aggregates

| Aggregate | Domain operations | Invariants it enforces |
|---|---|---|
| **Room** (one per host) | `Join`, `Leave`, `Touch`, `ExpireIdle`, `PauseListening`, `ResumeListening`, `SetMode`, `BeginUserTurn`, `RecordUtterance`, `RequestFloor`, `RaisesHand`, `CallOn`, `SetTurnMode`, `TryGrantFloor`, `ReleaseFloor`, `ReturnFloor` | Names are speakable, unique, not confusable with each other by ear, and use no reserved or control-phrase word; one floor holder at a time; the floor is never granted while you speak or inside the gap after; nothing but the resume phrase gets through while listening is paused |
| **Speech** (one per thing an agent says) | `Queue`, `Start`, `SentencePlayed`, `Finish`, `Interrupt`, `Withdraw`, `Revise` | Plain spoken text only: no code fences, inline code, bullets, headings, tables, bold, links or raw URLs; at most a configured length (600 characters by default); state moves Queued to Playing to Finished or Interrupted, or Queued to Withdrawn, and never back; only its own participant may withdraw or revise it, and only while it waits; a `--reply-to` speech is rejected if the conversation has moved on (see below) |

`Speech` asks the room what it needs through a narrow `IRoomView` (is this participant here, how many utterances exist, which is the newest one meant for this participant), so the two aggregates stay separate.

**Host-only commands.** `BeginUserTurn`, `RecordUtterance`, `TryGrantFloor`, `Start`, `SentencePlayed`, `Finish` and `Interrupt` are issued by the host itself: by the audio pipeline, by playback callbacks, or by policies. They live in the domain so their rules are tested, but no CLI or HTTP resource exposes them.

### Participants and voices

An agent joins the room with a name (`bspeak participant post --name Atlas`). The host assigns each participant a distinct TTS voice from a pool so you can tell them apart by ear. A name that sounds too close to one already in the room (one edit apart, or two for names of six letters or more) is rejected with `name-similar`, because speech to text would confuse them.

A participant that has not called the host within the idle timeout (30 minutes by default) is removed and its queued speech withdrawn. An open `utterance list --wait` counts as present, so an agent that is simply listening never times out.

Commands from an agent carry its identity with `--as <name>` or the `BSPEAK_PARTICIPANT` environment variable.

### Addressing

Every utterance goes to every participant, like sound in a room. The host annotates each one deterministically so agents do not have to guess:

- **`addressedTo`**: set when the utterance starts with a participant's name ("Atlas, run the tests") or with `everyone`. Matching tolerates small transcription errors against the participant list.
- **`focus`**: the participant the conversation is with when nothing is addressed. It is the last participant you addressed or the last one that spoke to you.

Getting the floor also moves focus to that participant: whoever spoke to you last is who an unaddressed reply goes to.

The room numbers utterances 1, 2, 3 and so on. That number is the only one agents use: `utterance list --after <id>` and `speech post --reply-to <id>`. Each utterance an agent receives carries `forYou`: addressed to it, to everyone, or unaddressed while it held focus.

The skill tells agents: answer when addressed; when nothing is addressed, the focus participant answers and the others stay quiet unless what you said clearly concerns them.

### The floor

Only one voice plays at a time.

- `speech post` queues a Speech and requests the floor. Requests are granted in order.
- The floor is never granted while you are speaking, or until a short gap (700 ms by default) after you stop or after the previous speech ends, so you always get the chance to answer first.
- When you start speaking during playback, playback stops at once (barge-in). With a headset the mic does not hear the speaker, so barge-in works in v1 without echo cancellation. The interrupted Speech records the last sentence you fully heard, so the agent knows what got through. A speech that had the floor but had not started playing is withdrawn, and once you finish a sentence everything still queued is withdrawn too: nothing resumes on its own, so each agent decides whether what it was going to say still fits.
- **Hands up.** The room has a turn mode: `open` (above) or `hands-up` (said as "hands up" and "open floor", or `turns put`). In hands up, a new speech raises a hand instead of joining the line, unless it is a direct reply (its `--reply-to` is the newest utterance meant for that participant), which skips the line. Hands are never granted by themselves: the user calls on one ("Atlas, go ahead", or "next" for the first one up; `turns call` from the CLI or the room view), which moves it to the front of the line. One hand per participant (`hand-already-up`); `speech patch` revises a waiting speech and keeps its place, so an agent keeps working and keeps its hand current instead of stacking stale messages. Hands survive new utterances; going back to an open floor lowers them into the line in order. "Who has their hand up?" is answered aloud by the room in a voice no agent uses.
- `--reply-to <utterance>` makes the speech conditional: if a newer utterance meant for this participant (addressed to it, to everyone, or unaddressed while it holds focus) arrived after it, the post is rejected with the rule `conversation-moved-on`, so the agent reads the new utterance before answering a stale one.

### Listening modes and the toggle

| Mode | Behavior |
|---|---|
| `always` (v1 default) | Every utterance is transcribed and delivered. |
| `wake-word` | Only utterances that start with the wake word are delivered, with the wake word removed. Built as a transcript match; a dedicated keyword model is not built yet. |

Saying a control phrase toggles listening: "stop listening" (or "pause listening") pauses, "start listening" (or "resume listening") resumes. Only a whole utterance that is just the phrase counts, give or take "okay" or "please", so "tell the server to stop listening on 8080" is delivered normally. Control phrases are matched by the host, never delivered to agents, and never stored. Agents and scripts can toggle too: `bspeak listening put off|on`.

While paused the host still detects speech, so it can catch the resume phrase and so barge-in still stops an agent that starts talking over a phone call. `UserStartedSpeaking` is still recorded (it carries no content), and every transcript except the resume phrase becomes `UtteranceDiscarded` with no text.

The host plays a short earcon (a distinct tone) on pause and resume so you know the state without looking.

### Events

| Event | Fields |
|---|---|
| `ParticipantJoined` | name, voice |
| `ParticipantLeft` | name, reason (left, timed-out) |
| `ListeningPaused`, `ListeningResumed` | by (voice, cli, or cli:name) |
| `ListeningModeChanged` | mode, wakeWord |
| `UserStartedSpeaking` | none |
| `UserSpoke` | utteranceId, text, addressedTo, focus |
| `UtteranceDiscarded` | reason (empty, paused, no-wake-word, control); never the text |
| `FloorGranted` | speechId, participant |
| `TurnModeChanged` | mode (open, hands-up), by |
| `HandRaised` | speechId, participant |
| `HandCalledOn` | speechId, participant, by |
| `HandsAsked` | none |
| `SpeechRevised` | speechId, participant, text |
| `SpeechQueued` | speechId, participant, text, replyTo |
| `PlaybackStarted` | speechId, participant, voice |
| `PlaybackFinished` | speechId, participant |
| `SpeechInterrupted` | speechId, participant, heardThrough, heardText |
| `SpeechWithdrawn` | speechId, participant, reason (withdrawn, participant-left) |

### Rule codes

| Code | Checked on | Meaning |
|---|---|---|
| `name-format` | join | Not a speakable name (1 to 24 letters) |
| `name-reserved` | join | Uses `everyone`, `all`, `hey` or a control-phrase word |
| `name-duplicate` | join | Already in the room |
| `name-similar` | join | Sounds too close to someone in the room |
| `participant-unknown` | every agent command | Not in the room; rejoin |
| `mode-invalid`, `wake-word-required` | listening patch | Bad mode, or wake-word mode without a wake word |
| `speech-empty`, `speech-markdown`, `speech-too-long` | speech post | Nothing to say, Markdown found (the message names it), or over the limit |
| `reply-to-unknown` | speech post | `--reply-to` names an utterance that does not exist |
| `conversation-moved-on` | speech post | A newer utterance meant for you arrived; read it first |
| `speech-not-yours` | speech delete | Only the speaker may withdraw a speech |
| `state-transition` | speech delete, playback, handoff patch | The speech or handoff is not in a state that allows this |
| `handoff-empty`, `handoff-result-required` | handoff post, handoff patch | No task text; closing a handoff without a spoken result |

`speech-unknown` is a not-found (exit 4), not a rule.

### Read models

| Read model | Built from | Query |
|---|---|---|
| Utterance feed | `UserSpoke` | `utterance list --after <id> [--for-you] [--wait]` |
| Speech status | the Speech aggregate | `speech get <id>`, `speech list`, `speech post --wait` |
| Room status | the Room aggregate | `host get`, `brief get` |

### Policies

| Whenever | Then |
|---|---|
| `UserStartedSpeaking` while a speech holds the floor (**Barge-in**) | Interrupt it if playing; withdraw it (reason `interrupted`) if it had not started; stop playback and drop audio still in flight |
| `UserSpoke` (**Drop stale speech**) | Withdraw every speech still waiting except raised hands (reason `user-spoke`): it was written before what the user just said. Agents see each in their inbox (`speech-withdrawn` with its text) and decide whether to say any of it again. Noise and coughs produce no `UserSpoke`, so they leave the queue alone |
| `FloorGranted` (**Play on grant**) | Ask the render side to synthesize and play it |
| `ListeningPaused`, `ListeningResumed` (**Earcon**) | Play the matching tone |
| `HandRaised` (**Chime**) | Play a soft single tone |
| `HandsAsked`, `TurnModeChanged` (**Room speaks**) | Say who has a hand up, or which mode is on, in a voice no agent uses; talking over it stops it |
| `ParticipantLeft` (**Withdraw on leave**) | Withdraw everything it had queued |
| `SpeechQueued`, `PlaybackFinished`, `SpeechInterrupted`, `SpeechWithdrawn`, `HandCalledOn`, `TurnModeChanged` (**Next speaker**) | Try to grant the floor to the oldest queued speech |
| Clock tick | Expire idle participants; try to grant the floor (picks up speeches waiting out the gap) |

Control phrases and addressing are not policies: they are part of how `RecordUtterance` decides what an utterance is. All policies run inside `RoomService`, in the same locked step as the command that triggered them. Effects on the devices (play, stop, earcon) run after the lock is released.

## Messaging

The host keeps an **event log** with increasing sequence numbers, in memory for v1. Each reader keeps its own position, so every participant sees everything. Agents read the utterance feed built from it with `utterance list --after <utterance id> --wait`; `event list --after <seq>` reads the raw log. `room tui` is a read model over the same log: it replays it from the start, follows it with long polls, and reconciles against `GET /api/host` every second for presence and who has a wait open. It never starts a host, and it starts over when the host's pid changes. A host restart clears the log and the room; agents rejoin. Persisting transcripts (to SQLite in the profile folder) is a later option, not a v1 requirement.

## Providers

Each capability is a port with swappable implementations, chosen in config.

| Port | Local (default) | Remote |
|---|---|---|
| Speech to text | Whisper (whisper.cpp via Whisper.net) | OpenAI-compatible `/v1/audio/transcriptions` |
| Text to speech | An ordered list, stepping down automatically: Phonon 31M (Gradium, Apache 2.0) through Gradium's streaming `ptts-openai-server` in Docker (b-speak runs the container), then Gradium's `ptts` CLI, then Kokoro (KokoroSharp, in-process, runs anywhere .NET does) | Any OpenAI-compatible `/v1/audio/speech` (for example a LiteLLM gateway), streaming if it supports raw PCM; can sit anywhere in the list |
| Voice activity detection | Silero VAD (ONNX) | n/a |
| Audio I/O | PortAudio (WASAPI on Windows) | n/a |

- **Seamless local models.** `bspeak model pull` downloads the default local models into the profile folder, and the host pulls them on first use if missing. No server or endpoint to run.
- **Latency.** TTS synthesizes per sentence and starts playing the first sentence while the rest is generated. A long first sentence is split at its first natural pause, so a short opening clause plays sooner.
- **Step-down.** `tts.providers` is tried in order. A provider that cannot run on this machine (no prebuilt binary for the platform, a CPU without AVX2, a failed download or warm-up) is skipped with the reason shown in `bspeak host get`; if the active one fails mid-conversation the next takes over for good. Room voices come from the first provider and map to the others by position. `phonon-server` reuses the `bspeak-phonon` container if running, starts it if stopped (about 5 s, the model is cached in a volume), or creates it (about 45 s the first time), and streams: first audio in about 170 ms. Without Docker it steps down to Gradium's CLI (about 1.1 s, one process per sentence), then Kokoro. Streaming engines hand over audio as it is generated and playback starts on the first chunk. Whisper steps down on its own across CUDA, Vulkan and CPU.
- **GPU.** Whisper runs on Vulkan (or CUDA when the toolkit is installed), falling back to the CPU. Kokoro v1.0 cannot run on DirectML (a ConvTranspose op fails), so it stays on the CPU; the engine catches that and falls back rather than crashing.
- **Config** lives in the profile folder (`~/.bspeak/config.json`): provider per port, endpoint and model names, voices, devices, control phrases, mode. `bspeak provider put` and `bspeak device put` edit it.

## CLI and HTTP API

Commands are `bspeak <resource> <verb>` with the HTTP verbs `get`, `list`, `post`, `put`, `patch` and `delete`, mirroring `/api` on the host. All commands print JSON. Exit codes: 0 ok, 1 rejected by a rule (the output names the rule), 2 usage, 3 wait timed out, 4 not found, 5 host unavailable. Brief prints Markdown text instead of JSON. Waits are long polls: the HTTP API holds a request open for up to 50 seconds (`?wait=<seconds>`), and the CLI repeats it until its own `--timeout`.

| Resource | CLI verbs | Notes |
|---|---|---|
| brief | `get` | The voice rules, room etiquette, the current participants and listening state. The agent reads this first |
| participant | `post [--name]`, `list`, `delete` | Join (without a name the host picks a distinct one), see who is in the room, leave |
| utterance | `list [--after <id>] [--for-you] [--wait] [--timeout]` | What the user said, with `addressedTo`, `focus` and `forYou`. `--for-you` skips utterances meant for others |
| speech | `post <text> [--reply-to] [--wait]`, `get`, `list`, `delete` | Say something; `--wait` returns when it finished or was interrupted; `delete` withdraws a queued speech |
| listening | `get`, `put on\|off`, `patch --mode` | The toggle and the mode |
| event | `list [--after] [--wait]` | The raw event log |
| model | `list`, `pull` | Local model files. Runs in the CLI itself, so you see download progress |
| device, config | `device list`, `config get` | Devices and settings. Edit `~/.bspeak/config.json` to change providers, models, voices and devices, then restart the host |
| host | `get` (status), `delete` (stop) | `bspeak serve [--no-audio]` runs it in the foreground |
| utterance | `post "<text>"` | Type as if you had said it: for testing, or without a mic |
| selftest | `[--play]` | Speaks a sentence and transcribes it back through the real engines, no mic needed |

| inbox | `list --as <name> [--after <seq>] [--wait]` | The voice agent's wait: utterances, updates on its handoffs, its own interruptions and withdrawn speech. Returns `next` to pass back, and a `context` block (who was heard last and how long ago, when you last spoke, when the user last spoke to you) for judging whether to open with a micro introduction |
| handoff | `post "<task>" --as <name> [--utterance <id>]`, `list [--status] [--wait]`, `get`, `patch --status --note [--by]` | Work passed from the voice agent to the orchestrator; `list --wait` blocks until one is open |

## A fast voice and a deeper worker

In a cycle (you stop talking, the agent thinks, the agent speaks), b-speak's part is small: end of turn 750 ms, speech to text about 200 ms on the GPU, first audio about half a second for a short clause. The model's turn dominates, especially when it runs tools. So the usual setup pairs a quick conversational agent (Sonnet or Haiku, often a background subagent) with a deeper agent (for example Opus) doing the heavy work and orchestrating workers. They keep each other informed however suits the harness: subagent messages, or b-speak's handoffs as a shared queue. The skill and the brief describe this lightly and leave the details to the agents' judgment.

### The Handoff aggregate

An optional queue for passing work between agents. `Post` needs a participant in the room and some text, and may reference an utterance; `Update` moves it Open to Working to Done or Failed, never back, and closing it needs a result note (`handoff-result-required`). Events: `HandoffPosted` and `HandoffUpdated`. The inbox read model is built from `UserSpoke`, updates on the participant's own handoffs, and its own interruptions and failed playback; its cursor is the event sequence. `handoff-unknown` is a not-found (exit 4).

## The skill (prose)

`skills/b-speak/SKILL.md` covers joining, listening, speaking, the usual two-agent setup, and recovering when the room pushes back. The harness-neutral version lives in `bspeak brief get`.

## Volume and voices

- **Volume** is b-speak's own playback level (not the system volume), 0 to 100, applied in the render device to speech and tones. `bspeak volume put <n|up|down>` changes it (steps of 15) and it is saved in `~/.bspeak/preferences.json`.
- **Voices**: `Room.ChangeVoice` gives a participant a different voice, raising `VoiceChanged`. It must be one the speaking engine has (`voice-unknown`; each engine reports its catalog, Phonon's from Gradium's list, Kokoro's English voices) and nobody else may be using it (`voice-taken`). `bspeak participant patch <name> --voice <voice>` exposes it; the choice is remembered per name and offered again on the next join.
- **Keep-alive**: between speeches the render device plays a faint noise floor (`audio.keepAliveDb`, default -80 dBFS) instead of digital silence, because many wireless headsets power down on silence and swallow the start of the next sentence.

## Releases and updates

- **Versions** come from git tags (MinVer). Pushing a tag like `v0.2.0` runs the release workflow: self-contained builds for win-x64, linux-x64 and osx-arm64, each with the skill and a `bspeak.release` marker, plus `SHA256SUMS`.
- **Installing**: `install.ps1` and `install.sh` download the latest release, verify its checksum, and put `bspeak` on the PATH. `INSTALL.md` walks any agent through install, `bspeak setup` and `bspeak skill install`, so a user only pastes one line.
- **Checking**: the host asks GitHub for the latest release at most once a day, in the background. When a newer one exists, `bspeak brief get` opens with a notice telling the agent to ask the user.
- **Updating**: `bspeak update` downloads the build for this platform, checks it against `SHA256SUMS`, stops the host, and swaps the files in place. Files that are in use (the CLI itself, an agent's open wait) are renamed aside instead of overwritten, which works on Windows too; leftovers are deleted on a later run. The skill is refreshed wherever `bspeak skill install` put it. Agents then rejoin the room. Source builds (no marker) are told to update with git instead.

## Not built yet

- `provider put` and `device put`. Edit config.json for now.
- A dedicated wake-word model (openWakeWord-style) to save running speech to text on everything. The transcript-match version of `wake-word` mode is built.
- Acoustic echo cancellation for room speakers (v1 assumes a headset). See the WebRTC APM notes in the `dotnet-local-voice` skill.
- MCP server adapter.
- Persisted transcripts.
- A small status page on the host (who is in the room, who holds the floor, listening state).
