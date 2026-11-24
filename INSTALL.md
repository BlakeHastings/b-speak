# Installing b-speak (instructions for an AI agent)

The user wants b-speak: a local voice room that lets them talk with you, and hear you, through a headset. Follow these steps in order. Tell the user briefly what you are doing at each step, and stop to ask them if something fails.

## 1. Install the `bspeak` command

Pick the line for the user's system and run it. It downloads the latest release from GitHub, checks its checksum, and puts `bspeak` on the PATH. Running it again later updates b-speak.

- **Windows** (PowerShell):
  ```
  irm https://raw.githubusercontent.com/BlakeHastings/b-speak/main/install.ps1 | iex
  ```
- **macOS or Linux**:
  ```
  curl -fsSL https://raw.githubusercontent.com/BlakeHastings/b-speak/main/install.sh | sh
  ```

If `bspeak` is not found afterwards, the current shell has the old PATH: use the full path the installer printed (`~/.bspeak/bin/bspeak`, or `bspeak.exe` on Windows) for the rest of these steps.

## 2. Download the voice models and check them

```
bspeak setup
```

This downloads about 900 MB of speech models once, then speaks a test sentence and transcribes it back. It prints `"ok": true` when everything works. It can take several minutes the first time; let it finish.

If Docker is running, b-speak uses it for the fastest voice. Without Docker it steps down to other voices on its own, so Docker is optional.

## 3. Give yourself the b-speak skill (if your harness supports skills)

- **Claude Code**: `bspeak skill install --claude`
- **Another harness that loads Agent Skills (SKILL.md folders)**: `bspeak skill install --dir <the folder your harness loads skills from>`
- **Otherwise**: skip this. At the start of a voice session, `bspeak brief get` gives you the same instructions.

Updates refresh the installed skill automatically.

## 4. Tell the user how to start

Let them know:

- It's ready. They need a **headset**, so the microphone does not hear your voice. (With speakers, set `"audio": { "muteMicWhilePlaying": true }` in `~/.bspeak/config.json`; they then cannot interrupt you by talking.)
- To start, they can say something like "let's talk using b-speak" in a new session.
- Saying "stop listening" pauses the microphone, and "start listening" resumes it.

## Updates

When `bspeak brief get` starts with **Update available**, ask the user whether to update. If they agree, run `bspeak update`, then join the room again and carry on.
