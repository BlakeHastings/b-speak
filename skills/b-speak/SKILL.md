---
name: b-speak
description: Talk with the user out loud through the b-speak voice tool (bspeak CLI) instead of the terminal. Use when the user asks to talk, have a conversation, or work by voice ("let's talk using b-speak", "switch to voice", "talk to me while I do the dishes", "join the voice room"). Sets up the two-tier pattern - a fast voice agent (Sonnet or Haiku subagent) that owns the conversation, and you as the orchestrator doing the deep work it hands off - so the conversation never waits on tool calls. Covers spawning the voice agent, the handoff loop, writing results to be spoken, and recovering when the host restarts.
---

# Talking with the user through b-speak

The user is listening through a headset, probably away from the screen. `bspeak` handles the microphone, speech to text, text to speech and turn-taking.

## The shape: a fast voice, a deep worker

A voice conversation dies if the speaker goes quiet for thirty seconds while a tool runs. So split the work:

- **The voice agent** is a fast, cheap model (Sonnet; Haiku for the snappiest replies) running as a background subagent. It is in the room. It listens, answers what it can from the conversation, asks clarifying questions, and acknowledges requests right away. Anything that needs real work it **hands off** to you and keeps talking. It never runs tools other than `bspeak`.
- **You are the orchestrator.** You are not in the room and do not speak. You wait for handoffs, do the work (yourself or through worker subagents, in parallel when it helps), and close each handoff with a result written to be said out loud. The voice agent hears it and tells the user.

The voice agent and you never talk directly. Everything goes through `bspeak handoff`, so the pattern works in any harness.

## Start (you, the orchestrator)

1. Read the room: `bspeak brief get`. The first command may take a few seconds while the host starts.
2. Spawn the voice agent in the background with the Agent tool: `subagent_type: "general-purpose"`, `model: "sonnet"` (or `"haiku"`), `run_in_background: true`, and this prompt, filled in:

   > You are the voice agent for this session. Read `~/.claude/skills/b-speak/voice-agent.md` and follow it exactly. Join the room as `<Name, or omit to let the host choose>`. What the user is working on: `<one paragraph: repo, current task, anything they said before switching to voice>`. Hand off anything that needs tools or real thinking; the orchestrator will answer through handoffs.

3. Start waiting for handoffs in the background (Bash with `run_in_background: true`):

   ```
   bspeak handoff list --wait
   ```

   It exits when a handoff is open and prints the open ones. Exit code 3 means it timed out: start it again.

## The handoff loop

When the wait exits:

1. For each open handoff, claim it at once: `bspeak handoff patch <id> --status working`. The voice agent hears the claim.
2. Start the wait again immediately, so new handoffs are never missed while you work.
3. Do the work. Use worker subagents for independent pieces. For anything longer than a minute or two, post progress the voice agent can relay: `bspeak handoff patch <id> --status working --note "Tests are running; about two minutes."`
4. Close it with a spoken result: `bspeak handoff patch <id> --status done --note "<result>"`, or `--status failed --note "<what went wrong and what you need>"`.

Write every `--note` as one to three short sentences someone would say on a call. Lead with the outcome, offer the detail: "Fixed. The login test was racing the session refresh. Want me to commit it?" No Markdown, file paths, code or URLs; the voice agent will paraphrase, but give it something sayable. If you need a decision from the user, close the handoff as done with the question in the note; the user's answer will come back as a new handoff.

## Keep the screen useful too

The user may glance at the terminal later. Keep your own replies short, and put anything long (a diff summary, a plan) in a file or your final message rather than in a note.

## When things go wrong

- The voice agent's task ends (notification) while the user is still talking: spawn a new one with the same prompt.
- `participant-unknown` or exit code 5 anywhere: the host restarted. Respawn the voice agent; restart your handoff wait.
- The user ends the conversation: the voice agent says goodbye, leaves the room, and finishes with a summary. Stop your handoff wait and finish any open handoffs as failed with a short reason.

## Without subagents

If your harness cannot run a background subagent, do both roles yourself: follow `voice-agent.md`, but run the inbox wait in the background, acknowledge before every tool call, and keep tool runs short.
