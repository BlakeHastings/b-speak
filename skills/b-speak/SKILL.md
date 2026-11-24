---
name: b-speak
description: Talk with the user out loud through the b-speak voice tool (bspeak CLI) instead of the terminal. Use when the user asks to talk, have a conversation, or work by voice ("let's talk using b-speak", "switch to voice", "talk to me while I do the dishes", "join the voice room"), or when they speak to you through b-speak and you need to answer. Covers joining the room, listening while you work, speaking in plain conversational sentences instead of Markdown, the usual fast-voice-plus-background-worker setup, and recovering when the room refuses a speech.
---

# Talking with the user through b-speak

The user is listening through a headset, probably away from the screen. `bspeak` handles the microphone, speech to text, text to speech and turn-taking. You wait for what they say and post what you want to say. Start with `bspeak brief get`: it has the commands and the current state of the room.

## How this is usually set up

Conversation feels broken when the speaker goes quiet for half a minute while a tool runs. So the common setup is a quick conversational agent paired with a deeper one working in the background. The model the user asked (say Opus) spins up a faster model (Sonnet or Haiku) as a background subagent to handle the room: listening, chatting, acknowledging requests. The deeper agent does the heavy work and orchestrates any workers, and the two keep each other informed however suits the harness: subagent messages, or `bspeak handoff` if that's easier. Use your judgment; a short exchange may not need two agents at all.

## Joining and listening

1. Join: `bspeak participant post` (or `--name <Name>`). Remember the `name` and `inboxAfter` it prints, and pass `--as <Name>` on later commands.
2. Say a quick hello so they know you're there.
3. Wait for them: `bspeak inbox list --as <Name> --after <inboxAfter> --wait`. It returns when they finish a sentence, when one of your handoffs changes, or when they interrupt you. Pass the `next` it prints as `--after` on the following wait. Exit code 3 just means nothing happened; wait again. If you're also doing other work, run the wait in the background (Bash `run_in_background: true`) so you keep listening.

Each utterance has `forYou`: true when they named you, said "everyone", or named nobody while you were the one they were talking to. Other agents may share the room; let them answer what's meant for them.

## Speaking

`bspeak speech post "<text>" --as <Name> --reply-to <utterance id>`

- Talk like a person on a call: a sentence or three, the point first. "Done. Tests pass and I changed two files. Want me to commit?"
- Plain words. No Markdown, lists, code, file paths or URLs; the room rejects Markdown and raw URLs.
- Speak when you finish something, need a decision, or hit something they'd want to know. Don't narrate tool calls or read out diffs and logs.

## When the room pushes back

- `conversation-moved-on`: they said something newer; your next wait has it.
- Interrupted (an inbox item of kind `speech-interrupted`): they talked over you on purpose. Respond to what they said next.
- "Stop listening": nothing reaches you until "start listening". Keep waiting anyway.
- `participant-unknown` or exit code 5: the host restarted. Join again.

When they wrap up, say goodbye and `bspeak participant delete <Name>`.
