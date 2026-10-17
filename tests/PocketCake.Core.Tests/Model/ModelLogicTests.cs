using System.Text.Json.Nodes;
using PocketCake.Core.Model;

namespace PocketCake.Core.Tests.Model;

public sealed class FootswitchTests
{
    [Fact]
    public void AssignmentOfReadsModuleBitFs1WinsTie()
    {
        Assert.Equal(FootswitchAssignment.Fs1, Footswitch.AssignmentOf(1 << 7, 0, 7));
        Assert.Equal(FootswitchAssignment.Fs2, Footswitch.AssignmentOf(0, 1 << 7, 7));
        Assert.Equal(FootswitchAssignment.None, Footswitch.AssignmentOf(0, 0, 7));
        Assert.Equal(FootswitchAssignment.Fs1, Footswitch.AssignmentOf(1 << 7, 1 << 7, 7));
        Assert.Equal(FootswitchAssignment.None, Footswitch.AssignmentOf(1 << 2, 0, 7));
    }

    [Fact]
    public void EmptyOrPartlyFilledSwitchesStaySelectable()
    {
        Assert.Empty(Footswitch.DisabledOptions(0, 0, 7));
        Assert.Empty(Footswitch.DisabledOptions((1 << 0) | (1 << 1), 0, 7));
    }

    [Fact]
    public void SwitchFullWithOtherModulesBlocks()
    {
        var full = (1 << 0) | (1 << 1) | (1 << 2);

        Assert.Equal([FootswitchAssignment.Fs1], Footswitch.DisabledOptions(full, 0, 7));
        Assert.Equal([FootswitchAssignment.Fs2], Footswitch.DisabledOptions(0, full, 7));
    }

    [Fact]
    public void FullSwitchAlreadyOnIsNotBlocked()
    {
        Assert.Empty(Footswitch.DisabledOptions((1 << 0) | (1 << 1) | (1 << 7), 0, 7));
    }

    [Fact]
    public void ModuleCountCountsSetBits()
    {
        Assert.Equal(0, Footswitch.ModuleCount(0));
        Assert.Equal(1, Footswitch.ModuleCount(1 << 8));
        Assert.Equal(2, Footswitch.ModuleCount((1 << 7) | (1 << 8)));
    }
}

public sealed class PresetRefTests
{
    [Fact]
    public void AllLists50UserAnd50Factory()
    {
        var all = PresetRef.All;

        Assert.Equal(100, all.Count);
        Assert.Equal(new PresetRef(PresetBank.User, 1), all[0]);
        Assert.Equal(new PresetRef(PresetBank.User, 50), all[49]);
        Assert.Equal(new PresetRef(PresetBank.Factory, 1), all[50]);
        Assert.Equal(new PresetRef(PresetBank.Factory, 50), all[^1]);
    }

    [Fact]
    public void LabelFormatsBankAndNumber()
    {
        Assert.Equal("P01", new PresetRef(PresetBank.User, 1).Label);
        Assert.Equal("F50", new PresetRef(PresetBank.Factory, 50).Label);
    }

    [Fact]
    public void ValueEquality()
    {
        Assert.Equal(new PresetRef(PresetBank.User, 3), new PresetRef(PresetBank.User, 3));
        Assert.NotEqual(new PresetRef(PresetBank.User, 3), new PresetRef(PresetBank.Factory, 3));
    }
}

public sealed class SliderMathTests
{
    [Fact]
    public void SnapToStep()
    {
        Assert.Equal(50, SliderMath.SnapToStep(50.4, 1));
        Assert.Equal(51, SliderMath.SnapToStep(50.6, 1));
        Assert.Equal(2.3, SliderMath.SnapToStep(2.34, 0.1));
    }

    [Fact]
    public void NudgeMovesClampsSnaps()
    {
        Assert.Equal(51, SliderMath.Nudge(50, 1, 1, 0, 100));
        Assert.Equal(49, SliderMath.Nudge(50, 1, -1, 0, 100));
        Assert.Equal(100, SliderMath.Nudge(100, 1, 1, 0, 100));
        Assert.Equal(0, SliderMath.Nudge(0, 1, -1, 0, 100));
    }

    [Fact]
    public void ParseSliderValue()
    {
        Assert.Equal(75, SliderMath.ParseSliderValue("75", 0, 100, 1));
        Assert.Equal(100, SliderMath.ParseSliderValue("999", 0, 100, 1));
        Assert.Equal(0, SliderMath.ParseSliderValue("-5", 0, 100, 1));
        Assert.Null(SliderMath.ParseSliderValue("abc", 0, 100, 1));
        Assert.Null(SliderMath.ParseSliderValue("", 0, 100, 1));
    }

    [Fact]
    public void ValueToRot()
    {
        Assert.Equal(0, SliderMath.ValueToRot(0, 0, 100));
        Assert.Equal(300, SliderMath.ValueToRot(100, 0, 100));
        Assert.Equal(150, SliderMath.ValueToRot(50, 0, 100));
        Assert.Equal(0, SliderMath.ValueToRot(5, 5, 5));
    }

    // dragLinearStep has no Core counterpart (UI drag math), so its two Dart tests are not ported.
}

public sealed class TapTempoTests
{
    [Fact]
    public void ClampBpm()
    {
        Assert.Equal(20, TapTempo.ClampBpm(10));
        Assert.Equal(300, TapTempo.ClampBpm(350));
        Assert.Equal(120.5, TapTempo.ClampBpm(120.3));
        Assert.Equal(120.0, TapTempo.ClampBpm(120.1));
    }

    [Fact]
    public void DelayMs()
    {
        Assert.Equal(500, TapTempo.DelayMs(120, 1.0));
        Assert.Equal(250, TapTempo.DelayMs(120, 0.5));
        Assert.Equal(375, TapTempo.DelayMs(120, 0.75));
        Assert.Equal(1000, TapTempo.DelayMs(120, 2.0));
        Assert.Equal(6000, TapTempo.DelayMs(20, 2.0));
    }

    [Fact]
    public void BpmFromTapsAveragesIntervals()
    {
        Assert.Equal(120, TapTempo.BpmFromTaps([0, 500]));
        Assert.Equal(120, TapTempo.BpmFromTaps([0, 500, 1000, 1500]));
        Assert.Null(TapTempo.BpmFromTaps([0]));
        Assert.Null(TapTempo.BpmFromTaps([]));
        Assert.Null(TapTempo.BpmFromTaps([0, 3500]));
        Assert.Null(TapTempo.BpmFromTaps([0, 0]));
    }

    [Fact]
    public void NoteDivisions()
    {
        Assert.Equal([0.5, 0.75, 1.0, 2.0], TapTempo.NoteDivisions.Select(d => d.Multiplier));
        Assert.Equal(("1/4", 1.0), TapTempo.NoteDivisions[2]);
    }
}

public sealed class PresetJsonTests
{
    private static DataAssets Data => TestData.Assets;

    private static Dictionary<int, Dictionary<int, double>> Params(int module, int alg, double v) =>
        new() { [module] = new() { [alg] = v } };

    [Fact]
    public void ToJsonEmitsVersionModeVolumeAndModule()
    {
        var state = new DecodedPresetState(false, 75, 140, new Dictionary<string, bool> { ["NR"] = true }, ["NR"]);

        var json = JsonNode.Parse(PresetJson.ToJson(state, new Dictionary<int, int> { [0] = 1 }, Params(0, 0, 50), Data, "P"))!;

        Assert.Equal("1.0", (string?)json["version"]);
        Assert.Equal("Normal", (string?)json["ampMode"]);
        Assert.Equal(75, (int)json["presetVolume"]!);
        Assert.Equal(140, (int)json["presetBpm"]!);

        var nr = json["modules"]!["NR"]!;

        Assert.True((bool)nr["enabled"]!);
        Assert.Equal("Gate", (string?)nr["effect"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"THRE":50}"""), nr["parameters"]));
    }

    [Fact]
    public void CloneModeEmitsCloneModule()
    {
        var state = new DecodedPresetState(true, 50, 120, new Dictionary<string, bool> { ["AMP"] = true }, []);

        var json = JsonNode.Parse(PresetJson.ToJson(state, new Dictionary<int, int> { [9] = 901 }, new Dictionary<int, Dictionary<int, double>>(), Data, null))!;
        var modules = json["modules"]!.AsObject();

        Assert.Equal("Clone", (string?)json["ampMode"]);
        Assert.True(modules.ContainsKey("Clone"));
        Assert.False(modules.ContainsKey("AMP"));
        Assert.Equal(Data.Effects.ById(901)?.Name, (string?)modules["Clone"]!["effect"]);
    }

    [Fact]
    public void FromJsonMapsNamesToIds()
    {
        var imp = PresetJson.FromJson(
            """
            {"version":"1.0","ampMode":"Normal","presetVolume":75,
             "modules":{"NR":{"enabled":true,"effect":"Gate","parameters":{"THRE":50}}},
             "signalChain":["NR"]}
            """,
            Data);

        Assert.False(imp.IsCloneMode);
        Assert.Equal(75, imp.PresetVolume);
        Assert.True(imp.ModuleStates[0]);
        Assert.Equal(1, imp.SelectedEffects[0]);
        Assert.Equal(50, imp.Parameters[0][0]);
    }

    [Fact]
    public void VersionMismatchThrows()
    {
        Assert.Throws<FormatException>(() => PresetJson.FromJson("""{"version":"2.0"}""", Data));
    }

    [Fact]
    public void UnknownEffectThrows()
    {
        Assert.Throws<FormatException>(() => PresetJson.FromJson(
            """{"version":"1.0","modules":{"NR":{"enabled":true,"effect":"Nope","parameters":{}}},"signalChain":[]}""",
            Data));
    }

    [Fact]
    public void UnknownModuleWarnsAndSkips()
    {
        var imp = PresetJson.FromJson(
            """{"version":"1.0","modules":{"BOGUS":{"enabled":true,"effect":null,"parameters":{}}},"signalChain":[]}""",
            Data);

        Assert.Contains(imp.Warnings, w => w.Contains("BOGUS"));
        Assert.Empty(imp.ModuleStates);
    }

    [Fact]
    public void OutOfRangeParamClampsWithWarning()
    {
        var imp = PresetJson.FromJson(
            """{"version":"1.0","modules":{"NR":{"enabled":true,"effect":"Gate","parameters":{"THRE":9999}}},"signalChain":[]}""",
            Data);

        Assert.Equal(100, imp.Parameters[0][0]);
        Assert.Contains(imp.Warnings, w => w.Contains("clamped"));
    }

    [Fact]
    public void PresetVolumeClamps()
    {
        var imp = PresetJson.FromJson("""{"version":"1.0","presetVolume":200,"modules":{},"signalChain":[]}""", Data);

        Assert.Equal(100, imp.PresetVolume);
    }

    [Fact]
    public void PresetBpmClampsAndDefaults()
    {
        Assert.Equal(260, PresetJson.FromJson("""{"version":"1.0","presetBpm":999,"modules":{},"signalChain":[]}""", Data).PresetBpm);
        Assert.Equal(120, PresetJson.FromJson("""{"version":"1.0","modules":{},"signalChain":[]}""", Data).PresetBpm);
    }

    [Fact]
    public void RoundTripsModuleThroughExportImport()
    {
        var state = new DecodedPresetState(false, 60, 90, new Dictionary<string, bool> { ["NR"] = true }, ["NR"]);

        var json = PresetJson.ToJson(state, new Dictionary<int, int> { [0] = 1 }, Params(0, 0, 42), Data, null);
        var imp = PresetJson.FromJson(json, Data);

        Assert.Equal(1, imp.SelectedEffects[0]);
        Assert.Equal(42, imp.Parameters[0][0]);
        Assert.Equal(60, imp.PresetVolume);
        Assert.Equal(90, imp.PresetBpm);
    }
}
