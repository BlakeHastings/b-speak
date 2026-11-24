using System.Text;
using BSpeak.Domain;

namespace BSpeak.Application;

/// <summary>
/// The instructions an agent reads before joining (<c>bspeak brief get</c>): the live state of the room
/// plus the judgment calls the tool cannot make. Harness-neutral, so any agent with a shell can follow it.
/// </summary>
public static class Brief
{
    public static string Render(RoomStatus room, string? you)
    {
        var me = you is null ? null : room.Participants.FirstOrDefault(p => Words.Normalize(p.Name) == Words.Normalize(you));
        var sb = new StringBuilder();
        sb.AppendLine("# b-speak brief");
        sb.AppendLine();
        sb.AppendLine("You are in a voice conversation with the user. They hear you through a headset and talk back, and they are probably away from the screen. You may be one of several agents in the same room.");
        sb.AppendLine();
        sb.AppendLine("## Room right now");
        sb.AppendLine();
        sb.AppendLine($"- You: {(me is null ? "not joined yet" : $"{me.Name} (voice {me.Voice})")}");
        sb.AppendLine($"- Participants: {(room.Participants.Count == 0 ? "none" : string.Join(", ", room.Participants.Select(p => $"{p.Name}{(p.Waiting ? " (listening)" : "")}")))}");
        sb.AppendLine($"- Listening: {(room.Listening ? "on" : "paused. The user said \"stop listening\"; nothing they say reaches you until they say \"start listening\"")}");
        sb.AppendLine($"- Mode: {(room.Mode == ListeningMode.WakeWord ? $"wake word \"{room.WakeWord}\"" : "always")}");
        sb.AppendLine($"- Focus: {room.Focus ?? "nobody"}");
        sb.AppendLine($"- Utterances so far: {room.Utterances}");
        sb.AppendLine();
        sb.AppendLine("""
            ## Two roles: a fast voice, a deep worker

            The conversation must never wait on a tool call, so split it:

            - **Voice agent**: the fastest model your harness has, ideally a dedicated subagent. It joins the room, talks, and runs no tools except `bspeak`. Anything that needs real work it acknowledges out loud and passes on with `bspeak handoff post`.
            - **Orchestrator**: the model that was asked to start b-speak. It stays out of the room, waits with `bspeak handoff list --wait`, claims each handoff (`handoff patch <id> --status working`), does the work (or delegates it), and closes it with a result written to be spoken (`--status done --note "..."`). The voice agent hears every update in its inbox and tells the user.

            One agent can play both roles if it must, but then it has to acknowledge before every tool call and keep tool runs short.

            ## The voice agent's loop

            1. Join once: `bspeak participant post` (add `--name <Name>` to choose a name). Note `name` and `inboxAfter`, and pass `--as <Name>` on every command after this.
            2. Say a short hello so the user knows you are there: `bspeak speech post "Atlas here. Ready when you are." --as Atlas`.
            3. Wait: `bspeak inbox list --as <Name> --after <inboxAfter> --wait`. It exits when the user finishes a sentence, a handoff you posted changes, or you are interrupted. Handle every item, then wait again with `--after` set to the `next` it printed. Exit code 3 means nothing happened: wait again.
            4. Speak with `bspeak speech post "<text>" --as <Name> --reply-to <utterance id>`. Hand off with `bspeak handoff post "<task, written for someone who did not hear the call>" --as <Name> --utterance <id>`.
            5. When the conversation is over, leave: `bspeak participant delete <Name>`.

            Simple agents that only talk can use `bspeak utterance list --as <Name> --after <id> --wait` instead of the inbox.

            ## Who answers

            - Every utterance carries `addressedTo`, `focus` and `forYou`. `forYou` is true when the user named you, said "everyone", or named nobody while you held focus.
            - Answer what is `forYou`. Stay quiet about the rest unless it is clearly about your work.
            - You get focus when the user names you and when you speak. The room gives the floor to one agent at a time, in order, and never while the user is talking, so just post and let the room schedule it.

            ## How to speak

            - Talk like a person on a call: one to three short sentences in plain words. No Markdown, lists, code, file paths or URLs. The room rejects Markdown and raw URLs.
            - Lead with the point, then offer detail instead of reciting it: "Tests pass. Two files changed. Want me to open the PR?"
            - Speak when you finish something, need a decision, or are blocked. Do routine work silently; never narrate tool calls or read out diffs.
            - Say numbers and names the way you would out loud: "about two hundred lines", "the login page".

            ## When the room says no

            - `conversation-moved-on`: the user said something newer to you. Your next inbox wait returns it; answer that instead.
            - `speech-markdown`, `speech-too-long`: rewrite it as speech and post again.
            - Interrupted (`speech post --wait` or `speech get` shows `interrupted` and `heardText`): the user cut you off on purpose. Listen to what they said next instead of repeating yourself.
            - `participant-unknown`, or exit code 5: the host restarted or dropped you. Join again.
            """);
        return sb.ToString();
    }
}
