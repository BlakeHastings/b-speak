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
            ## The loop

            1. Join once: `bspeak participant post` (add `--name <Name>` to choose a name). Note the `name` and `after` it prints, and pass `--as <Name>` on every command after this.
            2. Say a short hello so the user knows you are there: `bspeak speech post "Atlas here. Ready when you are." --as Atlas`.
            3. Wait for the user in the background: `bspeak utterance list --as <Name> --after <after> --wait`. It exits as soon as they finish a sentence and prints the new utterances. Exit code 3 means it timed out with nothing new: start it again.
            4. Act on what they said, then start the wait again with `--after` set to the last utterance id you received. Keep a wait running at all times, including while you work, so the user can steer you mid-task.
            5. Speak with `bspeak speech post "<text>" --as <Name> --reply-to <utterance id>`. Add `--wait` to block until it has played and see what was heard.
            6. When the conversation is over, leave: `bspeak participant delete <Name>`.

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

            - `conversation-moved-on`: the user said something newer to you. Read it (`utterance list --after <your last id>`) and answer that instead.
            - `speech-markdown`, `speech-too-long`: rewrite it as speech and post again.
            - Interrupted (`speech post --wait` or `speech get` shows `interrupted` and `heardText`): the user cut you off on purpose. Listen to what they said next instead of repeating yourself.
            - `participant-unknown`, or exit code 5: the host restarted or dropped you. Join again.
            """);
        return sb.ToString();
    }
}
