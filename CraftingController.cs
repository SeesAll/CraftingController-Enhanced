using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Crafting Controller", "Whispers88, modified by SeesAll", "3.4.2")]
    [Description("Allows you to modify the time spend crafting and which items can be crafted")]

    //Credits to previous authors Nivex & Mughisi
    public class CraftingController : RustPlugin
    {
        #region Config
        private Configuration config;
        private bool configLoadFailed;

        private readonly Dictionary<string, CraftingBackupData> defaultsetup = new Dictionary<string, CraftingBackupData>();

        public class CraftingData
        {
            public bool canCraft;
            public bool canResearch;
            public bool useCustomCraftTime;
            public float craftTime;
            public int workbenchLevel;
            public ulong defaultskinid;
        }

        public struct CraftingBackupData
        {
            public bool canCraft;
            public bool canResearch;
            public bool forceCraftTime;
            public float craftTime;
            public float eraTime;
            public int workbenchLevel;
            public int eraWorkbenchLevel;
        }

        public class Configuration
        {

            [JsonProperty("Default crafting rate multiplier 0 = Instant, 1 = Default, 2 = 2x speed")]
            public float CraftingRateMultiplier = 1;

            [JsonProperty("Save commands to config (save config changes via command to the configuration)")]
            public bool SaveCommands = true;

            [JsonProperty("Simple Mode (disables: instant bulk craft, skin options and full inventory checks for better performance)")]
            public bool SimpleMode = false;

            [JsonProperty("Allow crafting when inventory is full")]
            public bool FullInventory = false;

            [JsonProperty("Complete crafting on server shut down")]
            public bool CompleteCrafting = false;

            [JsonProperty("Craft items with random skins if not already skinned")]
            public bool RandomSkins = false;

            [JsonProperty("Show Crafting Notes")]
            public bool ShowCraftNotes = false;

            [JsonProperty("Crafting rate bonus mulitplier (apply oxide perms for additional mulitpliers", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, float> BonusMultiplier = new Dictionary<string, float>() { { "vip1", 1.5f }, { "vip2", 2f } };

            [JsonProperty("Advanced Crafting Options", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, CraftingData> CraftingOptions = new Dictionary<string, CraftingData>();

        }

        protected override void LoadDefaultConfig()
        {
            configLoadFailed = false;
            config = new Configuration();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null)
                {
                    throw new JsonException();
                }
            }
            catch
            {
                configLoadFailed = true;
                config = new Configuration();
                PrintError($"Configuration file {Name}.json is invalid. Defaults will be used in memory, but the invalid file will not be overwritten.");
            }
        }

        protected override void SaveConfig()
        {
            if (configLoadFailed)
            {
                PrintWarning($"Configuration changes were not saved because {Name}.json could not be loaded. Repair or replace the file, then reload the plugin.");
                return;
            }

            Puts($"Configuration changes saved to {Name}.json");
            Config.WriteObject(config, true);
        }

        #endregion Config

        #region Init
        private const string perminstantbulkcraft = "craftingcontroller.instantbulkcraft";
        private const string permblockitems = "craftingcontroller.blockitems";
        private const string permitemrate = "craftingcontroller.itemrate";
        private const string permcraftingrate = "craftingcontroller.craftingrate";
        private const string permsetbenchlvl = "craftingcontroller.setbenchlvl";
        private const string permsetskins = "craftingcontroller.setskins";

        private readonly List<string> permissions = new List<string> { perminstantbulkcraft, permblockitems, permitemrate, permcraftingrate, permsetbenchlvl, permsetskins };
        private readonly List<string> commands = new List<string> { nameof(CommandCraftingRate), nameof(CommandCraftTime), nameof(CommandBlockItem), nameof(CommandUnblockItem), nameof(CommandSetDefaultSkin), nameof(CommandWorkbenchLVL) };

        private string[] bonusPermNames = Array.Empty<string>();
        private float[] bonusPermMults = Array.Empty<float>();
        private static readonly MethodInfo finishCraftingMethod = typeof(ItemCrafter).GetMethod("FinishCrafting", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private void OnServerInitialized()
        {
            BackupDefaultBPs();
            bool configChanged = NormalizeConfig();

            bonusPermNames = new string[config.BonusMultiplier.Count];
            bonusPermMults = new float[config.BonusMultiplier.Count];
            int tier = 0;
            foreach (var bonus in config.BonusMultiplier)
            {
                bonusPermNames[tier] = "craftingcontroller." + bonus.Key;
                bonusPermMults[tier] = bonus.Value;
                tier++;
                WarnIfPercentage(bonus.Key, bonus.Value);
            }
            WarnIfPercentage("Default crafting rate multiplier", config.CraftingRateMultiplier);

            //register permissions
            permissions.ForEach(perm => permission.RegisterPermission(perm, this));
            foreach (var perm in bonusPermNames) permission.RegisterPermission(perm, this);
            //register commands
            commands.ForEach(command => AddLocalizedCommand(command));

            if (config.SimpleMode)
            {
                Unsubscribe(nameof(CanCraft));
                Unsubscribe(nameof(OnItemCraft));
                Unsubscribe(nameof(OnItemCraftFinished));
            }

            foreach (var item in ItemManager.bpList)
            {
                CraftingData data;
                if (config.CraftingOptions.TryGetValue(item.targetItem.shortname, out data) && data != null)
                {
                    if (float.IsNaN(data.craftTime) || float.IsInfinity(data.craftTime) || data.craftTime < 0f)
                    {
                        data.craftTime = item.time;
                        configChanged = true;
                    }
                    if (data.workbenchLevel < -1 || data.workbenchLevel > 3)
                    {
                        data.workbenchLevel = Mathf.Clamp(data.workbenchLevel, -1, 3);
                        configChanged = true;
                    }
                    continue;
                }

                config.CraftingOptions[item.targetItem.shortname] = new CraftingData()
                {
                    craftTime = item.time,
                    workbenchLevel = item.workbenchLevelRequired,
                    canCraft = item.userCraftable,
                    canResearch = item.isResearchable
                };
                configChanged = true;
            }

            if (configChanged) SaveConfig();
            UpdateCraftingRate();
        }

        private bool NormalizeConfig()
        {
            bool changed = false;

            if (float.IsNaN(config.CraftingRateMultiplier) || float.IsInfinity(config.CraftingRateMultiplier) || config.CraftingRateMultiplier < 0f)
            {
                config.CraftingRateMultiplier = 1f;
                changed = true;
            }

            if (config.BonusMultiplier == null)
            {
                config.BonusMultiplier = new Dictionary<string, float>();
                changed = true;
            }
            else
            {
                foreach (string key in new List<string>(config.BonusMultiplier.Keys))
                {
                    float value = config.BonusMultiplier[key];
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        config.BonusMultiplier.Remove(key);
                        changed = true;
                    }
                    else if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
                    {
                        config.BonusMultiplier[key] = 1f;
                        changed = true;
                    }
                }
            }

            if (config.CraftingOptions == null)
            {
                config.CraftingOptions = new Dictionary<string, CraftingData>();
                changed = true;
            }

            return changed;
        }

        private void WarnIfPercentage(string key, float value)
        {
            if (value <= 10f) return;
            PrintWarning($"'{key}' is {value}, which means {value}x craft speed (effectively instant). " +
                         $"If that came from a pre-3.3.6 config it was a percentage - use {100f / value:0.##} instead.");
        }

        private void BackupDefaultBPs()
        {
            foreach (var bp in ItemManager.GetBlueprints())
            {
                var target = bp.targetItem;
                if (target == null || defaultsetup.ContainsKey(target.shortname)) continue;

                ItemBlueprint.BlueprintOverride recipeOverride = bp.GetRecipeOverride();

                defaultsetup.Add(target.shortname, new CraftingBackupData()
                {
                    canCraft = bp.userCraftable,
                    canResearch = bp.isResearchable,
                    forceCraftTime = bp.ForceThisCraftTime,
                    craftTime = bp.time,
                    eraTime = recipeOverride.craftTime,
                    workbenchLevel = bp.workbenchLevelRequired,
                    eraWorkbenchLevel = recipeOverride.workbenchLevel
                });
            }
        }

        private void Unload()
        {
            RestoreQueuedBlueprints();
            ClearBonusBlueprints();
            skinupdate.Clear();

            //Reset to defaults
            foreach (var bp in ItemManager.GetBlueprints())
            {
                var target = bp.targetItem;
                CraftingBackupData craftingData;
                if (target == null || !defaultsetup.TryGetValue(target.shortname, out craftingData))
                    continue;

                SetCraftTime(bp, craftingData.craftTime, craftingData.eraTime);
                bp.ForceThisCraftTime = craftingData.forceCraftTime;
                SetWorkbenchLevel(bp, craftingData.workbenchLevel, craftingData.eraWorkbenchLevel);
                bp.userCraftable = craftingData.canCraft;
                bp.isResearchable = craftingData.canResearch;
            }
        }

        #endregion Init

        #region Localization
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoInvSpace"] = "You don't have enough room to craft this item!",
                ["NoPerms"] = "You don't have permission to use this command.",
                ["CannotFindItem"] = "Cannot find item {0}.",
                ["ItemBlocked"] = "{0} has been blocked from crafting.",
                ["ItemUnblocked"] = "{0} has been unblocked from crafting.",
                ["NeedsAdvancedOptions"] = "You need to enable advanced crafting options in your config to use this.",
                ["WrongNumberInput"] = "Your input needs to be a number.",
                ["ItemCraftTimeSet"] = "{0} craft time set to {1} seconds",
                ["WorkbenchLevelSet"] = "{0} workbench level set to {1}",
                ["CurrentCraftingRate"] = "The current speed crafting multiplier is {0}",
                ["CraftingMultiUpdated"] = "The crafting speed multiplier was updated to {0}",
                ["CraftTime2Args"] = "This command needs two arguments in the format /crafttime item.shortname timetocraft",
                ["BlockItem1Args"] = "This command needs one argument in the format /blockitem item.shortname",
                ["UnblockItem1Args"] = "This command needs one argument in the format /unblockitem item.shortname",
                ["WorckBenchLvl2Args"] = "This command needs two arguments in the format /benchlvl item.shortname workbenchlvl",
                ["BenchLevelInput"] = "The work bench level must be between 0 and 3, or 'default'",
                ["SetSkin2Args"] = "This command needs one argument in the format /setcraftskin item.shortname skinworkshopid",
                ["SkinSet"] = "The default skin for {0} was set to {1}",
                ["CraftTimeCheck"] = "The craft time of this item is {0}",
                //Commands
                ["CommandCraftingRate"] = "craftrate",
                ["CommandCraftTime"] = "crafttime",
                ["CommandBlockItem"] = "blockitem",
                ["CommandUnblockItem"] = "unblockitem",
                ["CommandWorkbenchLVL"] = "benchlvl",
                ["CommandSetDefaultSkin"] = "setcraftskin"

            }, this);
        }

        #endregion Localization

        #region Commands
        private void CommandCraftingRate(IPlayer iplayer, string command, string[] args)
        {
            if (!HasPerm(iplayer.Id, permcraftingrate))
            {
                Message(iplayer, "NoPerms");
                return;
            }
            if (args.Length == 0)
            {
                Message(iplayer, "CurrentCraftingRate", config.CraftingRateMultiplier);
                return;
            }
            float craftingrate;

            if (!TryParseNumber(args[0], out craftingrate))
            {
                Message(iplayer, "WrongNumberInput");
                return;
            }
            if (craftingrate < 0f) craftingrate = 0f;
            config.CraftingRateMultiplier = craftingrate;
            UpdateCraftingRate();
            Message(iplayer, "CraftingMultiUpdated", config.CraftingRateMultiplier);
            if (config.SaveCommands) SaveConfig();
        }

        private void CommandCraftTime(IPlayer iplayer, string command, string[] args)
        {
            if (!HasPerm(iplayer.Id, permitemrate))
            {
                Message(iplayer, "NoPerms");
                return;
            }
            if (args.Length == 1)
            {
                var itemcheck = FindItem(args[0]);
                if (itemcheck && itemcheck.Blueprint != null)
                {
                    Message(iplayer, "CraftTimeCheck", itemcheck.Blueprint.GetCraftTime());
                    return;
                }
                Message(iplayer, "CannotFindItem", args[0]);
                return;
            }
            if (args.Length < 2)
            {
                Message(iplayer, "CraftTime2Args");
                return;
            }
            var setitem = FindItem(args[0]);
            if (!setitem)
            {
                Message(iplayer, "CannotFindItem", args[0]);
                return;
            }
            CraftingData craftingdata;
            if (!config.CraftingOptions.TryGetValue(setitem.shortname, out craftingdata))
            {
                Message(iplayer, "NeedsAdvancedOptions");
                return;
            }

            if (args[1].Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                craftingdata.useCustomCraftTime = false;
                UpdateCraftingRate();
                Message(iplayer, "ItemCraftTimeSet", setitem.shortname, setitem.Blueprint.GetCraftTime().ToString());
                if (config.SaveCommands) SaveConfig();
                return;
            }

            float crafttime;
            if (!TryParseNumber(args[1], out crafttime))
            {
                Message(iplayer, "WrongNumberInput");
                return;
            }
            if (crafttime < 0f) crafttime = 0f;

            craftingdata.craftTime = crafttime;
            craftingdata.useCustomCraftTime = true;
            UpdateCraftingRate();

            Message(iplayer, "ItemCraftTimeSet", setitem.shortname, crafttime.ToString());
            if (config.SaveCommands) SaveConfig();
        }

        private void CommandBlockItem(IPlayer iplayer, string command, string[] args)
        {
            if (!HasPerm(iplayer.Id, permblockitems))
            {
                Message(iplayer, "NoPerms");
                return;
            }
            if (args.Length < 1)
            {
                Message(iplayer, "BlockItem1Args");
                return;
            }
            var blockitem = FindItem(args[0]);
            if (!blockitem)
            {
                Message(iplayer, "CannotFindItem", args[0]);
                return;
            }
            CraftingData craftingdata;
            if (!config.CraftingOptions.TryGetValue(blockitem.shortname, out craftingdata))
            {
                Message(iplayer, "NeedsAdvancedOptions");
                return;
            }
            craftingdata.canCraft = false;
            craftingdata.canResearch = false;
            UpdateCraftingRate();
            Message(iplayer, "ItemBlocked", blockitem.shortname);
            if (config.SaveCommands) SaveConfig();
        }

        private void CommandUnblockItem(IPlayer iplayer, string command, string[] args)
        {
            if (!HasPerm(iplayer.Id, permblockitems))
            {
                Message(iplayer, "NoPerms");
                return;
            }
            if (args.Length < 1)
            {
                Message(iplayer, "UnblockItem1Args");
                return;
            }
            var blockitem = FindItem(args[0]);
            if (!blockitem)
            {
                Message(iplayer, "CannotFindItem", args[0]);
                return;
            }
            CraftingData craftingdata;
            if (!config.CraftingOptions.TryGetValue(blockitem.shortname, out craftingdata))
            {
                Message(iplayer, "NeedsAdvancedOptions");
                return;
            }
            craftingdata.canCraft = true;
            craftingdata.canResearch = true;
            UpdateCraftingRate();
            Message(iplayer, "ItemUnblocked", blockitem.shortname);
            if (config.SaveCommands) SaveConfig();
        }

        private void CommandWorkbenchLVL(IPlayer iplayer, string command, string[] args)
        {
            if (!HasPerm(iplayer.Id, permsetbenchlvl))
            {
                Message(iplayer, "NoPerms");
                return;
            }
            if (args.Length < 2)
            {
                Message(iplayer, "WorckBenchLvl2Args");
                return;
            }
            var item = FindItem(args[0]);
            if (!item)
            {
                Message(iplayer, "CannotFindItem", args[0]);
                return;
            }
            CraftingData craftingdata;
            if (item.Blueprint == null || !config.CraftingOptions.TryGetValue(item.shortname, out craftingdata))
            {
                Message(iplayer, "NeedsAdvancedOptions");
                return;
            }

            if (args[1].Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                craftingdata.workbenchLevel = -1;
                UpdateCraftingRate();
                Message(iplayer, "WorkbenchLevelSet", item.shortname, item.Blueprint.GetWorkbenchLevel().ToString());
                if (config.SaveCommands) SaveConfig();
                return;
            }

            int benchlvl;
            if (!int.TryParse(args[1], out benchlvl))
            {
                Message(iplayer, "WrongNumberInput");
                return;
            }
            if (benchlvl < 0 || benchlvl > 3)
            {
                Message(iplayer, "BenchLevelInput");
                return;
            }

            craftingdata.workbenchLevel = benchlvl;
            UpdateCraftingRate();
            Message(iplayer, "WorkbenchLevelSet", item.shortname, benchlvl.ToString());
            if (config.SaveCommands) SaveConfig();
        }

        private void CommandSetDefaultSkin(IPlayer iplayer, string command, string[] args)
        {
            if (!HasPerm(iplayer.Id, permsetskins))
            {
                Message(iplayer, "NoPerms");
                return;
            }
            if (args.Length < 2)
            {
                Message(iplayer, "SetSkin2Args");
                return;
            }
            var setitem = FindItem(args[0]);
            if (!setitem)
            {
                Message(iplayer, "CannotFindItem", args[0]);
                return;
            }
            ulong skinid;
            if (!ulong.TryParse(args[1], out skinid))
            {
                Message(iplayer, "WrongNumberInput");
                return;
            }
            CraftingData craftingdata;
            if (!config.CraftingOptions.TryGetValue(setitem.shortname, out craftingdata))
            {
                Message(iplayer, "NeedsAdvancedOptions");
                return;
            }
            craftingdata.defaultskinid = skinid;
            Message(iplayer, "SkinSet", setitem.shortname, skinid.ToString());
            if (config.SaveCommands) SaveConfig();
        }
        #endregion Commands

        #region Methods
        private void UpdateCraftingRate()
        {
            foreach (var bp in ItemManager.GetBlueprints())
            {
                var target = bp.targetItem;
                if (target == null) continue;

                CraftingData data;
                CraftingBackupData def;
                if (!config.CraftingOptions.TryGetValue(target.shortname, out data)) continue;
                if (!defaultsetup.TryGetValue(target.shortname, out def)) continue;

                bp.userCraftable = data.canCraft;
                bp.isResearchable = data.canResearch;

                float bptime = def.eraTime > 0f ? def.eraTime : def.craftTime;

                if (config.CraftingRateMultiplier == 0f)
                    bptime = 0f;
                else
                {
                    if (data.useCustomCraftTime)
                        bptime = data.craftTime;
                    else
                        bptime /= config.CraftingRateMultiplier;
                }

                SetCraftTime(bp, bptime, bptime);

                bp.ForceThisCraftTime = true;

                if (data.workbenchLevel >= 0)
                    SetWorkbenchLevel(bp, data.workbenchLevel, data.workbenchLevel);
                else
                    SetWorkbenchLevel(bp, def.workbenchLevel, def.eraWorkbenchLevel);

                RefreshBonusBlueprints(bp, bptime);
            }
        }

        private void SetCraftTime(ItemBlueprint bp, float time, float eraTime)
        {
            for (int i = 0; i < bp.Overrides.Count; i++)
            {
                if (bp.Overrides[i].TargetEra != ConVar.Server.Era) continue;
                var bpoverride = bp.Overrides[i];
                bpoverride.craftTime = eraTime;
                bp.Overrides[i] = bpoverride;
                break;
            }
            bp.time = time;
        }

        private void SetWorkbenchLevel(ItemBlueprint bp, int level, int eraLevel)
        {
            bp.workbenchLevelRequired = level;
            for (int i = 0; i < bp.Overrides.Count; i++)
            {
                if (bp.Overrides[i].TargetEra != ConVar.Server.Era) continue;
                var bpoverride = bp.Overrides[i];
                bpoverride.workbenchLevel = eraLevel;
                bp.Overrides[i] = bpoverride;
                break;
            }
        }

        private void InstantBulkCraft(BasePlayer player, ItemCraftTask task, ItemDefinition item, int amount, int craftSkin, ulong skin)
        {
            if (skin == 0uL && craftSkin != 0)
            {
                skin = ItemDefinition.FindSkin(item.itemid, craftSkin);
            }
            int maxStack = Mathf.Max(1, item.stackable);

            bool scaleCondition = task.conditionScale != 1f && item.condition.enabled && item.condition.max > 0f;
            bool notes = config.ShowCraftNotes;
            var inv = player.inventory;
            var crafter = inv.crafting;

            for (int remaining = amount; remaining > 0; remaining -= maxStack)
            {
                int stack = Mathf.Min(remaining, maxStack);
                var itemtogive = ItemManager.Create(item, stack, skin);

                if (scaleCondition)
                {
                    itemtogive.maxCondition *= task.conditionScale;
                    itemtogive.condition = itemtogive.maxCondition;
                }

                itemtogive.OnVirginSpawn(player);
                itemtogive.SetItemOwnership(player, ItemOwnershipPhrases.CraftedPhrase);

                if (skin != 0uL)
                {
                    var held = itemtogive.GetHeldEntity();
                    if (held != null) held.skinID = skin;
                }

                if (!inv.GiveItem(itemtogive))
                    itemtogive.Drop(inv.containerMain.dropPosition, inv.containerMain.dropVelocity);

                if (notes) player.Command("note.inv", item.itemid, stack);
                Interface.CallHook("OnItemCraftFinished", task, itemtogive, crafter);
            }

            player.ProcessMissionEvent(BaseMission.MissionEventType.CRAFT_ITEM, item.itemid, amount);

            if (!string.IsNullOrEmpty(task.blueprint.UnlockAchievment))
                player.GiveAchievement(task.blueprint.UnlockAchievment);


            if (task.takenItems != null)
            {
                foreach (var taken in task.takenItems) taken.Remove();
                task.takenItems.Clear();
            }
        }

        private static void CompleteCrafting(BasePlayer player)
        {
            ItemCrafter crafter = player?.inventory?.crafting;
            if (crafter == null || crafter.queue.Count == 0) return;

            ItemCraftTask task = crafter.queue.First.Value;
            if (finishCraftingMethod == null)
            {
                Interface.Oxide.LogError("Crafting Controller could not locate ItemCrafter.FinishCrafting; cancelling queued crafts without completing the active batch.");
                return;
            }

            try
            {
                finishCraftingMethod.Invoke(crafter, new object[] { task });
            }
            catch (Exception ex)
            {
                Interface.Oxide.LogError($"Crafting Controller could not complete craft task {task.taskUID} during shutdown; it will be cancelled safely instead. {ex.GetBaseException().Message}");
            }

            // FinishCrafting completes one recipe batch. CancelTask then refunds any
            // ingredients that belong to the uncrafted remainder and returns the task
            // to Rust's pool instead of orphaning it during shutdown.
            crafter.CancelTask(task.taskUID);
        }

        private static void CancelAllCrafting(BasePlayer player)
        {
            ItemCrafter crafter = player.inventory.crafting;
            crafter.CancelAll();
        }

        #endregion Methods

        #region Bonus Blueprints

        private readonly Dictionary<ItemBlueprint, ItemBlueprint[]> bonusBPs = new Dictionary<ItemBlueprint, ItemBlueprint[]>();
        private GameObject bonusRoot;

        private ItemBlueprint GetBonusBlueprint(ItemBlueprint bp, int tier)
        {
            ItemBlueprint[] tiers;
            if (!bonusBPs.TryGetValue(bp, out tiers))
                bonusBPs[bp] = tiers = new ItemBlueprint[bonusPermMults.Length];

            ItemBlueprint stand = tiers[tier];
            if (stand != null) return stand;

            if (bonusRoot == null)
            {
                bonusRoot = new GameObject("CraftingController.Bonus");
                bonusRoot.SetActive(false);
            }

            stand = bonusRoot.AddComponent<ItemBlueprint>();

            stand._targetItem = bp.targetItem;
            stand.ingredients = bp.ingredients;
            stand.additionalUnlocks = bp.additionalUnlocks;
            stand.defaultBlueprint = bp.defaultBlueprint;
            stand.userCraftable = bp.userCraftable;
            stand.isResearchable = bp.isResearchable;
            stand.forceShowInConveyorFilter = bp.forceShowInConveyorFilter;
            stand.rarity = bp.rarity;
            stand.workbenchLevelRequired = bp.workbenchLevelRequired;
            stand.surplusItems = bp.surplusItems;
            stand.scrapRequired = bp.scrapRequired;
            stand.scrapFromRecycle = bp.scrapFromRecycle;
            stand.NeedsSteamItem = bp.NeedsSteamItem;
            stand.RequireUnlockedItem = bp.RequireUnlockedItem;
            stand.blueprintStackSize = bp.blueprintStackSize;
            stand.amountToCreate = bp.amountToCreate;
            stand.UnlockAchievment = bp.UnlockAchievment;
            stand.RecycleStat = bp.RecycleStat;
            stand.Overrides = new List<ItemBlueprint.BlueprintOverride>(bp.Overrides);
            stand.ForceThisCraftTime = true;

            float mult = bonusPermMults[tier];
            float time = mult == 0f ? 0f : bp.time / mult;
            SetCraftTime(stand, time, time);

            return tiers[tier] = stand;
        }

        private void RefreshBonusBlueprints(ItemBlueprint bp, float bptime)
        {
            ItemBlueprint[] tiers;
            if (!bonusBPs.TryGetValue(bp, out tiers)) return;

            for (int i = 0; i < tiers.Length; i++)
            {
                if (tiers[i] == null) continue;
                SetWorkbenchLevel(tiers[i], bp.workbenchLevelRequired, bp.GetRecipeOverride().workbenchLevel);
                float mult = bonusPermMults[i];
                float time = mult == 0f ? 0f : bptime / mult;
                SetCraftTime(tiers[i], time, time);
            }
        }

        private void ClearBonusBlueprints()
        {
            bonusBPs.Clear();
            if (bonusRoot != null) UnityEngine.Object.Destroy(bonusRoot);
            bonusRoot = null;
        }

        private void RestoreQueuedBlueprints()
        {
            if (bonusBPs.Count == 0) return;

            var originals = new Dictionary<ItemBlueprint, ItemBlueprint>();
            foreach (var entry in bonusBPs)
            {
                foreach (ItemBlueprint bonus in entry.Value)
                {
                    if (bonus != null) originals[bonus] = entry.Key;
                }
            }

            foreach (BasePlayer player in BasePlayer.allPlayerList)
            {
                ItemCrafter crafter = player?.inventory?.crafting;
                if (crafter == null) continue;

                foreach (ItemCraftTask task in crafter.queue)
                {
                    ItemBlueprint original;
                    if (task?.blueprint != null && originals.TryGetValue(task.blueprint, out original))
                        task.blueprint = original;
                }
            }
        }
        #endregion Bonus Blueprints

        #region Hooks

        private readonly Dictionary<ulong, Dictionary<int, ulong>> skinupdate = new Dictionary<ulong, Dictionary<int, ulong>>();

        private object CanCraft(ItemCrafter crafter, ItemBlueprint blueprint, int amount, bool free)
        {
            if (config.FullInventory || crafter?.owner == null || blueprint?.targetItem == null ||
                amount < 1 || amount > blueprint.targetItem.craftingStackable)
                return null;

            int amountPerCraft = blueprint.amountToCreate;
            if (amountPerCraft < 1) return null;

            int projectedSpace;
            if (!TryGetSpaceAfterCollecting(crafter, blueprint, amount, free, out projectedSpace))
                return null;

            // Rust collects ingredients for the entire requested task before
            // OnItemCraft runs. Approving only the batches that currently fit
            // would require returning the other ingredients afterward, and
            // those refunds can occupy the slots reserved for crafted output.
            // Require room for the complete request while the inventory is
            // still untouched instead.
            if (projectedSpace >= amount * amountPerCraft) return null;

            BasePlayer player = crafter.owner;
            if (player.IsConnected)
                SendReply(player, GetLang("NoInvSpace", player.UserIDString));
            return false;
        }

        private object OnItemCraft(ItemCraftTask task, BasePlayer player, Item fromTempBlueprint)
        {
            if (player == null || task == null || task.blueprint == null || task.amount <= 0) return null;
            var target = task.blueprint.targetItem;
            if (target == null || task.instanceData != null) return null;

            int tocraft = task.amount * task.blueprint.amountToCreate;
            ulong defaultskin = 0uL;
            if (!config.FullInventory)
            {
                int space = FreeSpace(player, target);
                if (space < tocraft)
                {
                    ReturnCraft(task, player);
                    return true;
                }
            }


            if (task.skinID == 0)
            {
                CraftingData data;
                if (config.CraftingOptions.TryGetValue(target.shortname, out data))
                {
                    defaultskin = data.defaultskinid;
                }

                if (config.RandomSkins && defaultskin == 0)
                {
                    List<ulong> skins = GetSkins(target);
                    if (skins.Count > 0) defaultskin = skins.GetRandom();
                }

                if (defaultskin > 999999)
                    SetPendingSkin(player, task, defaultskin);
                else
                    task.skinID = (int)defaultskin;
            }

            int tier = -1;
            for (int i = 0; i < bonusPermNames.Length; i++)
            {
                if (!HasPerm(player.UserIDString, bonusPermNames[i])) continue;
                if (tier < 0 || bonusPermMults[i] == 0f || (bonusPermMults[tier] != 0f && bonusPermMults[i] > bonusPermMults[tier]))
                    tier = i;
            }

            if (tier >= 0 && bonusPermMults[tier] != 1f)
                task.blueprint = GetBonusBlueprint(task.blueprint, tier);

            if (task.blueprint.time == 0f || HasPerm(player.UserIDString, perminstantbulkcraft))
            {
                ClearPendingSkin(player, task);
                InstantBulkCraft(player, task, target, tocraft, task.skinID, defaultskin);
                return true;
            }
            return null;
        }

        private void OnItemCraftFinished(ItemCraftTask task, Item item, ItemCrafter crafter)
        {
            if (task == null || item == null) return;
            var player = crafter?.owner;
            if (player == null) return;

            Dictionary<int, ulong> tasks;
            ulong skinid;
            if (!skinupdate.TryGetValue(player.userID, out tasks)) return;
            if (!tasks.TryGetValue(task.taskUID, out skinid)) return;

            item.skin = skinid;
            var held = item.GetHeldEntity();

            if (held != null)
            {
                held.skinID = skinid;
                held.SendNetworkUpdate();
            }
            if (task.amount <= 0)
                ClearPendingSkin(player, task);
        }

        private void OnItemCraftCancelled(ItemCraftTask task, ItemCrafter crafter)
        {
            BasePlayer player = crafter?.owner;
            if (player == null || task == null) return;

            ClearPendingSkin(player, task);
            if (task.takenItems == null || task.takenItems.Count == 0) return;
            ItemDefinition target = task.blueprint?.targetItem;

            // Oxide invokes this hook before Rust's cancellation refund. Rust
            // only tries containerMain and drops an item when that move fails,
            // which loses ingredients from a full inventory or belt stack.
            // Restore them through the same main/belt/overstack-safe path used
            // by rejected crafts, then clear the list so vanilla does not
            // process the same items a second time.
            foreach (Item item in task.takenItems)
            {
                if (item == null) continue;

                if (target != null && item.IsBlueprint() && item.blueprintTargetDef == target)
                    item.UseItem(task.numCrafted);

                if (item.amount > 0)
                    GiveOrDrop(player, item);
            }
            task.takenItems.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player != null) skinupdate.Remove(player.userID);
        }

        private void OnServerShutdown()
        {
            foreach (var player in BasePlayer.allPlayerList)
            {
                if (player?.inventory?.crafting == null || player.inventory.crafting.queue.Count == 0) continue;
                if (config.CompleteCrafting)
                    CompleteCrafting(player);
                CancelAllCrafting(player);
            }
        }

        #endregion Hooks

        #region Helpers
        private void SetPendingSkin(BasePlayer player, ItemCraftTask task, ulong skin)
        {
            ulong id = player.userID;
            Dictionary<int, ulong> tasks;
            if (!skinupdate.TryGetValue(id, out tasks))
                skinupdate[id] = tasks = new Dictionary<int, ulong>();
            tasks[task.taskUID] = skin;
        }

        private void ClearPendingSkin(BasePlayer player, ItemCraftTask task)
        {
            if (player == null) return;
            ulong id = player.userID;
            Dictionary<int, ulong> tasks;
            if (!skinupdate.TryGetValue(id, out tasks)) return;
            tasks.Remove(task.taskUID);
            if (tasks.Count == 0) skinupdate.Remove(id);
        }

        private static bool TryParseNumber(string input, out float value)
        {
            return float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   && !float.IsNaN(value)
                   && !float.IsInfinity(value);
        }

        private void ReturnCraft(ItemCraftTask task, BasePlayer crafter)
        {
            if (crafter != null && crafter.IsConnected)
                SendReply(crafter, GetLang("NoInvSpace", crafter.UserIDString));
            if (task.takenItems == null) return;

            foreach (var item in task.takenItems)
            {
                if (item != null && item.amount > 0)
                    GiveOrDrop(crafter, item);
            }
            task.takenItems.Clear();
        }

        private static void GiveOrDrop(BasePlayer player, Item item)
        {
            if (item == null || item.amount <= 0) return;

            PlayerInventory inventory = player.inventory;
            if (item.MoveToContainer(inventory.containerMain) ||
                item.MoveToContainer(inventory.containerBelt) ||
                inventory.GiveItem(item))
                return;

            // Admin and stack-size plugins can leave the original source stack
            // above its current normal maximum. A rejected craft must be able to
            // reconstruct that pre-craft stack before falling back to a drop.
            if (item.MoveToContainer(inventory.containerMain, -1, true, true, player) ||
                item.MoveToContainer(inventory.containerBelt, -1, true, true, player))
                return;

            item.Drop(inventory.containerMain.dropPosition, inventory.containerMain.dropVelocity);
        }

        private ItemDefinition FindItem(string itemNameOrId)
        {
            ItemDefinition itemDef;
            if (int.TryParse(itemNameOrId, out int itemId))
            {
                itemDef = ItemManager.FindItemDefinition(itemId);
                return itemDef;
            }
            itemDef = ItemManager.FindItemDefinition(itemNameOrId.ToLowerInvariant());
            return itemDef;
        }

        private int FreeSpace(BasePlayer player, ItemDefinition item)
        {
            return FreeSpace(player, item, null);
        }

        private int FreeSpace(BasePlayer player, ItemDefinition item, Dictionary<Item, int> projectedAmounts)
        {
            var slots = player.inventory.containerMain.capacity + player.inventory.containerBelt.capacity;
            List<Item> containeritems = Pool.Get<List<Item>>();
            Dictionary<ItemDefinition, int> queueamts = Pool.Get<Dictionary<ItemDefinition, int>>();
            containeritems.AddRange(player.inventory.containerMain.itemList);
            containeritems.AddRange(player.inventory.containerBelt.itemList);

            int value = 0;

            foreach (var queueitem in player.inventory.crafting.queue)
            {
                if (queueitem.blueprint.targetItem == item) continue;
                if (queueamts.TryGetValue(queueitem.blueprint.targetItem, out value))
                {
                    queueamts[queueitem.blueprint.targetItem] += queueitem.amount * queueitem.blueprint.amountToCreate;
                    continue;
                }
                queueamts[queueitem.blueprint.targetItem] = queueitem.amount * queueitem.blueprint.amountToCreate;
            }

            int queuestacks = 0;
            foreach (var i in queueamts)
            {
                queuestacks += StackCount(i.Key, i.Value - Stackroom(containeritems, i.Key, projectedAmounts));
            }

            int occupiedSlots = 0;
            int invstackroom = 0;
            foreach (Item inventoryItem in containeritems)
            {
                int inventoryAmount = GetProjectedAmount(inventoryItem, projectedAmounts);
                if (inventoryAmount <= 0) continue;

                occupiedSlots++;
                if (inventoryItem.info == item && inventoryAmount < inventoryItem.MaxStackable())
                    invstackroom += inventoryItem.MaxStackable() - inventoryAmount;
            }

            invstackroom += (slots - occupiedSlots - queuestacks) * Mathf.Max(1, item.stackable);
            foreach (var x in player.inventory.crafting.queue)
            {
                if (x.blueprint.targetItem.shortname == item.shortname)
                {
                    invstackroom -= x.amount * x.blueprint.amountToCreate;
                }
            }
            Pool.FreeUnmanaged(ref containeritems);
            Pool.FreeUnmanaged(ref queueamts);
            return invstackroom;
        }

        private static int GetProjectedAmount(Item item, Dictionary<Item, int> projectedAmounts)
        {
            int amount;
            return projectedAmounts != null && projectedAmounts.TryGetValue(item, out amount) ? amount : item.amount;
        }

        private static int Stackroom(List<Item> items, ItemDefinition item, Dictionary<Item, int> projectedAmounts)
        {
            int stackroom = 0;
            foreach (Item inventoryItem in items)
            {
                int inventoryAmount = GetProjectedAmount(inventoryItem, projectedAmounts);
                if (inventoryItem.info == item && inventoryAmount > 0 && inventoryAmount < inventoryItem.MaxStackable())
                    stackroom += inventoryItem.MaxStackable() - inventoryAmount;
            }
            return stackroom;
        }

        private bool TryGetSpaceAfterCollecting(ItemCrafter crafter, ItemBlueprint blueprint, int amount, bool free, out int space)
        {
            BasePlayer player = crafter.owner;
            Dictionary<Item, int> projectedAmounts = Pool.Get<Dictionary<Item, int>>();

            try
            {
                foreach (ItemContainer container in crafter.containers)
                {
                    foreach (Item inventoryItem in container.itemList)
                    {
                        // CanCraft does not receive the temporary-blueprint item
                        // that CraftItem removes later. Defer to the post-collection
                        // check rather than falsely rejecting that special path.
                        if (inventoryItem.IsBlueprint())
                        {
                            space = 0;
                            return false;
                        }

                        projectedAmounts[inventoryItem] = inventoryItem.amount;
                    }
                }

                if (!free)
                {
                    foreach (ItemAmount ingredient in blueprint.GetIngredients())
                    {
                        int required = (int)ingredient.amount * amount;

                        foreach (ItemContainer container in crafter.containers)
                        {
                            for (int pass = 0; pass < 2 && required > 0; pass++)
                            {
                                bool redirected = pass == 1;
                                foreach (Item inventoryItem in container.itemList)
                                {
                                    int remaining;
                                    if (!projectedAmounts.TryGetValue(inventoryItem, out remaining) || remaining <= 0)
                                        continue;

                                    bool exactItem = inventoryItem.info.itemid == ingredient.itemid;
                                    if ((!redirected && !exactItem) || (redirected && exactItem) ||
                                        !inventoryItem.info.MatchesItemId(ingredient.itemid, true) ||
                                        (inventoryItem.info.condition.enabled && inventoryItem.condition <= 0f))
                                        continue;

                                    int collected = Mathf.Min(remaining, required);
                                    projectedAmounts[inventoryItem] = remaining - collected;
                                    required -= collected;
                                    if (required <= 0) break;
                                }
                            }

                            if (required <= 0) break;
                        }

                        // Let Rust report missing or unusable ingredients rather
                        // than replacing that reason with an inventory-space error.
                        if (required > 0)
                        {
                            space = 0;
                            return false;
                        }
                    }
                }

                space = FreeSpace(player, blueprint.targetItem, projectedAmounts);
                return true;
            }
            finally
            {
                Pool.FreeUnmanaged(ref projectedAmounts);
            }
        }

        private static int StackCount(ItemDefinition item, int amount)
        {
            int maxStack = item.stackable;
            if (maxStack <= 0 || amount <= 0) return 0;
            return amount / maxStack + (amount % maxStack > 0 ? 1 : 0);
        }

        private readonly Dictionary<string, List<ulong>> skinsCache = new Dictionary<string, List<ulong>>();
        private List<ulong> GetSkins(ItemDefinition def)
        {
            List<ulong> skins;
            if (skinsCache.TryGetValue(def.shortname, out skins)) return skins;
            skins = new List<ulong>();
            foreach (var skin in ItemSkinDirectory.ForItem(def))
            {
                skins.Add((ulong)skin.id);
            }
            foreach (var skin in Rust.Workshop.Approved.All.Values)
            {
                if (skin?.Skinnable != null && skin.Skinnable.ItemName == def.shortname)
                    skins.Add(skin.WorkshopdId);
            }
            skinsCache.Add(def.shortname, skins);
            return skins;
        }

        private string GetLang(string langKey, string playerId = null, params object[] args)
        {
            return string.Format(lang.GetMessage(langKey, this, playerId), args);
        }

        private void Message(IPlayer player, string langKey, params object[] args)
        {
            if (player.IsConnected) player.Message(GetLang(langKey, player.Id, args));
        }

        private bool HasPerm(string id, string perm) => permission.UserHasPermission(id, perm);

        private void AddLocalizedCommand(string command)
        {
            foreach (string language in lang.GetLanguages(this))
            {
                Dictionary<string, string> messages = lang.GetMessages(language, this);
                foreach (KeyValuePair<string, string> message in messages)
                {
                    if (!message.Key.Equals(command)) continue;

                    if (string.IsNullOrEmpty(message.Value)) continue;

                    AddCovalenceCommand(message.Value, command);
                }
            }
        }
        #endregion Helpers
    }
}

