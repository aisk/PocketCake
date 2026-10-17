using PocketCake.Core.Model;

namespace PocketCake.Core.Tests.Model;

public sealed class ChainOrderTests
{
    private static readonly string[] U01 = ["NR", "FX1", "DRV", "AMP", "IR", "EQ", "FX2", "DLY", "RVB"];

    [Fact]
    public void CollapseGroupsAmpBlockIntoSixGroups()
    {
        var groups = ChainOrder.Collapse(U01);

        Assert.Equal(6, groups.Count);
        Assert.Single(groups, g => ChainOrder.IsAmpBlock(g));
        Assert.Equal(ChainOrder.AmpBlock, groups.First(g => ChainOrder.IsAmpBlock(g)));
        Assert.Equal(5, groups.Count(g => g.Count == 1));
    }

    [Fact]
    public void FlattenIsInverseOfCollapse()
    {
        Assert.Equal(U01, ChainOrder.Flatten(ChainOrder.Collapse(U01)));
    }

    [Fact]
    public void ReorderingKeepsAmpBlockContiguous()
    {
        var groups = ChainOrder.Collapse(U01);
        List<List<string>> rotated = [.. groups.Skip(1), groups[0]];
        var flat = ChainOrder.Flatten(rotated);
        var i = flat.IndexOf("DRV");

        Assert.Equal(ChainOrder.AmpBlock, flat.GetRange(i, 4));
        Assert.Equal(string.Join("-", flat), ChainOrder.Key(rotated));
    }

    [Fact]
    public void SplitOrMisorderedBlockStaysSingletons()
    {
        string[] weird = ["AMP", "DRV", "IR", "EQ", "NR", "FX1", "FX2", "DLY", "RVB"];
        var groups = ChainOrder.Collapse(weird);

        Assert.DoesNotContain(groups, g => ChainOrder.IsAmpBlock(g));
        Assert.Equal(weird, ChainOrder.Flatten(groups));
    }
}

/// <summary>Per-effect completeness guard over the shipped data tables.</summary>
public sealed class DataCompletenessTests
{
    private static DataAssets Data => TestData.Assets;

    // The exact values a param's control can produce: toggle 0/1, select option indices,
    // knob/eqBand min..max by step (snapped to the slider precision).
    private static List<double> ProducibleValues(EffectParameter p)
    {
        switch (p.WidgetType)
        {
            case WidgetType.Toggle:
                return [0, 1];
            case WidgetType.Select:
                return Enumerable.Range(0, p.Options?.Count ?? 0).Select(i => (double)i).ToList();
            default:
                if (p.Min is not { } min || p.Max is not { } max)
                {
                    return [];
                }

                var step = (p.Step ?? 1) == 0 ? 1.0 : p.Step ?? 1;
                var divisions = (int)Math.Round((max - min) / step, MidpointRounding.AwayFromZero);

                return Enumerable.Range(0, divisions + 1)
                    .Select(i => step == Math.Round(step)
                        ? Math.Round(min + i * step, MidpointRounding.AwayFromZero)
                        : Math.Round(min + i * step, 1, MidpointRounding.AwayFromZero))
                    .ToList();
        }
    }

    // All (moduleId, effectId) pairs the UI can reach.
    private static IEnumerable<(int Module, int Effect)> ModuleEffects() =>
        Enumerable.Range(0, 10).SelectMany(m => Data.Commands.EffectIdsFor(m).Select(id => (m, id)));

    [Fact]
    public void EveryModuleEffectExistsInLibrary()
    {
        foreach (var (m, id) in ModuleEffects())
        {
            Assert.True(Data.Effects.ById(id) is not null, $"module {m} effect {id} is in effectTypes but missing from the effect library");
        }
    }

    [Fact]
    public void KnobsHaveRangeAndSelectsHaveOptions()
    {
        foreach (var (_, id) in ModuleEffects())
        {
            foreach (var p in Data.Effects.ById(id)!.Params)
            {
                switch (p.WidgetType)
                {
                    case WidgetType.Knob:
                    case WidgetType.EqBand:
                        Assert.True(p.Min is not null, $"effect {id} / {p.Name}: no min");
                        Assert.True(p.Max is not null, $"effect {id} / {p.Name}: no max");
                        break;
                    case WidgetType.Select:
                        Assert.True(p.Options is { Count: > 0 }, $"effect {id} / {p.Name}: select with no options");
                        break;
                }
            }
        }
    }

    [Fact]
    public void ParameterCommandTableCoversEveryProducibleValue()
    {
        var gaps = new List<string>();

        foreach (var (m, id) in ModuleEffects())
        {
            foreach (var p in Data.Effects.ById(id)!.Params)
            {
                foreach (var v in ProducibleValues(p))
                {
                    if (Data.Commands.ParameterCommand(m, p.AlgId, SliderMath.ParamValueKey(v)) is null)
                    {
                        gaps.Add($"module {m} effect {id} {p.Name} algId {p.AlgId} value {v}");
                    }
                }
            }
        }

        Assert.True(gaps.Count == 0, $"missing parameter commands ({gaps.Count}):\n{string.Join("\n", gaps.Take(20))}");
    }
}
