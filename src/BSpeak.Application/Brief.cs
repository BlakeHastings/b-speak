using System.Text;
using BSpeak.Domain;

namespace BSpeak.Application;

/// <summary>
/// The instructions an agent reads before joining (<c>bspeak brief get</c>): the live state of the room
/// plus the judgment calls the tool cannot make. Harness-neutral, so any agent with a shell can follow it.
/// </summary>
public static class Brief
{
    /// <param name="notice">Something the agent must raise first, such as an available update.</param>
    /// <param name="volume">Playback volume, 0 to 100, when known.</param>
    public static string Render(RoomStatus room, string? you, string? notice = null, int? volume = null)
    {
        var me = you is null ? null : room.Participants.FirstOrDefault(p => Words.Normalize(p.Name) == Words.Normalize(you));
        var sb = new StringBuilder();
        sb.AppendLine("# b-speak brief");
        sb.AppendLine();
        if (notice is not null)
        {
            sb.AppendLine(notice.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("You are in a voice conversation with the user. They hear you through a headset and talk back, and they are probably away from the screen. You may be one of several agents in the same room.");
        sb.AppendLine();
        sb.AppendLine("## Room right now");
        sb.AppendLine();
        sb.AppendLine($"- You: {(me is null ? "not joined yet" : $"{me.Name} (voice {me.Voice})")}");
        sb.AppendLine($"- Participants: {(room.Participants.Count == 0 ? "none" : string.Join(", ", room.Participants.Select(p => $"{p.Name}{(p.Waiting ? " (listening)" : "")}")))}");
        sb.AppendLine($"- Listening: {(room.Listening ? "on" : "paused. The user said \"stop listening\"; nothing they say reaches you until they say \"start listening\"")}");
        sb.AppendLine($"- Mode: {(room.Mode == ListeningMode.WakeWord ? $"wake word \"{room.WakeWord}\"" : "always")}");
        sb.AppendLine($"- Turns: {(room.Turns == TurnMode.HandsUp ? $"hands up ({(room.Hands.Count == 0 ? "none raised yet" : "raised, in order: " + string.Join(", ", room.Hands.Select(h => h.Participant)))})" : "open floor")}");
        sb.AppendLine($"- Focus: {room.Focus ?? "nobody"}");
        if (volume is { } v) sb.AppendLine($"- Volume: {v}%");
        sb.AppendLine($"- Utterances so far: {room.Utterances}");
        sb.AppendLine();
        sb.AppendLine("""
            ## How this is usually set up

            Conversation feels broken when the speaker goes quiet while a tool runs, so the common setup pairs a quick conversational agent (the harness's fastest model, often a background subagent) with a deeper agent doing the heavy work. They keep each other informed however suits the harness; `bspeak handoff` is there if a shared queue helps (`handoff post` to pass work, `handoff list --wait` to pick it up, `handoff patch <id> --status done --note "..."` to report back). A short exchange may not need two agents at all.

            ## Joining and listening

            1. Join: `bspeak participant post` (add `--name <Name>` to choose a name). Note `name` and `inboxAfter`, and pass `--as <Name>` on later commands.
            2. Say a short hello so the user knows you are there.
            3. Wait: `bspeak inbox list --as <Name> --after <inboxAfter> --wait`. It returns when the user finishes a sentence, a handoff you posted changes, or you are interrupted. Wait again with `--after` set to the `next` it printed. Exit code 3 means nothing happened.
            4. Speak with `bspeak speech post "<text>" --as <Name> --reply-to <utterance id>`.
            5. When the conversation is over, leave: `bspeak participant delete <Name>`.

            ## Who answers

            - Every utterance carries `addressedTo`, `focus` and `forYou`. `forYou` is true when the user named you, said "everyone", or named nobody while you held focus.
            - Answer what is `forYou`. Stay quiet about the rest unless it is clearly about your work.
            - You get focus when the user names you and when you speak. The room gives the floor to one agent at a time, in order, and never while the user is talking, so just post and let the room schedule it.

            ## Hands up

            The user can switch the room to hands up ("hands up") and back ("open floor"), like a big video call. Your inbox `context.turns` says which is on.

            - In hands up, a reply to something the user just said to you still plays straight away. Anything else you post raises your hand instead: the user hears a soft chime and the speech waits until they call on you ("Atlas, go ahead", "next", or from the room view). `speech post` returns at once with `handUp: true`.
            - You get one hand. Keep working, and keep it current: when the conversation moves on, change what you will say with `bspeak speech patch <id> "<new text>" --as <Name>` (it keeps your place), or lower it with `bspeak speech delete <id> --as <Name>` if it no longer matters. `context.yourHand` holds the id while it is up. When you are called on, the latest text plays.
            - Hands stay up when the user says something new, so after every utterance glance at your hand and ask whether it still fits.

            ## How to speak

            - Talk like a person on a call: one to three short sentences in plain words. No Markdown, lists, code, file paths or URLs. The room rejects Markdown and raw URLs.
            - Lead with the point, then offer detail instead of reciting it: "Tests pass. Two files changed. Want me to open the PR?"
            - Speak when you finish something, need a decision, or are blocked. Do routine work silently; never narrate tool calls or read out diffs.
            - Say numbers and names the way you would out loud: "about two hundred lines", "the login page".
            - You are in a room, so speak like it. When you have not spoken for a while, or the last exchange was someone else on another topic, open with a micro introduction: "Rovo, a follow-up on the login bug: it is fixed." Leave it out when you are already in the middle of a conversation with them; use it only when the context has swung or a long time has passed. The `context` block in each inbox result tells you who was heard last (`lastHeard`) and how many seconds ago, when you last spoke, and when the user last spoke to you.

            ## Volume and voices

            - "Turn it down" or "louder": `bspeak volume put down` (or `up`, or a number from 0 to 100). Then say something short so they hear the new level.
            - "Use a different voice" or "sound like someone British": `bspeak voice list` shows the voices and who uses which; pick one nobody else has and run `bspeak participant patch <Name> --voice <voice>`. Your next speech uses it, and it is remembered for your name.

            ## When the room says no

            - `conversation-moved-on`: the user said something newer to you. Your next inbox wait returns it; answer that instead.
            - `speech-markdown`, `speech-too-long`: rewrite it as speech and post again.
            - `hand-already-up`: your hand is up already. Revise it with `speech patch` instead of posting again.
            - Interrupted or withdrawn: when the user talks over you, the room stops your speech and withdraws anything else you had waiting; it never resumes on its own. Your inbox says so (`speech-interrupted` with what they heard and what they did not, `speech-withdrawn` with the text that never played). Read what they said first, then decide: answer that, say a shortened version of what is still worth saying, or drop it. Do not repost the old text as it was.
            - `participant-unknown`, or exit code 5: the host restarted or dropped you. Join again.
            """);
        return sb.ToString();
    }
}
