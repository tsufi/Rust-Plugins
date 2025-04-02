// PvPvE.cs v1.5.0 - Includes monument PvP master list, scientist PvE fix, PvP status export + Rustcord support

using UnityEngine;
using Oxide.Core;
using Oxide.Core.Plugins;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;

namespace Oxide.Plugins
{
    [Info("PvPvE", "Tsufi", "1.5.0")]
    [Description("PvP zones based on biomes, monuments, cargo, hackable crates and spawn points. Includes PvP buffs like gather rate, crafting speed and upkeep reduction.")]
    public class PvPvE : RustPlugin
    {
        #region ⛳ Fields
        [PluginReference] private Plugin Clans;
        [PluginReference] private Plugin Rustcord;

        private ConfigData config;
        private readonly List<Vector3> activeHackableCrates = new();
        private readonly List<TemporaryZone> temporaryPvPZones = new();
        private BaseEntity cargoShip;
        private float cargoShipRadius = 100f;

        private static readonly Dictionary<string, int> biomeTextureIndices = new()
        {
            { "gravel", 0 }, { "dirt", 1 }, { "sand", 2 }, { "rock", 3 },
            { "forest", 4 }, { "grass", 5 }, { "snow", 6 }
        };

        private Timer statusExportTimer;
        #endregion

        private static bool IsSafeZoneStatic(string name)
        {
            name = name.ToLower();
            return name.Contains("outpost") || name.Contains("bandit") ||
                   name.Contains("fishing village") || name.Contains("compound") ||
                   name.Contains("stables");
        }

        #region ⚙️ Config Data
        private class ConfigData
        {
            public bool EnableCraftingBoostInPvP = true;
            public float CraftingSpeedMultiplier = 2.0f;
            public bool EnableUpkeepReductionInPvP = true;
            public float UpkeepMultiplier = 0.75f;
            public bool EnableGatherBoostInPvP = true;
            public float GatherRateMultiplierInPvP = 1.5f;
            public float TerrainThreshold = 0.2f;
            public float MonumentRadius = 150f;
            public float HackableCrateRadius = 100f;
            public int PvPStatusExportInterval = 3600; // seconds
            public string RustcordChannel = ""; // Discord channel ID
            public Dictionary<string, int> BiomePvP = new()
            {
                { "grass", 0 }, { "forest", 0 }, { "rock", 0 }, { "snow", 1 },
                { "sand", 1 }, { "dirt", 0 }
            };
            public Dictionary<string, int> MonumentPvP = new();
            public Dictionary<string, float> MonumentRadiusOverrides = new();
        }

        private class TemporaryZone
        {
            public Vector3 Position;
            public float Radius;
            public float EndTime;
        }
        #endregion

        #region 🚀 Initialization
        protected override void LoadConfig()
        {
            try
            {
                base.LoadConfig();
                config = Config.ReadObject<ConfigData>();

                if (config.MonumentPvP == null || config.MonumentPvP.Count == 0)
                {
                    PrintWarning("MonumentPvP not found or empty, regenerating config...");
                    LoadDefaultConfig();
                }
            }
            catch
            {
                PrintWarning("Config file is invalid. Regenerating default config...");
                LoadDefaultConfig();
            }

            Config.WriteObject(config, true);
        }

        void OnNewSave(string filename)
        {
        LoadDefaultConfig();
        Puts("Config Remake");
        SaveConfig();
        }
        protected override void LoadDefaultConfig()
        {
            config = new ConfigData();
            DetectMonuments();
            SaveConfig();
        }

        void Init()
        {
            NextTick(() =>
            {
                DetectMonuments();
                ExportPvPStatus();

                if (config.PvPStatusExportInterval > 0)
                {
                    statusExportTimer = timer.Every(config.PvPStatusExportInterval, ExportPvPStatus);
                }
            });
        }


        private bool IsSafeZone(string name)
        {
            name = name.ToLower();
            return name.Contains("outpost") || name.Contains("bandit") ||
                   name.Contains("fishing village") || name.Contains("compound") ||
                   name.Contains("stables");
        }
        #endregion

        #region 📤 PvP Status Export
        private void ExportPvPStatus()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Monument PvP Status:");

            foreach (var kvp in config.MonumentPvP.OrderBy(x => x.Key))
            {
                string status = kvp.Value == 1 ? "PvP" : "PvE";
                sb.AppendLine($"{kvp.Key}: {status}");
            }

            var filePath = Interface.Oxide.DataDirectory + "/PvPvE_MonumentStatus.txt";
            File.WriteAllText(filePath, sb.ToString());

            if (!string.IsNullOrEmpty(config.RustcordChannel) && Rustcord != null)
            {
                Rustcord.Call("SendMessage", config.RustcordChannel, $"```\n{sb.ToString()}\n```", null);
            }
        }
        #endregion

        #region 🛡️ Damage Handling
object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
{
    if (entity == null || info == null || info.Initiator == null)
        return null;

    // Always allow NPC damage handling
    if (entity is BaseNpc || info.Initiator is BaseNpc)
        return null;

    var attacker = info.Initiator as BasePlayer;
    if (attacker == null || attacker == entity)
        return null;

    if (entity is LootContainer || entity is CollectibleEntity)
        return null;

    // If we're in a PvE zone (outside monuments)
    if (!IsNearAnyMonument(entity.transform.position))
    {
        // Allow damage to structures if the player owns the structure
        if (entity.OwnerID != 0 && attacker.userID == entity.OwnerID)
            return null;

        // Prevent PvP in non-monument areas
        attacker.ChatMessage("<color=orange>PvP is disabled in this area!</color>");
        return true;
    }

    // If we're inside a monument PvE zone, allow damage to doors and structures
    if (IsNearAnyMonument(entity.transform.position))
    {
        // Allow damage to monument doors and structures even in PvE
        if (entity.OwnerID != 0 && attacker.userID == entity.OwnerID) // Player can destroy own structure
            return null;
        
        // Block PvP damage in monuments
        if (entity is BasePlayer)
        {
            attacker.ChatMessage("<color=orange>PvP is disabled in this monument!</color>");
            return true;
        }
    }

    attacker?.ChatMessage("<color=orange>PvP is disabled in this area!</color>");
    return true;
}


        #endregion

        #region 🏷️ Monument Utilities
        private string GetMonumentShortName(MonumentInfo monument)
        {
            if (string.IsNullOrEmpty(monument.name)) return "Unknown";

            string path = monument.name.ToLower();
            if (path.Contains("oilrig2")) return "Large Oilrig";
            if (path.Contains("oilrig")) return "Small Oilrig";
            if (path.Contains("underwater_lab")) return "Underwater Lab";
            if (path.Contains("airfield")) return "Airfield";
            if (path.Contains("harbor_1")) return "Harbor 1";
            if (path.Contains("harbor_2")) return "Harbor 2";
            if (path.Contains("harbor")) return "Harbor";
            if (path.Contains("launchsite")) return "Launch Site";
            if (path.Contains("military_tunnel")) return "Military Tunnel";
            if (path.Contains("trainyard")) return "Train Yard";
            if (path.Contains("oxum")) return "Oxum's Gas Station";
            if (path.Contains("satellite")) return "Satellite Dish";
            if (path.Contains("junkyard")) return "Junkyard";
            if (path.Contains("bandit")) return "Bandit Camp";
            if (path.Contains("outpost")) return "Outpost";
            if (path.Contains("supermarket")) return "Supermarket";
            if (path.Contains("warehouse")) return "Warehouse";
            if (path.Contains("powerplant")) return "Power Plant";
            if (path.Contains("water_treatment")) return "Water Treatment Plant";
            return monument.name.Split('/').Last().Replace(".prefab", "").Replace('_', ' ').Trim();
        }
        #endregion

        #region 🐞 Debug Command
        [ChatCommand("pvp_debug")]
        private void DebugPvPZone(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin) return;
            Vector3 pos = player.transform.position;
            List<string> reasons = new();

            if (IsInAllowedBiome(pos)) reasons.Add("Biome");
            if (IsNearPvPMonument(pos)) reasons.Add("Monument");
            if (IsNearHackableCrate(pos)) reasons.Add("Hackable Crate");
            if (IsInTemporaryPvPZone(pos)) reasons.Add("Temporary Zone");
            if (IsOnCargoShip(pos)) reasons.Add("Cargo Ship");

            if (reasons.Count == 0)
                player.ChatMessage("<color=yellow>You are in a PvE area.</color>");
            else
                player.ChatMessage($"<color=red>PvP Enabled</color> due to: {string.Join(", ", reasons)}");
        }
        #endregion

        #region 🔍 PvP Zone Checks
        private bool IsPvPAllowed(Vector3 position)
        {
            return IsInAllowedBiome(position)
                || IsNearPvPMonument(position)
                || IsNearHackableCrate(position)
                || IsInTemporaryPvPZone(position)
                || IsOnCargoShip(position);
        }

        private bool IsInAllowedBiome(Vector3 position)
        {
            float normX = TerrainMeta.NormalizeX(position.x);
            float normZ = TerrainMeta.NormalizeZ(position.z);

            foreach (var kvp in config.BiomePvP)
            {
                string biomeName = kvp.Key.ToLower();
                if (!biomeTextureIndices.TryGetValue(biomeName, out int index)) continue;

                float weight = TerrainMeta.SplatMap.GetSplat(normX, normZ, index);
                if (weight > config.TerrainThreshold && kvp.Value == 1)
                    return true;
            }

            return false;
        }

        private bool IsNearPvPMonument(Vector3 position)
        {
            foreach (var monument in TerrainMeta.Path.Monuments)
            {
                string name = GetMonumentShortName(monument);
                if (!config.MonumentPvP.TryGetValue(name, out int enabled) || enabled == 0)
                    continue;

                float radius = config.MonumentRadiusOverrides.TryGetValue(name, out float custom) ? custom : config.MonumentRadius;
                if (Vector3.Distance(position, monument.transform.position) <= radius)
                    return true;
            }
            return false;
        }

        private bool IsNearHackableCrate(Vector3 position)
        {
            foreach (var cratePos in activeHackableCrates)
            {
                if (Vector3.Distance(position, cratePos) <= config.HackableCrateRadius)
                    return true;
            }
            return false;
        }

        private bool IsInTemporaryPvPZone(Vector3 position)
        {
            float now = Time.realtimeSinceStartup;
            foreach (var zone in temporaryPvPZones)
            {
                if (now > zone.EndTime) continue;
                if (Vector3.Distance(position, zone.Position) <= zone.Radius)
                    return true;
            }
            return false;
        }

        private bool IsOnCargoShip(Vector3 position)
        {
            return cargoShip != null && Vector3.Distance(position, cargoShip.transform.position) <= cargoShipRadius;
        }
        #endregion

        #region ⚒️ PvP Buff Hooks
        object OnBuildingPrivilegeCalculateUpkeepCost(BuildingPrivlidge tc, Dictionary<ItemDefinition, int> upkeepCosts)
        {
            if (tc == null || !config.EnableUpkeepReductionInPvP) return null;
            if (IsPvPAllowed(tc.transform.position))
            {
                foreach (var itemDef in upkeepCosts.Keys.ToList())
                {
                    upkeepCosts[itemDef] = Mathf.CeilToInt(upkeepCosts[itemDef] * config.UpkeepMultiplier);
                }
            }
            return upkeepCosts;
        }

        void OnItemCraft(ItemCraftTask task, BasePlayer crafter)
        {
            if (crafter == null || task == null || !config.EnableCraftingBoostInPvP) return;
            if (IsPvPAllowed(crafter.transform.position))
            {
                task.endTime = Time.realtimeSinceStartup + ((task.blueprint.time * task.amount) / config.CraftingSpeedMultiplier);
            }
        }

        object OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            var player = entity as BasePlayer;
            if (player == null || item == null || dispenser == null || !config.EnableGatherBoostInPvP)
                return null;

            if (dispenser.containedItems != null && dispenser.containedItems.Count > 0)
                return null;

            if (!dispenser.GetComponent<ResourceEntity>())
                return null;

            if (IsPvPAllowed(player.transform.position))
            {
                item.amount = Mathf.CeilToInt(item.amount * config.GatherRateMultiplierInPvP);
            }

            return null;
        }
        #endregion

        #region 🧠 Monument Detection
private void DetectMonuments()
{
    if (config.MonumentPvP == null)
        config.MonumentPvP = new Dictionary<string, int>();

    // Temporary dictionary to store changes
    var monumentChanges = new Dictionary<string, int>();

    // Force PvP for certain monuments based on biome settings
    foreach (var biome in config.BiomePvP)
    {
        string biomeName = biome.Key.ToLower();
        if (biome.Value == 1) // If the biome is PvP
        {
            // Make all monuments in this biome PvP
            foreach (var monument in TerrainMeta.Path.Monuments)
            {
                string name = GetMonumentShortName(monument);
                if (!string.IsNullOrEmpty(name) && !config.MonumentPvP.ContainsKey(name))
                {
                    monumentChanges[name] = 1; // Set to PvP for all biomes marked as PvP
                }
            }
        }
    }

    // Apply the changes outside the loop to avoid modifying the collection during iteration
    foreach (var change in monumentChanges)
    {
        config.MonumentPvP[change.Key] = change.Value;
    }

    // Apply the updated configuration
    Config.WriteObject(config, true);
    Puts($"Detected {config.MonumentPvP.Count} monuments. PvP config saved.");
}



        private bool IsNearAnyMonument(Vector3 position)
        {
            foreach (var monument in TerrainMeta.Path.Monuments)
            {
                float radius = config.MonumentRadiusOverrides.TryGetValue(GetMonumentShortName(monument), out float r)
                    ? r : config.MonumentRadius;

                if (Vector3.Distance(position, monument.transform.position) <= radius)
                    return true;
            }

            // If no monuments were found within the radius, return false
            return false;
        

        #endregion

        }
    }
}
