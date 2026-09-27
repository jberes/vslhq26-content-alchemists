using Bunit;
using Castmill.UI.Platform;

namespace Castmill.UI.Tests;

/// <summary>
/// The browser voice service remembers the producer's microphone per device, hands it to the
/// recorder on Record, and follows a microphone whose id the browser changed.
/// </summary>
public sealed class BrowserVoiceInputChoiceTests : CastmillUiTestContext
{
    private const string ModulePath = "./_content/Castmill.UI/js/castmill-recorder.js";
    private static readonly AudioInput BuiltIn = new("mbp", "MacBook Pro Microphone");
    private static readonly AudioInput Usb = new("usb-1", "Shure MV7");

    private readonly BunitJSModuleInterop _module;

    public BrowserVoiceInputChoiceTests()
    {
        _module = JSInterop.SetupModule(ModulePath);
        _module.Setup<VoiceCaptureSnapshot>("capability").SetResult(new VoiceCaptureSnapshot(VoiceCaptureStates.Idle));
        _module.Setup<AudioInput[]>("listInputs").SetResult([BuiltIn, Usb]);
        _module.SetupVoid("watchInputs", _ => true).SetVoidResult();
        _module.SetupVoid("start", _ => true).SetVoidResult();
    }

    private BrowserVoiceCaptureService Service() => new(JSInterop.JSRuntime, UiState);

    [Fact]
    public async Task Initialize_restores_the_saved_microphone_and_lists_and_watches_the_inputs()
    {
        await UiState.SetAsync("cm.voice.input", """{"deviceId":"usb-1","label":"Shure MV7"}""");
        var voice = Service();

        await voice.InitializeAsync();

        Assert.Equal(Usb, voice.SelectedInput);
        Assert.Equal([BuiltIn, Usb], voice.Inputs);
        Assert.Single(_module.Invocations["watchInputs"]);
    }

    [Fact]
    public async Task Choosing_saves_per_device_and_choosing_the_default_clears_it()
    {
        var voice = Service();
        await voice.InitializeAsync();

        await voice.SelectInputAsync(Usb);
        Assert.Equal("""{"deviceId":"usb-1","label":"Shure MV7"}""", await UiState.GetAsync("cm.voice.input"));

        await voice.SelectInputAsync(null);
        Assert.Equal(string.Empty, await UiState.GetAsync("cm.voice.input"));
        var fresh = Service();
        await fresh.InitializeAsync();
        Assert.Null(fresh.SelectedInput);
    }

    [Fact]
    public async Task Record_hands_the_chosen_microphone_to_the_recorder()
    {
        var voice = Service();
        await voice.InitializeAsync();
        await voice.SelectInputAsync(Usb);

        await voice.StartAsync(600);

        var start = Assert.Single(_module.Invocations["start"]);
        Assert.Equal(600, start.Arguments[1]);
        Assert.Equal(Usb, start.Arguments[2]);
    }

    [Fact]
    public async Task A_microphone_found_again_under_a_new_id_is_remembered_by_that_id()
    {
        var voice = Service();
        await voice.InitializeAsync();
        await voice.SelectInputAsync(Usb);

        await voice.OnVoiceCaptureChanged(new VoiceCaptureSnapshot(
            VoiceCaptureStates.Recording, InputLabel: "Shure MV7", InputDeviceId: "usb-rotated"));

        Assert.Equal(new AudioInput("usb-rotated", "Shure MV7"), voice.SelectedInput);
        Assert.Contains("usb-rotated", await UiState.GetAsync("cm.voice.input"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fallback_to_the_default_does_not_overwrite_the_remembered_microphone()
    {
        var voice = Service();
        await voice.InitializeAsync();
        await voice.SelectInputAsync(Usb);

        await voice.OnVoiceCaptureChanged(new VoiceCaptureSnapshot(
            VoiceCaptureStates.Recording, InputLabel: "MacBook Pro Microphone", InputDeviceId: "mbp",
            Notice: "“Shure MV7” isn't connected, so this is recording from the system default instead."));

        Assert.Equal(Usb, voice.SelectedInput);
    }

    [Fact]
    public async Task Plugging_a_microphone_in_or_out_updates_the_inputs_and_notifies()
    {
        var voice = Service();
        await voice.InitializeAsync();
        var changes = 0;
        voice.Changed += () => changes++;

        await voice.OnInputsChanged([BuiltIn]);

        Assert.Equal([BuiltIn], voice.Inputs);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task An_unreadable_saved_choice_is_ignored()
    {
        await UiState.SetAsync("cm.voice.input", "{not json");
        var voice = Service();

        await voice.InitializeAsync();

        Assert.Null(voice.SelectedInput);
    }
}
