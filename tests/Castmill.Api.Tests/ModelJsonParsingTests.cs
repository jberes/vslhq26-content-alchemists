using Castmill.Api.Services.Ai;

namespace Castmill.Api.Tests;

/// <summary>
/// Every generator funnels its reply through <c>ParseModelJson</c>. A Tech Edit run with the
/// knowledge base attached failed on 2026-09-06 with "Tech edit failed: JsonReaderException" —
/// System.Text.Json's internal parse-error type, surfaced by name, which told the producer
/// nothing about what went wrong (ADR-066).
/// </summary>
public sealed class ModelJsonParsingTests
{
    [Fact]
    public void Plain_json_parses()
    {
        var parsed = AiOrchestrator.ParseModelJson("""{"title":"A"}""");
        Assert.Equal("A", parsed.GetProperty("title").GetString());
    }

    [Fact]
    public void A_fenced_block_parses()
    {
        var parsed = AiOrchestrator.ParseModelJson("```json\n{\"title\":\"A\"}\n```");
        Assert.Equal("A", parsed.GetProperty("title").GetString());
    }

    [Fact]
    public void An_object_wrapped_in_chat_is_salvaged()
    {
        var parsed = AiOrchestrator.ParseModelJson(
            "Here is the updated artifact:\n{\"title\":\"A\",\"changes\":[]}\nLet me know if you need anything else.");
        Assert.Equal("A", parsed.GetProperty("title").GetString());
    }

    [Fact]
    public void Prose_with_no_object_reports_what_the_model_actually_said()
    {
        var error = Assert.Throws<Castmill.Api.Services.Ai.ModelJsonException>(
            () => AiOrchestrator.ParseModelJson("I cannot complete this request."));

        Assert.Contains("did not return JSON", error.Message, StringComparison.Ordinal);
        Assert.Contains("I cannot complete this request.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncated_json_is_reported_rather_than_thrown_as_a_reader_exception()
    {
        var error = Assert.Throws<Castmill.Api.Services.Ai.ModelJsonException>(
            () => AiOrchestrator.ParseModelJson("""{"title":"A","markdown":"half a sent"""));

        Assert.Contains("did not return JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_reply_says_so()
    {
        var error = Assert.Throws<Castmill.Api.Services.Ai.ModelJsonException>(() => AiOrchestrator.ParseModelJson("   "));
        Assert.Contains("(nothing)", error.Message, StringComparison.Ordinal);
    }
}
