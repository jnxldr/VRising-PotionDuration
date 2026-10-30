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
using WeaponCoatingData = ProjectM.WeaponCoating.WeaponCoating;

namespace PotionDuration;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> DumpBuffs;
    internal static ConfigEntry<int> PotionMinutes;
    internal static ConfigEntry<int> ElixirMinutes;
    internal static ConfigEntry<int> ImbuementMinutes;
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
        ImbuementMinutes = Config.Bind("Duration", "ImbuementMinutes", 120,
            new ConfigDescription("Duration in minutes for imbuements (weapon coatings) (base game 60).\n" + examples, minutes));
        Include = Config.Bind("Selection", "Include", "",
            "Comma-separated buff names to treat as potions too (or as elixirs, if the name contains 'Elixir'). The buff must last 60 minutes in the base game.");
        Exclude = Config.Bind("Selection", "Exclude", "", "Comma-separated buff or imbuement (weapon coating) names to leave unchanged.");
        DumpBuffs = Config.Bind("Discovery", "DumpBuffs", false,
            "Write every timed buff (name, ID, duration) to BepInEx/config/PotionDuration/buffs.csv at start, to find buff names.");

        // Server: change the buffs themselves. Client: change its own copy of the prefabs, which the item tooltips read.
        var isServer = IsServer = Paths.ProcessName.Equals("VRisingServer", StringComparison.OrdinalIgnoreCase);
        if (isServer) _harmony = Harmony.CreateAndPatchAll(typeof(ServerReadyPatch), MyPluginInfo.PLUGIN_GUID);
        else
        {
            AddComponent<ClientWorldWatcher>();
            AddComponent<ImbuementIconFixer>();
            _harmony = Harmony.CreateAndPatchAll(typeof(CoatingServerTimePatch), MyPluginInfo.PLUGIN_GUID);
        }
        Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} loaded on {(isServer ? "server" : "client")} " +
            $"(Enabled={Enabled.Value}, PotionMinutes={PotionMinutes.Value}, ElixirMinutes={ElixirMinutes.Value}, " +
            $"ImbuementMinutes={ImbuementMinutes.Value}, DumpBuffs={DumpBuffs.Value})");
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
            if (!IsServer)
            {
                TooltipPatcher.Apply(world, changed);
                ImbuementIconFixer.ClientWorld = world;
            }
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

// Client only. The buff bar builds the imbuement icon's entry with a fixed 60-minute total (LifeTime 3600,
// Age = 3600 - time left), so with more than 60 minutes left the age is negative and the icon shows no timer.
// Writing a corrected entry back into the game's list crashed the client, so this hook only records the server time;
// ImbuementIconFixer then corrects the icon's text and progress ring after the HUD has drawn it.
[HarmonyPatch(typeof(BuffBarParentSystem), nameof(BuffBarParentSystem.AddWeaponCoatingExpireEntry))]
static class CoatingServerTimePatch
{
    internal static double ServerTime = -1;
    internal static double RealTimeAt;

    static void Postfix(ServerTime serverTime)
    {
        ServerTime = serverTime.Time;
        RealTimeAt = Time.realtimeSinceStartupAsDouble;
    }
}

public class ImbuementIconFixer : MonoBehaviour
{
    const float VanillaSeconds = 3600f;
    internal static World ClientWorld;
    internal static readonly HashSet<int> CoatingGuids = new();

    // The HUD hides the icon's own time text and ring every frame, so the fixer shows copies of them that the HUD doesn't know about.
    class Overlay
    {
        public BuffBarEntry Entry;
        public GameObject Text, Ring;
        public LocalizedText Label;
        public UnityEngine.UI.Image RingImage;
        public string LastText;
    }

    readonly Dictionary<IntPtr, Overlay> _overlays = new();
    readonly List<IntPtr> _gone = new();
    float _nextScan;
    string _hoursFormat;

    public ImbuementIconFixer(IntPtr ptr) : base(ptr) { }

    void LateUpdate()
    {
        if (!Plugin.Enabled.Value || CoatingGuids.Count == 0) return;
        try
        {
            var world = ClientWorld;
            if (world == null || !world.IsCreated || CoatingServerTimePatch.ServerTime < 0) return;
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 1f;
                var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<BuffBarEntry>());
                for (int i = 0; i < found.Length; i++)
                {
                    var e = found[i].TryCast<BuffBarEntry>();
                    if (e == null || _overlays.ContainsKey(e.Pointer) || !CoatingGuids.Contains(e.PrefabGUID.GuidHash)) continue;
                    if (e.TimeLeft == null || e.Progress == null) continue;
                    _overlays[e.Pointer] = CreateOverlay(e);
                }
            }
            if (_overlays.Count == 0) return;

            var em = world.EntityManager;
            var coatableType = new ComponentType(Il2CppType.Of<ProjectM.WeaponCoating.Coatable>(), ComponentType.AccessMode.ReadOnly);
            var now = CoatingServerTimePatch.ServerTime + (Time.realtimeSinceStartupAsDouble - CoatingServerTimePatch.RealTimeAt);
            var total = Plugin.ImbuementMinutes.Value * 60f;

            foreach (var (key, o) in _overlays)
            {
                var e = o.Entry;
                if (e == null || o.Text == null || o.Ring == null)
                {
                    _gone.Add(key);
                    continue;
                }
                // The HUD reuses entries for other buffs; at 60 minutes or less the game's own timer is right.
                float left = 0;
                var show = CoatingGuids.Contains(e.PrefabGUID.GuidHash) && em.Exists(e.Entity) && em.HasComponent(e.Entity, coatableType);
                if (show)
                {
                    left = (float)(em.GetComponentData<ProjectM.WeaponCoating.Coatable>(e.Entity).CoatingExpireTime - now);
                    show = left > VanillaSeconds;
                }
                if (o.Text.activeSelf != show) o.Text.SetActive(show);
                if (o.Ring.activeSelf != show) o.Ring.SetActive(show);
                if (!show) continue;

                var text = Hours(e, Mathf.CeilToInt(left / 3600f));
                if (text != o.LastText)
                {
                    o.Label.ForceSet(text);
                    o.LastText = text;
                }
                o.RingImage.fillAmount = Mathf.Clamp01(1f - left / Math.Max(total, left));
            }
            foreach (var key in _gone) _overlays.Remove(key);
            _gone.Clear();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Imbuement icon fix failed: {ex}");
            enabled = false;
        }
    }

    static Overlay CreateOverlay(BuffBarEntry e)
    {
        var o = new Overlay { Entry = e };
        o.Ring = Copy(e.Progress.gameObject, "PotionDuration_Progress");
        o.RingImage = o.Ring.GetComponent<UnityEngine.UI.Image>();
        o.Text = Copy(e.TimeLeft.gameObject, "PotionDuration_TimeLeft");
        o.Label = o.Text.GetComponent<LocalizedText>();
        return o;
    }

    // Copy placed right after the original, so it draws in the same order.
    static GameObject Copy(GameObject original, string name)
    {
        var copy = UnityEngine.Object.Instantiate(original, original.transform.parent);
        copy.name = name;
        copy.transform.SetSiblingIndex(original.transform.GetSiblingIndex() + 1);
        copy.SetActive(false);
        return copy;
    }

    // The game's hours string for the player's language ("{value}h" in English).
    string Hours(BuffBarEntry e, int hours)
    {
        if (_hoursFormat == null)
        {
            try { _hoursFormat = Stunlock.Localization.Localization.Get(e.LKey_Hours, false) ?? ""; }
            catch { _hoursFormat = ""; }
        }
        if (_hoursFormat.Contains("{value}")) return _hoursFormat.Replace("{value}", hours.ToString());
        if (_hoursFormat.Contains("{0}")) return _hoursFormat.Replace("{0}", hours.ToString());
        return hours + "h";
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

    // Returns the changed prefab names with their kind ("potion", "elixir" or "coating"), for the tooltips.
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

        ApplyCoatings(world, exclude, changedBuffs);
        return changedBuffs;
    }

    // Weapon coatings aren't timed buffs: the coating prefab's WeaponCoating.ExpireDuration is copied to the weapon's
    // Coatable.CoatingExpireTime when the coating is applied, and the buff icon timer reads that. Found by component, not name.
    // Adds the changed coatings and their buffs to `changed` (kind "coating") so their tooltips follow.
    static void ApplyCoatings(World world, HashSet<string> exclude, Dictionary<string, string> changed)
    {
        var em = world.EntityManager;
        var pcs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        var lookup = pcs._PrefabLookupMap;
        var coatingType = new ComponentType(Il2CppType.Of<WeaponCoatingData>(), ComponentType.AccessMode.ReadWrite);
        var seconds = Plugin.ImbuementMinutes.Value * 60f;

        int count = 0, skipped = 0;
        var kvs = pcs._PrefabGuidToEntityMap.GetKeyValueArrays(Allocator.Temp);
        try
        {
            for (int i = 0; i < kvs.Length; i++)
            {
                var entity = kvs.Values[i];
                if (!em.HasComponent(entity, coatingType)) continue;
                var name = lookup.GetName(kvs.Keys[i]) ?? "";
                var coating = em.GetComponentData<WeaponCoatingData>(entity);
                var buffName = lookup.GetName(coating.BuffId) ?? "";
                if (exclude.Contains(name)) continue;
                if (coating.ExpireDuration != VanillaSeconds)
                {
                    skipped++;
                    Plugin.Logger.LogWarning($"Skipped imbuement {name} ({kvs.Keys[i].GuidHash}): vanilla duration {coating.ExpireDuration}s, not {VanillaSeconds}s");
                    continue;
                }
                var old = coating.ExpireDuration;
                coating.ExpireDuration = seconds;
                em.SetComponentData(entity, coating);
                count++;
                ImbuementIconFixer.CoatingGuids.Add(kvs.Keys[i].GuidHash);
                changed[name] = "coating";
                if (buffName != "") changed[buffName] = "coating";
                Plugin.Logger.LogInfo($"Changed imbuement {name} ({kvs.Keys[i].GuidHash}, buff {buffName}): {old}s -> {coating.ExpireDuration}s");
            }
        }
        finally
        {
            kvs.Dispose();
        }

        var summary = $"[{world.Name}] {count} imbuements (weapon coatings) changed ({Plugin.ImbuementMinutes.Value} min), {skipped} skipped";
        if (count == 0) Plugin.Logger.LogWarning(summary + ". No imbuement found: a game patch may have changed how weapon coatings work.");
        else Plugin.Logger.LogInfo(summary);
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
        // AB_Consumable_SunResistancePotion_T01_Buff -> sunresistancepotiont01, AB_Elixir_Bat_T01_Buff -> elixirbatt01,
        // Item_Vampire_Coating_Blood -> vampirecoatingblood; the item, buff and recipe prefabs of one potion share that stem.
        var stems = new Dictionary<string, string>();
        foreach (var kv in changedBuffs) stems[Normalize(Regex.Replace(kv.Key, @"^(AB_|Item_)(Consumable_)?|_Buff$", ""))] = kv.Value;
        var minutes = new Dictionary<string, string>
        {
            ["potion"] = Plugin.PotionMinutes.Value.ToString(),
            ["elixir"] = Plugin.ElixirMinutes.Value.ToString(),
            ["coating"] = Plugin.ImbuementMinutes.Value.ToString(),
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
        if (changed == 0) Plugin.Logger.LogWarning(summary + ". No potion/elixir/imbuement tooltip changed.");
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
