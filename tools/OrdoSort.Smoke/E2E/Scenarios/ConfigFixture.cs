using System.Text.Json;
using OrdoSort.Core;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>A real config.json inside the fixture, loaded through the app's
/// own Config.Load so scenarios exercise the same parsing production does.
///
/// Key names checked against src/OrdoSort.Core/Config.cs's [JsonPropertyName]
/// attributes one at a time (inbox/deferred/history_db/naming_mode/sort/
/// uppercase_names/routes/label/path/hotkey), not assumed from the brief that
/// proposed this file: a mistyped key here would silently fall back to
/// Config's own default (Config.Normalize() null-coalesces every field) and
/// every later task that reuses this fixture would be debugging a phantom
/// instead of a mistyped string. Also cross-checked against the inline config
/// tools/OrdoSort.Smoke/Program.cs:48-61 already writes for the existing
/// (non-E2E) smoke harness — same seven keys, same shape.</summary>
internal static class ConfigFixture
{
    /// <summary>Writes config.json at the fixture root and returns
    /// (loaded config, its path). Inbox, deferred and one destination route
    /// all live under the fixture.</summary>
    public static (Config Cfg, string Path) Write(Fixture fx)
    {
        var inbox = fx.Dir("inbox");
        var deferred = fx.Dir("deferred");
        var dest = fx.Dir("filed");
        var path = Path.Combine(fx.Root, "config.json");

        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            inbox = inbox.Replace('\\', '/'),
            deferred = deferred.Replace('\\', '/'),
            history_db = "history.sqlite",
            naming_mode = "insert",
            sort = "filename_asc",
            uppercase_names = true,
            routes = new[]
            {
                new { label = "Invoices", path = dest.Replace('\\', '/'), hotkey = "Ctrl+1" },
            },
        }));

        return (Config.Load(path), path);
    }
}
