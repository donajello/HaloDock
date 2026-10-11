using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloDock;

/// <summary>Screen edges the wheel can open from. The bottom is left to the taskbar.</summary>
public enum ScreenEdge
{
    Left,
    Right,
    Top,
}

public enum MonitorMode
{
    All,
    Primary,
    Selected,
}

public sealed class LaunchItem
{
    public string Path { get; set; } = "";

    /// <summary>Custom label; falls back to the file/folder name when empty.</summary>
    public string? Name { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? DefaultName(Path) : Name!;

    [JsonIgnore]
    public bool Exists => File.Exists(Path) || Directory.Exists(Path);

    public static string DefaultName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0 || trimmed.EndsWith(':')) return path; // drive root like "C:\"
        var ext = System.IO.Path.GetExtension(trimmed);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.GetFileNameWithoutExtension(trimmed);
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}

/// <summary>Symbol on the drop target that takes a held icon off the ring.</summary>
public enum RemoveSymbol
{
    Unpin,
    Minus,
    Hide,
    Unstar,
    Cross,
}

public static class RemoveSymbols
{
    /// <summary>Icon-font family that ships with Windows (Fluent on 11, MDL2 on 10).</summary>
    public const string Font = "Segoe Fluent Icons, Segoe MDL2 Assets";

    public static string Glyph(RemoveSymbol symbol) => symbol switch
    {
        RemoveSymbol.Minus => "",
        RemoveSymbol.Hide => "",
        RemoveSymbol.Unstar => "",
        RemoveSymbol.Cross => "",
        _ => "", // unpin
    };

    /// <summary>The matching "add" symbol, shown where dragged-in files will be added.</summary>
    public static string AddGlyph(RemoveSymbol symbol) => symbol switch
    {
        RemoveSymbol.Unpin => "", // pin
        RemoveSymbol.Hide => "",  // eye
        RemoveSymbol.Unstar => "", // star
        _ => "",                  // plus
    };

    public static string RemoveText(RemoveSymbol symbol) =>
        symbol == RemoveSymbol.Unpin ? "Unpin from ring" : "Remove from ring";

    public static string AddText(RemoveSymbol symbol) =>
        symbol == RemoveSymbol.Unpin ? "Pin to ring" : "Add to ring";

    public static string Label(RemoveSymbol symbol) => symbol switch
    {
        RemoveSymbol.Minus => "Minus / plus",
        RemoveSymbol.Hide => "Eye with a slash / eye",
        RemoveSymbol.Unstar => "Star with a slash / star",
        RemoveSymbol.Cross => "Cross / plus",
        _ => "Unpin / pin",
    };
}

/// <summary>A saved set of items, shown as one coloured dot on the menu's inner ring.</summary>
public sealed class RingPreset
{
    public static readonly string[] Palette =
        ["#FFCE5A", "#4C9AFF", "#4CD964", "#FF5C5C", "#A66CFF", "#FF9F43", "#2ED1C4", "#FF6FB5"];

    public string Name { get; set; } = "Ring";
    public string Color { get; set; } = Palette[0];
    public List<LaunchItem> Items { get; set; } = new();
}

public sealed class AppSettings
{
    public List<RingPreset> Rings { get; set; } = new();

    /// <summary>Index into <see cref="Rings"/> of the ring the menu shows.</summary>
    public int ActiveRing { get; set; }

    /// <summary>Pre-rings settings files kept a single item list here; migrated into the first ring on load.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LaunchItem>? Items { get; set; }

    [JsonIgnore]
    public RingPreset CurrentRing => Rings[Math.Clamp(ActiveRing, 0, Rings.Count - 1)];

    /// <summary>Edges that open the wheel when the pointer rests against them.</summary>
    public List<ScreenEdge> Edges { get; set; } = [ScreenEdge.Left];

    public MonitorMode MonitorMode { get; set; } = MonitorMode.All;

    /// <summary>Device names (e.g. \\.\DISPLAY2) used when MonitorMode is Selected.</summary>
    public List<string> SelectedMonitors { get; set; } = new();

    /// <summary>Ignore a left edge that borders another monitor, so crossing between screens never triggers.</summary>
    public bool OuterEdgesOnly { get; set; } = true;

    public int HoverDelayMs { get; set; } = 150;
    public int EdgeWidthPx { get; set; } = 2;
    public double Radius { get; set; } = 190;
    public double ItemSize { get; set; } = 72;
    public bool InvertScroll { get; set; }
    public RemoveSymbol RemoveSymbol { get; set; } = RemoveSymbol.Unpin;
    public bool IgnoreFullscreen { get; set; } = true;
    public bool StartWithWindows { get; set; }

    /// <summary>Opt-in: look for a newer release on GitHub once a day.</summary>
    public bool CheckForUpdates { get; set; }
    public DateTime? LastUpdateCheck { get; set; }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HaloDock", "settings.json");

    /// <summary>Where settings lived before the app was renamed from EdgeDock.</summary>
    static string OldFilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EdgeDock", "settings.json");

    public static AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            if (!File.Exists(FilePath) && File.Exists(OldFilePath))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                File.Copy(OldFilePath, FilePath);
            }
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch
        {
            // Corrupt settings: start fresh rather than refusing to launch.
        }
        return (settings ?? new()).Normalized();
    }

    /// <summary>Guarantees at least one ring, migrating an old single item list into it.</summary>
    public AppSettings Normalized()
    {
        if (Rings.Count == 0) Rings.Add(new RingPreset { Name = "Main", Items = Items ?? new() });
        else if (Items is { Count: > 0 }) Rings[0].Items.AddRange(Items);
        Items = null;
        ActiveRing = Math.Clamp(ActiveRing, 0, Rings.Count - 1);
        Edges = (Edges ?? [ScreenEdge.Left]).Distinct().ToList();
        return this;
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
}
