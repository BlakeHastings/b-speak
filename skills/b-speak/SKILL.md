---
name: b-speak
description: Talk with the user out loud through the b-speak voice tool (bspeak CLI) instead of the terminal. Use when the user asks to talk, have a conversation, or work by voice ("let's talk using b-speak", "switch to voice", "talk to me while I do the dishes", "join the voice room"), or when they speak to you through b-speak and you need to answer. Covers joining the room, keeping a background listener running while you work, speaking in plain conversational sentences instead of Markdown, sharing the room with other agents, and recovering when the room refuses a speech.
---

# Talking with the user through b-speak

The user is listening through a headset, probably away from the screen. `bspeak` handles the microphone, speech to text, text to speech and turn-taking. You only wait for what they say and post what you want to say.

## Start

1. Read the live instructions and room state: `bspeak brief get`. Follow them; they are the source of truth for the commands and the rules of the room. The first command may take a moment while the host starts.
2. Join: `bspeak participant post` (or `--name <Name>` if the user named you). Remember the `name` and the `after` number it prints. Pass `--as <Name>` on every later command.
3. Say hello so they know you are there: `bspeak speech post "Atlas here. What are we working on?" --as Atlas`.
4. Start listening in the background (below), then wait for them to talk.

## Keep a listener running, in the background

Run the wait with the Bash tool's `run_in_background: true`:

```
bspeak utterance list --as <Name> --after <after> --wait
```

You are notified when it exits. Read its output: each utterance has an `id`, `text`, and `forYou`. Then:

- Immediately start a new background wait with `--after` set to the highest `id` you received. Never leave yourself without one, including while you are busy coding, or the user cannot steer you.
- Exit code 3 means it timed out with nothing new. Just start it again.
- If `forYou` is false, another agent was addressed. Stay quiet unless it is clearly about your work.

Do not poll with `sleep`, and do not run the wait in the foreground: it blocks until the user speaks.

## Speak like a person on a call

- Post with `bspeak speech post "<text>" --as <Name> --reply-to <id>`, where `<id>` is the utterance you are answering.
- One to three short sentences. Lead with the point, then offer detail: "Done. The tests pass and I changed two files. Want me to commit?"
- Plain words only. No Markdown, bullets, code, file paths, URLs, or tables. The room rejects Markdown and raw URLs; if it says `speech-markdown`, rewrite and post again.
- Say numbers and names as you would aloud: "about forty lines", "the settings page", not `src/settings/page.tsx:42`.

## Talk less, work more

Voice is for the conversation, not a narration of your work. Do the work with your normal tools, silently. Speak when you:

- finish something they asked for,
- need a decision or are blocked,
- hit something they would want to know now (a failing deploy, a destructive step you will not take without them).

Never read out diffs, logs, or tool output. If they want detail, they will ask, or you can offer to put it on screen. Keep terminal text short too; they are not looking at it.

## Sharing the room

Other agents may be in the room. The user addresses one by starting with its name ("Nova, ..."). Answer only what is `forYou`. The room schedules who speaks, one at a time and never over the user, so just post; do not wait for silence yourself.

## When the room says no, or cuts you off

- `conversation-moved-on`: they said something newer to you. Read it (`utterance list --as <Name> --after <last id>`) and answer that instead.
- Interrupted (`speech post --wait` or `speech get <id>` shows `interrupted`): they talked over you on purpose. Respond to what they said next; do not repeat yourself.
- They say "stop listening": nothing reaches you until "start listening". Keep your wait running anyway.
- `participant-unknown` or exit code 5: the host restarted. Join again and start a new wait.

## Finish

When the user ends the conversation, say a short goodbye, then `bspeak participant delete <Name>`. Leave the host running; other agents may be using it.
