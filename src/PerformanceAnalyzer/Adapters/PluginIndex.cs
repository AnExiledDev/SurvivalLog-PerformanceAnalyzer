using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Unity.IL2CPP;
using PerformanceAnalyzer.Core;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// Which mod a piece of code belongs to: the BepInEx plugin whose DLL holds it. A DLL that is not a plugin (a shared
/// library) becomes a mod of its own, named after the file.
/// </summary>
internal sealed class PluginIndex
{
    private readonly Recorder _recorder;
    private readonly Dictionary<Assembly, int> _byAssembly = new();
    private readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Assembly> _assemblyOf = new();
    private readonly List<ModInfo> _loaded = new();

    public PluginIndex(Recorder recorder)
    {
        _recorder = recorder;
        _byAssembly[typeof(PluginIndex).Assembly] = Recorder.AnalyzerMod;
        _byKey[recorder.Mods[Recorder.AnalyzerMod].Key] = Recorder.AnalyzerMod;
        _assemblyOf[Recorder.AnalyzerMod] = typeof(PluginIndex).Assembly;
    }

    /// <summary>Every plugin BepInEx loaded, for the report's last section.</summary>
    public IReadOnlyList<ModInfo> Loaded => _loaded;

    /// <summary>Registers every loaded plugin. Run once the chainloader is done, so every plugin is in its list.</summary>
    public void ReadPlugins()
    {
        foreach (var plugin in IL2CPPChainloader.Instance.Plugins.Values)
        {
            var metadata = plugin.Metadata;
            var info = new ModInfo(ModKeys.For(metadata.GUID, metadata.Name), metadata.Name, metadata.Version?.ToString() ?? "", metadata.GUID);
            var assembly = plugin.Instance?.GetType().Assembly;

            _loaded.Add(info);

            if (assembly == null || _byAssembly.ContainsKey(assembly)) continue;

            Register(assembly, info);
        }

        _loaded.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Plugin assemblies other than this mod's own, for finding their Update methods and for a deep profile.</summary>
    public IEnumerable<(int Mod, Assembly Assembly)> PluginAssemblies() =>
        _assemblyOf.Where(pair => pair.Key != Recorder.AnalyzerMod).Select(pair => (pair.Key, pair.Value)).ToList();

    public Assembly? AssemblyOf(string key) => _byKey.TryGetValue(key, out var mod) && _assemblyOf.TryGetValue(mod, out var assembly) ? assembly : null;

    /// <summary>The mod that owns code in <paramref name="assembly"/>.</summary>
    public int ModOf(Assembly assembly)
    {
        if (_byAssembly.TryGetValue(assembly, out var mod)) return mod;

        var name = assembly.GetName().Name ?? "unknown";

        return Register(assembly, new ModInfo(ModKeys.For("", name), name, assembly.GetName().Version?.ToString() ?? "", ""));
    }

    public bool IsOurs(Assembly assembly) => assembly == typeof(PluginIndex).Assembly;

    /// <summary>The loader's own assemblies and the game's: their patches are not a mod's.</summary>
    public static bool IsFramework(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? "";

        return name is "0Harmony" or "HarmonyXInterop" or "MonoMod.RuntimeDetour" or "MonoMod.Utils"
            || name.StartsWith("BepInEx", StringComparison.Ordinal)
            || name.StartsWith("Il2CppInterop", StringComparison.Ordinal)
            || name.StartsWith("MonoMod", StringComparison.Ordinal);
    }

    /// <summary>Whether the type is one of the game's, seen through the interop DLLs.</summary>
    public static bool IsGameType(Type? type)
    {
        if (type == null) return false;

        var location = SafeLocation(type.Assembly);

        return location.Length > 0 && Path.GetFileName(Path.GetDirectoryName(location) ?? "").Equals("interop", StringComparison.OrdinalIgnoreCase);
    }

    private int Register(Assembly assembly, ModInfo info)
    {
        var key = info.Key;

        for (var n = 2; _byKey.ContainsKey(key); n++) key = info.Key + n;

        var mod = _recorder.AddMod(info with { Key = key });

        _byAssembly[assembly] = mod;
        _byKey[key] = mod;
        _assemblyOf[mod] = assembly;

        return mod;
    }

    private static string SafeLocation(Assembly assembly)
    {
        try
        {
            return assembly.IsDynamic ? "" : assembly.Location;
        }
        catch (NotSupportedException)
        {
            return "";
        }
    }
}
