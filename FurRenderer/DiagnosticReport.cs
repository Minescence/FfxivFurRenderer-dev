using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sheets = Lumina.Excel.Sheets;

namespace FurRenderer;

/// <summary>
/// The "Export all settings" text: the settings and what the plugin and the game were doing, for a user to paste into a problem
/// report. Each line catches its own failure (a game patch can break any of these reads), so the rest still exports.
/// </summary>
internal static unsafe class DiagnosticReport
{
    // Options that change what the fur reads from the game's frame: resolution and upscaling move the depth and GBuffer textures,
    // AA, SSAO, shadows and glare shade the coat whose light the fur takes. All UInt options (TryGet doesn't check the type).
    private static readonly string[] GraphicsOptions =
    [
        "ScreenMode", "Fps", "Gamma", "GraphicsRezoUpscaleType", "GraphicsRezoScale", "DynamicRezoType", "DynamicRezoThreshold",
        "AntiAliasing_DX11", "SSAO_DX11", "Glare_DX11", "DepthOfField_DX11", "TranslucentQuality_DX11", "ShadowSoftShadowType_DX11",
        "ShadowTextureSizeType_DX11", "ShadowCascadeCountType_DX11", "LodType_DX11", "PhysicsTypeSelf_DX11", "ReflectionType_DX11",
    ];

    // Loaded from anywhere, these hook the game's rendering (post-processing, upscaler swaps, overlays).
    private static readonly string[] RenderHooks = ["reshade", "gshade", "optiscaler", "specialk", "rtss", "overlay", "discordhook", "dxvk", "nvngx", "xess", "fidelityfx"];

    public static string Build(Configuration config, Configuration defaults, string status, string? lastFrame,
        IEnumerable<KeyValuePair<string, string>> seen, IDalamudPluginInterface pluginInterface, IDataManager data, IGameConfig gameConfig)
    {
        var text = new StringBuilder();
        void Line(string label, Func<string> value)
        {
            string shown;
            try { shown = value(); }
            catch (Exception ex) { shown = "unavailable: " + ex.Message; } // one broken read mustn't lose the rest of the report
            text.Append(label).Append(": ").AppendLine(shown);
        }

        text.AppendLine("Fur Renderer diagnostics. Paste all of this when you report a problem.");
        Line("Exported", () => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        Line("Plugin", () => $"{typeof(DiagnosticReport).Assembly.GetName().Version}{(pluginInterface.IsDev ? " (dev build)" : "")}");
        Line("Dalamud", () => typeof(IDalamudPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(IDalamudPlugin).Assembly.GetName().Version?.ToString() ?? "unknown");
        Line("Game", () => Framework.Instance() is var fw && fw != null ? fw->GameVersionString : "unknown");
        Line("Status", () => status);
        Line("Last frame", () => string.IsNullOrEmpty(lastFrame) ? "no fur drawn yet" : lastFrame);

        text.AppendLine().AppendLine("[Scene]");
        Line("Eorzea time", () =>
        {
            var fw = Framework.Instance();
            if (fw == null) return "unknown";
            var own = Clock(fw->ClientTime.EorzeaTime);
            // GPose, Weatherman or another plugin can hold it.
            return fw->ClientTime.IsEorzeaTimeOverridden ? $"{Clock(fw->ClientTime.EorzeaTimeOverride)}, held (the clock says {own})" : own;
        });
        Line("Weather", () =>
        {
            var weather = WeatherManager.Instance();
            if (weather == null) return "unknown";
            var id = weather->GetCurrentWeather();
            return $"{data.GetExcelSheet<Sheets.Weather>().GetRowOrDefault(id)?.Name.ToString()} ({id})";
        });
        Line("Zone", () =>
        {
            var game = GameMain.Instance();
            if (game == null) return "unknown";
            var id = game->CurrentTerritoryTypeId;
            return $"{data.GetExcelSheet<Sheets.TerritoryType>().GetRowOrDefault(id)?.PlaceName.ValueNullable?.Name.ToString()} ({id})";
        });
        Line("GPose", () => GameMain.IsInGPose() ? "yes" : "no");
        Line("Key light", () => GameInterop.KeyLightDirection() is { } towards
            ? $"the game's sun or moon, towards {towards.ToString("F2", CultureInfo.InvariantCulture)}"
            : "the game's sun or moon can't be read: using the built-in direction");

        text.AppendLine().AppendLine("[Game and PC]");
        Line("Graphics options", () => string.Join(", ", GraphicsOptions.Select(o => gameConfig.System.TryGet(o, out uint v) ? $"{o} {v}" : $"{o} ?")));
        Line("Render hooks and proxy DLLs", () =>
        {
            var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            using var process = Process.GetCurrentProcess();
            var found = process.Modules.Cast<ProcessModule>()
                .Where(m => !string.Equals(m.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(Path.GetDirectoryName(m.FileName), gameDirectory, StringComparison.OrdinalIgnoreCase)
                        || RenderHooks.Any(h => m.ModuleName.Contains(h, StringComparison.OrdinalIgnoreCase))))
                .Select(m => $"{m.FileVersionInfo.ProductName} {m.FileVersionInfo.ProductVersion}".Trim() is { Length: > 0 } version ? $"{m.ModuleName} ({version})" : m.ModuleName)
                .ToList();
            return found.Count > 0 ? string.Join(", ", found) : "none";
        });
        Line("Loaded plugins", () => string.Join(", ", pluginInterface.InstalledPlugins.Where(p => p.IsLoaded).OrderBy(p => p.Name).Select(p => $"{p.Name} {p.Version}")));

        text.AppendLine().AppendLine("[What the plugin sees]");
        foreach (var (key, value) in seen) text.Append(key).Append(": ").AppendLine(value);

        var settings = JObject.FromObject(config);
        var initial = JObject.FromObject(defaults);
        var changed = settings.Properties().Where(p => !JToken.DeepEquals(p.Value, initial[p.Name])).ToList();
        text.AppendLine().AppendLine("[Settings changed from the defaults]");
        foreach (var p in changed) text.AppendLine($"{p.Name}: {p.Value.ToString(Formatting.None)} (default {initial[p.Name]?.ToString(Formatting.None)})");
        if (changed.Count == 0) text.AppendLine("none");
        text.AppendLine().AppendLine("[All settings]").AppendLine(settings.ToString(Formatting.Indented));

        // Paths (mod files, the game folder) can hold the Windows user name.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 3
            ? text.ToString().Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase).Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            : text.ToString();
    }

    private static string Clock(long eorzeaSeconds)
    {
        var day = eorzeaSeconds % 86400;
        return $"{day / 3600:D2}:{day / 60 % 60:D2}";
    }
}
