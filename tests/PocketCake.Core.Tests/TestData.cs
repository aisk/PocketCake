using PocketCake.Core.Model;

namespace PocketCake.Core.Tests;

/// <summary>Shared, lazily loaded device data tables and fixture paths.</summary>
public static class TestData
{
    private static readonly Lazy<DataAssets> Lazy = new(DataAssets.Load);

    public static DataAssets Assets => Lazy.Value;

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
