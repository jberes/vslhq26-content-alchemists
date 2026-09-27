using Bunit;
using Castmill.UI.Design;
using Castmill.UI.Platform;

namespace Castmill.UI.Tests;

/// <summary>
/// The producer picks which microphone records. The choice is remembered per device, the list
/// follows microphones being plugged in or removed, and a remembered microphone that is gone is
/// named — the recording then uses the system default and says so instead of failing quietly.
/// </summary>
public sealed class VoiceInputPickerTests : CastmillUiTestContext
{
    private static readonly AudioInput BuiltIn = new("mbp", "MacBook Pro Microphone");
    private static readonly AudioInput Usb = new("usb-1", "Shure MV7");

    private static string[] OptionLabels(IRenderedComponent<VoiceRecorder> view) =>
        [.. view.FindAll("select option").Select(o => o.TextContent.Trim())];

    [Fact]
    public void Before_the_microphone_is_allowed_it_explains_the_default_and_offers_no_empty_list()
    {
        var view = Render<VoiceRecorder>();

        Assert.Empty(view.FindAll("select"));
        Assert.Contains("system's default microphone", view.Find(".cm-voice__source").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_picker_lists_named_inputs_after_the_system_default_and_saves_the_choice()
    {
        Voice.SetInputs(BuiltIn, Usb);
        var view = Render<VoiceRecorder>();

        Assert.Equal(["System default", "MacBook Pro Microphone", "Shure MV7"], OptionLabels(view));
        Assert.True(view.Find("select option[value='']").HasAttribute("selected"));

        view.Find("select").Change("usb-1");
        Assert.Equal(Usb, Voice.SelectedInput);

        view.Find("select").Change("");
        Assert.Null(Voice.SelectedInput);
        Assert.Equal([Usb, null], Voice.Selections);
    }

    [Fact]
    public void Record_opens_the_chosen_input_and_the_recorder_names_it()
    {
        Voice.SetInputs(BuiltIn, Usb);
        Voice.SelectedInput = Usb;
        var view = Render<VoiceRecorder>();
        Assert.True(view.Find("select option[value='usb-1']").HasAttribute("selected"));

        view.FindAll("button").Single(b => b.TextContent.Trim() == "Record").Click();

        Assert.Equal(Usb, Voice.StartedWith);
        Assert.Contains("Recording from Shure MV7", view.Find(".cm-voice__source").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_remembered_input_that_is_unplugged_is_named_and_record_warns_it_will_use_the_default()
    {
        Voice.SetInputs(BuiltIn);
        Voice.SelectedInput = Usb;
        var view = Render<VoiceRecorder>();

        var missing = view.Find("select option[value='usb-1']");
        Assert.Equal("Shure MV7 (not connected)", missing.TextContent.Trim());
        Assert.True(missing.HasAttribute("disabled"));
        Assert.Contains("“Shure MV7” isn't connected. Record will use the system default.",
            view.Find(".cm-voice__notice").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Plugging_a_microphone_in_or_out_updates_the_list_and_the_warning_live()
    {
        Voice.SetInputs(BuiltIn, Usb);
        Voice.SelectedInput = Usb;
        var view = Render<VoiceRecorder>();
        Assert.Empty(view.FindAll(".cm-voice__notice"));

        view.InvokeAsync(() => Voice.SetInputs(BuiltIn));
        view.WaitForAssertion(() =>
        {
            Assert.Equal(["System default", "MacBook Pro Microphone", "Shure MV7 (not connected)"], OptionLabels(view));
            Assert.NotEmpty(view.FindAll(".cm-voice__notice"));
        });

        view.InvokeAsync(() => Voice.SetInputs(BuiltIn, Usb));
        view.WaitForAssertion(() =>
        {
            Assert.Equal(["System default", "MacBook Pro Microphone", "Shure MV7"], OptionLabels(view));
            Assert.Empty(view.FindAll(".cm-voice__notice"));
        });
    }

    [Fact]
    public void A_remembered_input_with_a_new_id_is_still_recognised_by_name()
    {
        Voice.SetInputs(BuiltIn, new AudioInput("usb-rotated", "Shure MV7"));
        Voice.SelectedInput = Usb;
        var view = Render<VoiceRecorder>();

        Assert.True(view.Find("select option[value='usb-rotated']").HasAttribute("selected"));
        Assert.Empty(view.FindAll(".cm-voice__notice"));
    }

    [Fact]
    public void A_fallback_while_recording_is_announced_with_the_input_actually_used()
    {
        var view = Render<VoiceRecorder>();
        view.InvokeAsync(() => Voice.Set(new VoiceCaptureSnapshot(
            VoiceCaptureStates.Recording,
            InputLabel: "MacBook Pro Microphone",
            Notice: "“Shure MV7” isn't connected, so this is recording from the system default instead.")));

        view.WaitForAssertion(() =>
        {
            Assert.Contains("Recording from MacBook Pro Microphone", view.Find(".cm-voice__source").TextContent, StringComparison.Ordinal);
            var notice = view.Find(".cm-voice__notice");
            Assert.Equal("alert", notice.GetAttribute("role"));
            Assert.Contains("Shure MV7", notice.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_stopped_recording_says_which_input_it_came_from()
    {
        var view = Render<VoiceRecorder>();
        view.InvokeAsync(() => Voice.Set(new VoiceCaptureSnapshot(
            VoiceCaptureStates.Stopped, 4, PlaybackUrl: "blob:x", ContentType: "audio/webm", SizeBytes: 2048,
            InputLabel: "Shure MV7")));

        view.WaitForAssertion(() =>
            Assert.Contains("Recorded from Shure MV7", view.Find(".cm-voice__source").TextContent, StringComparison.Ordinal));
    }
}
