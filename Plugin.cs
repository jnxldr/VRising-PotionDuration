using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.UI;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace PotionDuration;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> DumpBuffs;
    internal static ConfigEntry<int> PotionMinutes;
    internal static ConfigEntry<int> ElixirMinutes;
    internal static ConfigEntry<string> Include;
    internal static ConfigEntry<string> Exclude;
    internal static bool IsServer;

    Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        Enabled = Config.Bind("General", "Enabled", true, "Set to false to turn the mod off (everything stays as in the base game).");
        var minutes = new AcceptableValueRange<int>(1, 1440);
        const string examples = "Examples: 30 = 30 min (50% shorter than the base game)\n" +
            "          120 = 2 h (100% longer; the default)\n" +
            "          240 = 4 h (300% longer, 4x the base game)\n" +
            "Any value from 1 to 1440 (24 h); a value outside that range is set to the nearest limit.\n" +
            "Players: use the same minutes as the server, or the tooltips show the wrong time.";
        PotionMinutes = Config.Bind("Duration", "PotionMinutes", 120,
            new ConfigDescription("Duration in minutes for potions and brews (base game 60).\n" + examples, minutes));
        ElixirMinutes = Config.Bind("Duration", "ElixirMinutes", 120,
            new ConfigDescription("Duration in minutes for elixirs (base game 60).\n" + examples, minutes));
        Include = Config.Bind("Selection", "Include", "",
            "Comma-separated buff names to treat as potions too (or as elixirs, if the name contains 'Elixir'). The buff must last 60 minutes in the base game.");
        Exclude = Config.Bind("Selection", "Exclude", "", "Comma-separated buff names to leave unchanged.");
        DumpBuffs = Config.Bind("Discovery", "DumpBuffs", false,
            "Write every timed buff (name, ID, duration) to BepInEx/config/PotionDuration/buffs.csv at start, to find buff names.");

        // Server: change the buffs themselves. Client: change its own copy of the prefabs, which the item tooltips read.
        var isServer = IsServer = Paths.ProcessName.Equals("VRisingServer", StringComparison.OrdinalIgnoreCase);
        if (isServer) _harmony = Harmony.CreateAndPatchAll(typeof(ServerReadyPatch), MyPluginInfo.PLUGIN_GUID);
        else AddComponent<ClientWorldWatcher>();
        Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} loaded on {(isServer ? "server" : "client")} " +
            $"(Enabled={Enabled.Value}, PotionMinutes={PotionMinutes.Value}, ElixirMinutes={ElixirMinutes.Value}, DumpBuffs={DumpBuffs.Value})");
    }

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        return true;
    }

    // Called once per world, after it has loaded its prefabs.
    internal static void OnGameDataReady(World world)
    {
        if (!Enabled.Value) return;
        try
        {
            if (DumpBuffs.Value) BuffDump.Write(world);
            var changed = DurationPatcher.Apply(world);
            if (!IsServer) TooltipPatcher.Apply(world, changed);
        }
        catch (Exception e)
        {
            Logger.LogError($"Game-data-ready handler failed in world {world.Name}: {e}");
        }
    }
}

[HarmonyPatch(typeof(SpawnTeamSystem_OnPersistenceLoad), nameof(SpawnTeamSystem_OnPersistenceLoad.OnUpdate))]
static class ServerReadyPatch
{
    static bool _done;

    static void Postfix(SpawnTeamSystem_OnPersistenceLoad __instance)
    {
        if (_done) return;
        var world = __instance.World;
        if (world == null || world.Name != "Server") return;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        if (prefabs == null || !prefabs.GameDataInitialized) return;
        _done = true;
        Plugin.OnGameDataReady(world);
    }
}

// The client creates a new world each time it joins a server, so check every world about once a second
// and apply once per world when its game data is ready. (A postfix on PrefabCollectionSystem.OnUpdate never fired on the client.)
public class ClientWorldWatcher : MonoBehaviour
{
    readonly HashSet<ulong> _seen = new();
    readonly HashSet<ulong> _done = new();
    float _next;

    public ClientWorldWatcher(IntPtr ptr) : base(ptr) { }

    void Update()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 1f;
        var worlds = World.s_AllWorlds;
        for (int i = 0; i < worlds.Count; i++)
        {
            var world = worlds[i];
            if (world == null || !world.IsCreated || _done.Contains(world.SequenceNumber)) continue;
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            if (prefabs == null) continue;
            if (_seen.Add(world.SequenceNumber))
                Plugin.Logger.LogInfo($"World {world.Name} ({world.SequenceNumber}) has prefabs; waiting for game data");
            if (!prefabs.GameDataInitialized) continue;
            _done.Add(world.SequenceNumber);
            Plugin.OnGameDataReady(world);
        }
    }
}

static class DurationPatcher
{
    const float VanillaSeconds = 3600f;
    static readonly Regex PotionName = new(@"^AB_Consumable_\w*Potion_T\d+_Buff$");
    static readonly Regex ElixirName = new(@"^AB_Elixir_\w+_Buff$");

    // Potion/elixir buff prefabs by name (reviewed against docs/buffs-1.1.15.csv for game 1.1.15: 13 potions, 8 elixirs).
    // Returns null when the name is out of scope.
    static string Classify(string name, HashSet<string> include, HashSet<string> exclude)
    {
        if (exclude.Contains(name)) return null;
        if (ElixirName.IsMatch(name)) return "elixir";
        if (PotionName.IsMatch(name)) return "potion";
        if (include.Contains(name)) return name.Contains("Elixir", StringComparison.OrdinalIgnoreCase) ? "elixir" : "potion";
        return null;
    }

    static HashSet<string> NameList(string value) =>
        new(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);

    // Returns the changed buff prefab names with their kind ("potion" or "elixir").
    public static Dictionary<string, string> Apply(World world)
    {
        var em = world.EntityManager;
        var pcs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        var lookup = pcs._PrefabLookupMap;
        var map = pcs._PrefabGuidToEntityMap;
        var changedBuffs = new Dictionary<string, string>();
        var buffType = new ComponentType(Il2CppType.Of<Buff>(), ComponentType.AccessMode.ReadOnly);
        var lifeTimeType = new ComponentType(Il2CppType.Of<LifeTime>(), ComponentType.AccessMode.ReadWrite);
        var include = NameList(Plugin.Include.Value);
        var exclude = NameList(Plugin.Exclude.Value);
        var seconds = new Dictionary<string, float>
        {
            ["potion"] = Plugin.PotionMinutes.Value * 60f,
            ["elixir"] = Plugin.ElixirMinutes.Value * 60f,
        };

        int changed = 0, skipped = 0, unconverted = 0;
        var foundIncludes = new HashSet<string>();
        var keys = pcs._PrefabDataLookup.GetKeyArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < keys.Length; i++)
            {
                var guid = keys[i];
                if (!lookup.TryGetName(guid, out var name) || name == null) continue;
                var kind = Classify(name, include, exclude);
                if (kind == null) continue;
                if (include.Contains(name)) foundIncludes.Add(name);

                // Prefabs the game only converts on demand have no entity yet, so they can't be changed here.
                if (!map.TryGetValue(guid, out var entity))
                {
                    unconverted++;
                    Plugin.Logger.LogWarning($"Not converted at start, left vanilla: {name} ({guid.GuidHash})");
                    continue;
                }
                if (!em.HasComponent(entity, buffType) || !em.HasComponent(entity, lifeTimeType))
                {
                    skipped++;
                    Plugin.Logger.LogWarning($"Skipped {kind} {name} ({guid.GuidHash}): no Buff/LifeTime component");
                    continue;
                }
                var life = em.GetComponentData<LifeTime>(entity);
                if (life.Duration != VanillaSeconds)
                {
                    skipped++;
                    Plugin.Logger.LogWarning($"Skipped {kind} {name} ({guid.GuidHash}): vanilla duration {life.Duration}s, not {VanillaSeconds}s");
                    continue;
                }
                var old = life.Duration;
                life.Duration = seconds[kind];
                em.SetComponentData(entity, life);
                changed++;
                changedBuffs[name] = kind;
                Plugin.Logger.LogInfo($"Changed {kind} {name} ({guid.GuidHash}): {old}s -> {life.Duration}s");
            }
        }
        finally
        {
            keys.Dispose();
        }

        foreach (var name in include.Where(n => !foundIncludes.Contains(n)))
            Plugin.Logger.LogWarning($"Include entry not found or excluded: {name}");
        var summary = $"[{world.Name}] {changed} buff prefabs changed (potions {Plugin.PotionMinutes.Value} min, elixirs {Plugin.ElixirMinutes.Value} min), " +
            $"{skipped} skipped, {unconverted} not converted at start";
        if (changed == 0) Plugin.Logger.LogWarning(summary + ". Nothing matched: a game patch may have renamed the buffs; re-run DumpBuffs.");
        else Plugin.Logger.LogInfo(summary);
        return changedBuffs;
    }
}

// Client only. Tooltip numbers are baked text, not read from LifeTime: each description's values are
// LocalizedStringBuilderParameter entries (Key, Value as a string, e.g. duration = "60") in a buffer on the prefab.
static class TooltipPatcher
{
    const string VanillaMinutes = "60";

    // Ignores underscores, case and a possessive "s" before "potion"
    // (the buff is AB_Consumable_WranglerPotion_T01_Buff, its item Item_Consumable_WranglersPotion_T01).
    static string Normalize(string s) => s.Replace("_", "").ToLowerInvariant().Replace("spotion", "potion");

    public static void Apply(World world, Dictionary<string, string> changedBuffs)
    {
        var em = world.EntityManager;
        var pcs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        var lookup = pcs._PrefabLookupMap;
        var paramType = new ComponentType(Il2CppType.Of<LocalizedStringBuilderParameter>(), ComponentType.AccessMode.ReadWrite);
        // AB_Consumable_SunResistancePotion_T01_Buff -> sunresistancepotiont01, AB_Elixir_Bat_T01_Buff -> elixirbatt01;
        // the item, buff and recipe prefabs of one potion share that stem.
        var stems = changedBuffs.ToDictionary(kv => Normalize(Regex.Replace(kv.Key, @"^AB_(Consumable_)?|_Buff$", "")), kv => kv.Value);
        var minutes = new Dictionary<string, string>
        {
            ["potion"] = Plugin.PotionMinutes.Value.ToString(),
            ["elixir"] = Plugin.ElixirMinutes.Value.ToString(),
        };

        int withParams = 0, changed = 0, other = 0;
        var kvs = pcs._PrefabGuidToEntityMap.GetKeyValueArrays(Allocator.Temp);
        try
        {
            for (int i = 0; i < kvs.Length; i++)
            {
                var entity = kvs.Values[i];
                if (!em.HasComponent(entity, paramType)) continue;
                withParams++;
                var buffer = em.GetBuffer<LocalizedStringBuilderParameter>(entity);
                string name = null, stem = null;
                for (int j = 0; j < buffer.Length; j++)
                {
                    var p = buffer[j];
                    var key = p.Key.ToString();
                    if (!key.Contains("duration", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name == null)
                    {
                        name = lookup.GetName(kvs.Keys[i]) ?? "";
                        var n = Normalize(name);
                        stem = stems.Keys.FirstOrDefault(s => n.Contains(s));
                    }
                    var value = p.Value.ToString();
                    if (stem == null)
                    {
                        if (value != VanillaMinutes) continue;
                        other++;
                        Plugin.Logger.LogInfo($"Tooltip {name} {key} = {value}: no changed buff matches this name; left vanilla");
                        continue;
                    }
                    if (value != VanillaMinutes)
                    {
                        Plugin.Logger.LogWarning($"Tooltip {name} {key} = \"{value}\", not {VanillaMinutes}; left as is");
                        continue;
                    }
                    p.Value = new FixedString128Bytes(minutes[stems[stem]]);
                    buffer[j] = p;
                    changed++;
                    Plugin.Logger.LogInfo($"Tooltip {name} {key}: {value} -> {p.Value}");
                }
            }
        }
        finally
        {
            kvs.Dispose();
        }

        var summary = $"[{world.Name}] tooltips: {changed} duration values changed, {other} other 60-minute values left vanilla, " +
            $"{withParams} prefabs with description parameters";
        if (changed == 0) Plugin.Logger.LogWarning(summary + ". No potion/elixir tooltip changed.");
        else Plugin.Logger.LogInfo(summary);
    }
}

static class BuffDump
{
    public static void Write(World world)
    {
        var em = world.EntityManager;
        var pcs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        var lookup = pcs._PrefabLookupMap;
        var buffType = new ComponentType(Il2CppType.Of<Buff>(), ComponentType.AccessMode.ReadOnly);
        var lifeTimeType = new ComponentType(Il2CppType.Of<LifeTime>(), ComponentType.AccessMode.ReadOnly);

        var rows = new List<string>();
        var map = pcs._PrefabGuidToEntityMap;
        var kvs = map.GetKeyValueArrays(Allocator.Temp);
        try
        {
            for (int i = 0; i < kvs.Length; i++)
            {
                var guid = kvs.Keys[i];
                var entity = kvs.Values[i];
                if (!em.HasComponent(entity, buffType) || !em.HasComponent(entity, lifeTimeType)) continue;
                var life = em.GetComponentData<LifeTime>(entity);
                var buff = em.GetComponentData<Buff>(entity);
                var name = lookup.GetName(guid) ?? "";
                rows.Add($"{Csv(name)},{guid.GuidHash},{life.Duration},{life.EndAction},{buff.BuffType},{buff.BuffEffectType}");
            }
        }
        finally
        {
            kvs.Dispose();
        }

        var dir = Path.Combine(Paths.ConfigPath, "PotionDuration");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "buffs.csv");
        File.WriteAllLines(file, new[] { "name,guid,duration_s,end_action,buff_type,buff_effect_type" }
            .Concat(rows.OrderBy(r => r, StringComparer.OrdinalIgnoreCase)));

        Plugin.Logger.LogInfo($"DumpBuffs: {rows.Count} timed buff prefabs written to {file} " +
            $"(prefab entities {map.Count()}, prefab GUIDs known {pcs._PrefabDataLookup.Count()})");
    }

    static string Csv(string s) => s.IndexOfAny(new[] { ',', '"' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
}
