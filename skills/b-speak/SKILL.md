---
name: b-speak
description: Talk with the user out loud through the b-speak voice tool (bspeak CLI) instead of the terminal. Use when the user asks to talk, have a conversation, or work by voice ("let's talk using b-speak", "switch to voice", "talk to me while I do the dishes", "join the voice room"), or when they speak to you through b-speak and you need to answer. Covers joining the room, listening while you work, speaking in plain conversational sentences instead of Markdown, the usual fast-voice-plus-background-worker setup, and recovering when the room refuses a speech.
---

# Talking with the user through b-speak

The user is listening through a headset, probably away from the screen. `bspeak` handles the microphone, speech to text, text to speech and turn-taking. You wait for what they say and post what you want to say. Start with `bspeak brief get`: it has the commands and the current state of the room. If it opens with "Update available", ask the user before anything else; if they agree, run `bspeak update` and then carry on.

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
- Remember you are in a room. If you have not spoken for a while, or the last exchange was someone else on a different topic, open with a micro introduction so they know who is talking and what about: "Rovo, a follow-up on the login bug: it is fixed." Skip it when you are mid-conversation with them; it should only appear when the context has swung or a long time has passed. Each inbox result has a `context` block to judge by: who was heard last (`lastHeard`) and how long ago, when you last spoke, and when they last spoke to you.

## Hands up

The user can switch the room to hands up ("hands up") and back ("open floor"), like a large video call where people raise a hand and wait to be called on. `context.turns` in each inbox result says which is on.

In hands up, answering something the user just said to you still plays right away; direct replies skip the line. Anything else you post raises your hand: they hear a soft chime and your speech waits until they call on you ("Atlas, go ahead", or "next"). `speech post` returns straight away with `handUp: true`. You get one hand, so keep working and keep it current. When the conversation moves on, change what you'll say with `bspeak speech patch <id> "<new text>" --as <Name>`, which keeps your place in line, or lower it with `bspeak speech delete <id> --as <Name>` if it no longer matters. `context.yourHand` holds the id while it's up. Hands stay up when the user says something new, so after each utterance ask yourself whether yours still fits.

## Volume and voices

If they ask you to turn it down (or up), run `bspeak volume put down` (`up`, or 0 to 100), then say something short at the new level. If they want a different voice, `bspeak voice list` shows what is available and who uses what; pick a free one and run `bspeak participant patch <Name> --voice <voice>`.

## When the room pushes back

- `conversation-moved-on`: they said something newer; your next wait has it.
- Interrupted or withdrawn (inbox items `speech-interrupted` and `speech-withdrawn`): when they talk over you, b-speak stops you and drops whatever you had queued; nothing resumes on its own. Read what they said, then decide whether anything you were going to say still matters. Usually answer them instead; if something is still worth saying, say a shorter version. Never repost the old text unchanged.
- `hand-already-up`: your hand is already up; revise it with `speech patch` rather than posting again.
- "Stop listening": nothing reaches you until "start listening". Keep waiting anyway.
- `participant-unknown` or exit code 5: the host restarted. Join again.

When they wrap up, say goodbye and `bspeak participant delete <Name>`.
