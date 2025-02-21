using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using FroggyFilter.Framework;
using FroggyFilter.Framework.Compatibility;
using FroggyFilter.Framework.Extensions;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Companions;
using StardewValley.Extensions;
using StardewValley.GameData;
using StardewValley.Locations;
using StardewValley.Monsters;
using StardewValley.TokenizableStrings;

namespace FroggyFilter;

// ReSharper disable once UnusedType.Global
internal sealed class ModEntry : Mod
{
    private static ModEntry _instance = null!;
    private const int CpLoadingTicks = 6;
    private const int MonsterLocalizedNameIdx = 14;
    private const int IsMinesEnemyIdx = 12;

    private ModConfig _modConfig = null!;
    private Harmony _harmony = null!;

    private int _ticksAfterLaunch;
    private bool _hasLoadedData;

    private readonly Dictionary<string, string> _localizedMonsterNames = new();
    private readonly HashSet<string> _forceExcludedNames = ["Truffle Crab"];
    private readonly HashSet<string> _activeSlayerQuests = [];

    public override void Entry(IModHelper helper)
    {
        _instance = this;
        I18n.Init(Helper.Translation);
        _harmony = new Harmony(ModManifest.UniqueID);

        _modConfig = helper.ReadConfig<ModConfig>();

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.TimeChanged += OnTenMinuteUpdate;
        helper.Events.Content.AssetsInvalidated += OnAssetsInvalidated;

        Patch();
    }

    private void Patch()
    {
        var frogUpdatePatchMethod = AccessTools.DeclaredMethod(
            typeof(HungryFrogCompanion),
            nameof(HungryFrogCompanion.Update)
        );
        var frogUpdateTranspiler = new HarmonyMethod(
            AccessTools.DeclaredMethod(typeof(ModEntry), nameof(TranspileFrogUpdate))
        );

        _harmony.Patch(frogUpdatePatchMethod, transpiler: frogUpdateTranspiler);
    }

    private static IEnumerable<CodeInstruction> TranspileFrogUpdate(IEnumerable<CodeInstruction> instructions)
    {
        var matcher = new CodeMatcher(instructions);

        matcher.MatchEndForward(
            new CodeMatch(OpCodes.Call,
                AccessTools.DeclaredMethod(
                    typeof(Utility),
                    nameof(Utility.findClosestMonsterWithinRange)
                )
            )
        ).ThrowIfNotMatch("Unable to find insertion point to transpile frog eating logic");

        matcher.SetInstruction(
            new CodeInstruction(
                OpCodes.Call,
                AccessTools.DeclaredMethod(
                    typeof(ModEntry),
                    nameof(FindClosestMonsterWithinRangeRespectingChoices)
                )
            )
        );

        return matcher.InstructionEnumeration();
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        Helper.Events.GameLoop.UpdateTicked += HandleContentLoadedTicks;
    }

    private void OnTenMinuteUpdate(object? sender, TimeChangedEventArgs e)
    {
        _activeSlayerQuests.Clear();
        foreach (var monsterSlayerQuestData in DataLoader.MonsterSlayerQuests(Game1.content).Values)
        {
            if (monsterSlayerQuestData.Targets == null)
            {
                continue;
            }
            _activeSlayerQuests.AddRange(monsterSlayerQuestData.Targets);
        }
    }

    private void OnAssetsInvalidated(object? sender, AssetsInvalidatedEventArgs e)
    {
        if (!e.NamesWithoutLocale.Any(x => x.IsEquivalentTo("Data/Monsters")))
        {
            return;
        }

        Monitor.Log("Monster data invalidated - reloading...");
        // Give CP time to reapply patches
        _hasLoadedData = false;
        _ticksAfterLaunch = 0;
        Helper.Events.GameLoop.UpdateTicked += HandleContentLoadedTicks;
    }

    private void LoadAndRegisterMonsters()
    {
        var monsterData = Helper.GameContent.Load<Dictionary<string, string>>("Data/Monsters");
        var configExcluded = _modConfig.ExcludedMonsters.Split('/', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        var configNeedsSave = false;

        foreach (var (internalName, data) in monsterData)
        {
            var split = data.Split('/');
            if (split.Length <= IsMinesEnemyIdx || !split[IsMinesEnemyIdx].Equals("true"))
            {
                Monitor.Log($"Ignoring monster entry {internalName}, likely not an enemy that should be tracked");
                continue;
            }

            if (_forceExcludedNames.Contains(internalName) || configExcluded.Contains(internalName))
            {
                Monitor.Log($"Entry {internalName} is explicitly excluded, skipping...");
                continue;
            }

            var localizedName = split.Length <= MonsterLocalizedNameIdx ? internalName : split[MonsterLocalizedNameIdx];
            _localizedMonsterNames[internalName] = localizedName;


            if (!_modConfig.EnabledMonsters.TryAdd(internalName, true))
            {
                continue;
            }

            configNeedsSave = true;
        }

        UnregisterModConfig();
        if (configNeedsSave)
        {
            Helper.WriteConfig(_modConfig);
        }

        RegisterModConfig();
    }

    private void HandleContentLoadedTicks(object? sender, UpdateTickedEventArgs e)
    {
        _ticksAfterLaunch++;
        if (_hasLoadedData || _ticksAfterLaunch < CpLoadingTicks)
        {
            return;
        }

        LoadAndRegisterMonsters();
        _hasLoadedData = true;
        Helper.Events.GameLoop.UpdateTicked -= HandleContentLoadedTicks;
    }

    private void UnregisterModConfig()
    {
        var modConfigMenuApi = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        modConfigMenuApi?.Unregister(ModManifest);
    }

    private void RegisterModConfig()
    {
        var api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (api is null)
        {
            return;
        }

        api.Register(
            ModManifest,
            reset: () => { _modConfig = new ModConfig(); },
            save: () => Helper.WriteConfig(_modConfig)
        );

        // Set up GMCM
        api.AddSectionTitle(ModManifest, () => I18n.Gmcm_Title_General());

        api.AddBoolOption(ModManifest,
            getValue: () => _modConfig.ExcludeEradicationGoals,
            setValue: value => _modConfig.ExcludeEradicationGoals = value,
            name: () => I18n.Gmcm_Item_ExcludeEradicationGoals(),
            tooltip: () => I18n.Gmcm_Item_ExcludeEradicationGoals_Tooltip()
        );

        api.AddSectionTitle(ModManifest, () => I18n.Gmcm_Title_Monsters());

        var sortedMonsters = _localizedMonsterNames.OrderBy(x => x.Key);
        foreach (var (internalName, localizedName) in sortedMonsters)
        {
            api.AddBoolOption(ModManifest,
                getValue: () => _modConfig.EnabledMonsters.GetOrCreate(internalName, true),
                setValue: value => _modConfig.EnabledMonsters[internalName] = value,
                name: () => _localizedMonsterNames.ContainsKey(localizedName) ? internalName : localizedName
            );
        }

        api.AddPageLink(ModManifest, "advanced-options", () => I18n.Gmcm_Page_Advanced());
        api.AddPage(ModManifest, "advanced-options", () => I18n.Gmcm_Page_Advanced());

        api.AddTextOption(
            ModManifest,
            getValue: () => _modConfig.ExcludedMonsters,
            setValue: value => _modConfig.ExcludedMonsters = value,
            name: () => I18n.Gmcm_Item_MonstersExcluded(),
            tooltip: () => I18n.Gmcm_Item_MonstersExcluded_Tooltip()
        );
    }

    public static Monster? FindClosestMonsterWithinRangeRespectingChoices(
        GameLocation location,
        Vector2 originPoint,
        int range,
        bool ignoreUntargetables = false,
        Func<Monster, bool>? match = null)
    {
        Monster? closestMonster = null;
        float shortestDistance = range + 1;

        foreach (var character in location.characters)
        {
            if (character is not Monster monster)
                continue;

            if (_instance._activeSlayerQuests.Contains(monster.Name) && _instance._modConfig.ExcludeEradicationGoals)
            {
                _instance.Monitor.Log($"Cannot eat {monster.Name}, still have slayer task.");
                continue;
            }

            if (_instance._modConfig.EnabledMonsters.TryGetValue(monster.Name, out var isMonsterEnabled) &&
                !isMonsterEnabled)
            {
                _instance.Monitor.Log($"Cannot eat {monster.Name}, not enabled.");
                continue;
            }

            if (ignoreUntargetables && character is Spiker)
                continue;

            if (match != null && !match(monster))
                continue;

            if (monster.IsInvisible)
                continue;

            var distance = Vector2.Distance(originPoint, character.getStandingPosition());
            if (distance > range || distance >= shortestDistance)
                continue;

            closestMonster = monster;
            shortestDistance = distance;
        }

        return closestMonster;
    }
}