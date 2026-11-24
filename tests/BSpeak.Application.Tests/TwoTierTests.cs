using BSpeak.Application;
using BSpeak.Domain;

namespace BSpeak.Application.Tests;

/// <summary>The voice agent talks; the orchestrator works. They meet through handoffs and the inbox.</summary>
public sealed class TwoTierTests
{
    [Fact]
    public async Task Voice_agent_hands_off_work_and_hears_the_result()
    {
        var h = new Harness();
        var atlas = h.Room.Join("Atlas");

        // The voice agent waits on its inbox; you ask for something that needs real work.
        var firstWait = h.Room.WaitForInboxAsync("Atlas", atlas.InboxAfter, TimeSpan.FromSeconds(5));
        h.Say("Atlas, can you fix the flaky login test?");
        var heard = await firstWait;
        var utterance = Assert.Single(heard.Items);
        Assert.Equal(("utterance", 1, true), (utterance.Kind, utterance.UtteranceId, utterance.ForYou));

        // It acknowledges right away and hands the work off.
        h.Room.PostSpeech("Atlas", "On it. I'll let you know.", 1);
        var handoff = h.Room.PostHandoff("Atlas", "Fix the flaky login test in the auth suite.", 1);
        Assert.Equal(("h1", HandoffStatus.Open), (handoff.Id, handoff.Status));

        // The orchestrator's wait sees the open handoff and claims it.
        var open = await h.Room.WaitForOpenHandoffsAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("h1", Assert.Single(open).Id);
        h.Room.UpdateHandoff("h1", "orchestrator", HandoffStatus.Working, null);
        Assert.Empty(await h.Room.WaitForOpenHandoffsAsync(TimeSpan.FromMilliseconds(50)));

        // The voice agent's next wait returns the claim at once; the one after wakes on the result.
        var claimed = await h.Room.WaitForInboxAsync("Atlas", heard.Next, TimeSpan.FromSeconds(5));
        Assert.Equal(HandoffStatus.Working, Assert.Single(claimed.Items).Status);
        var resultWait = h.Room.WaitForInboxAsync("Atlas", claimed.Next, TimeSpan.FromSeconds(5));
        Assert.False(resultWait.IsCompleted);
        h.Room.UpdateHandoff("h1", "orchestrator", HandoffStatus.Done, "Fixed. The test was racing the session refresh.");
        var done = Assert.Single((await resultWait).Items);
        Assert.Equal((HandoffStatus.Done, "Fixed. The test was racing the session refresh."), (done.Status!.Value, done.Text));
    }

    [Fact]
    public void Inbox_reports_your_own_interruptions_and_not_other_agents_handoffs()
    {
        var h = new Harness();
        var atlas = h.Room.Join("Atlas");
        h.Room.Join("Nova");
        h.Room.PostHandoff("Nova", "Nova's work.", null);
        h.Room.UpdateHandoff("h1", null, HandoffStatus.Working, null);

        h.Room.PostSpeech("Atlas", "First point. Second point.", null);
        h.Room.PlaybackStarted("s1");
        h.Room.SentencePlayed("s1", 0);
        h.Say("Wait.");

        var inbox = h.Room.Inbox("Atlas", atlas.InboxAfter);
        Assert.Equal(["speech-interrupted", "utterance"], inbox.Items.Select(i => i.Kind));
        Assert.Equal("First point.", inbox.Items[0].Text);
        Assert.Empty(h.Room.Inbox("Atlas", inbox.Next).Items);
    }

    [Fact]
    public async Task Inbox_wait_times_out_with_the_cursor_moved_forward()
    {
        var h = new Harness();
        var atlas = h.Room.Join("Atlas");
        h.Room.Join("Nova"); // an event, but nothing for Atlas
        var batch = await h.Room.WaitForInboxAsync("Atlas", atlas.InboxAfter, TimeSpan.FromMilliseconds(50));
        Assert.True(batch.TimedOut);
        Assert.Empty(batch.Items);
        Assert.True(batch.Next > atlas.InboxAfter);
    }
}
