using BSpeak.Domain;

namespace BSpeak.Domain.Tests;

public sealed class SpokenTextTests
{
    [Theory]
    [InlineData("Run this:\n```\ndotnet test\n```", "a code fence")]
    [InlineData("Run `dotnet test` again.", "inline code")]
    [InlineData("# Summary\nAll good.", "a heading")]
    [InlineData("Changes:\n- parser\n- tests", "a list item")]
    [InlineData("Steps:\n1. build\n2. test", "a list item")]
    [InlineData("| a | b |", "a table row")]
    [InlineData("This is **important**.", "bold markup")]
    [InlineData("See [the docs](docs.md).", "a markdown link")]
    [InlineData("It is at https://example.com now.", "a raw URL")]
    public void Finds_markdown(string text, string expected) => Assert.Equal(expected, SpokenText.FindMarkdown(text));

    [Theory]
    [InlineData("Tests pass. Three files changed. Want me to open the PR?")]
    [InlineData("Version 2.0 shipped at 3 p.m., and the build is green!")]
    [InlineData("I think - roughly - it's done.")]
    public void Accepts_plain_speech(string text) => Assert.Null(SpokenText.FindMarkdown(text));

    [Fact]
    public void Splits_sentences_on_terminal_punctuation_and_newlines()
    {
        Assert.Equal(
            ["Tests finished.", "Two failures!", "Want details?", "Version 2.0 is out"],
            SpokenText.Sentences("Tests finished. Two failures! Want details?\nVersion 2.0 is out"));
    }
}

public sealed class ControlPhraseTests
{
    private static readonly ControlPhrases Phrases = ControlPhrases.Default;

    [Theory]
    [InlineData("Stop listening.", ControlAction.Pause)]
    [InlineData("okay, stop listening please", ControlAction.Pause)]
    [InlineData("Pause listening, thank you.", ControlAction.Pause)]
    [InlineData("Start listening!", ControlAction.Resume)]
    [InlineData("resume listening", ControlAction.Resume)]
    public void Matches_whole_utterances(string said, ControlAction expected) => Assert.Equal(expected, Phrases.Match(said));

    [Theory]
    [InlineData("Tell the server to stop listening on 8080.")]
    [InlineData("Atlas, stop listening to the queue.")]
    [InlineData("stop")]
    [InlineData("")]
    public void Ignores_phrases_inside_sentences(string said) => Assert.Null(Phrases.Match(said));
}

public sealed class AddressResolverTests
{
    private static readonly string[] Room = ["Atlas", "Nova", "Big Ben"];

    [Theory]
    [InlineData("Atlas, run the tests.", "Atlas")]
    [InlineData("atlas run the tests", "Atlas")]
    [InlineData("And Nova, keep going on the settings page.", "Nova")]
    [InlineData("Hey Nova what's the status", "Nova")]
    [InlineData("Atlis, run the tests.", "Atlas")]
    [InlineData("Big Ben, what time is it?", "Big Ben")]
    [InlineData("Everyone, stop what you're doing.", AddressResolver.Everyone)]
    [InlineData("Everybody listen up.", AddressResolver.Everyone)]
    public void Resolves_the_addressee(string said, string expected) => Assert.Equal(expected, AddressResolver.Resolve(said, Room));

    [Theory]
    [InlineData("Wait, which parser?")]
    [InlineData("All right, let's go.")]
    [InlineData("Now run it again.")]
    [InlineData("")]
    public void Returns_null_when_nobody_is_named(string said) => Assert.Null(AddressResolver.Resolve(said, Room));

    [Fact]
    public void Ambiguous_close_matches_resolve_to_nobody() =>
        Assert.Null(AddressResolver.Resolve("Nora, check this.", ["Nova", "Norm"]));
}

public sealed class ParticipantNameTests
{
    private static string Violation(Action act) => Assert.Throws<DomainViolation>(act).Code;

    [Theory]
    [InlineData("Atlas")]
    [InlineData("Big Ben")]
    [InlineData("O'Neil")]
    public void Accepts_speakable_names(string name) => Assert.Equal(name, ParticipantNames.Require($"  {name} ", ControlPhrases.Default));

    [Theory]
    [InlineData("")]
    [InlineData("R2D2")]
    [InlineData("agent_1")]
    [InlineData("AVeryLongNameThatNobodyWouldSay")]
    public void Rejects_unspeakable_names(string name) =>
        Assert.Equal("name-format", Violation(() => ParticipantNames.Require(name, ControlPhrases.Default)));

    [Theory]
    [InlineData("Everyone")]
    [InlineData("Stop")]
    [InlineData("Listening Larry")]
    public void Rejects_reserved_words(string name) =>
        Assert.Equal("name-reserved", Violation(() => ParticipantNames.Require(name, ControlPhrases.Default)));
}
