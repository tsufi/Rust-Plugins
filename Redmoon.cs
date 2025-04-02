// Redmoon.cs v1.4.9 - Zombies despawn at night end, wave system added, spawn scaling, and Boss Zombie integration

using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Ext.Discord.Attributes;
using Oxide.Ext.Discord.Builders;
using Oxide.Ext.Discord.Clients;
using Oxide.Ext.Discord.Entities;
using Oxide.Ext.Discord.Interfaces;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Oxide.Plugins
{
    [Info("Redmoon", "Tsufi", "1.4.9")]
    [Description("PvE Redmoon zombie event with Discord integration and leaderboard tracking.")]
    public class Redmoon : RustPlugin, IDiscordPlugin
    {
        public DiscordClient Client { get; set; }
        [PluginReference] private Plugin PvPvE;

        private Snowflake _guildId;

        private ConfigData config;
        private StoredData data;
        private bool eventActive;
        private Timer redmoonTimer;
        private BaseEntity bossZombie;
        private int nightsSinceLastRedmoon = int.MaxValue;

        private List<BaseEntity> spawnedZombies = new List<BaseEntity>();
        private int zombiesKilledThisWave = 0;
        private int waveThreshold = 10;
        private int waveMultiplier = 1;

        private class ConfigData
        {
            public float RedmoonChancePerNight = 0.2f;
            public int RedmoonNightCooldown = 1;
            public int ZombiesPerPlayer = 3;
            public int MaxZombiesPerPlayer = 6;
            public int MaxPlaytimeMinutes = 1440;
            public float ZombieSpawnRadius = 30f;
            public float ZombieHealth = 200f;
            public List<string> LootItems = new() { "scrap", "cloth", "metal.fragments", "pistol.ammo" };
            public int MinLoot = 1;
            public int MaxLoot = 3;
            public float BossSpawnChance = 0.2f;
            public int BossHealth = 1000;
            public int BossLootMultiplier = 2;
            public float BossPvPRadius = 60f;
            public float BossPvPDuration = 300f;
            public string DiscordChannelId = "";
        }

        private class StoredData
        {
            public Dictionary<ulong, int> PlayerPoints = new();
        }

        protected override void LoadDefaultConfig() => config = new ConfigData();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<ConfigData>();
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        private void OnServerSave() => Interface.Oxide.DataFileSystem.WriteObject(Name, data);
        private void OnNewSave(string filename) => data = new StoredData();

        private void Init()
        {
            data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            timer.Every(60f, CheckForNight);
        }

        private void Unload()
        {
            redmoonTimer?.Destroy();
            ResetEnvironment();
        }

        [HookMethod("OnDiscordGuildCreated")]
        private void OnDiscordGuildCreated(DiscordGuild guild)
        {
            _guildId = guild.Id;
        }

        private void CheckForNight()
        {
            if (eventActive || !IsNight()) return;

            if (nightsSinceLastRedmoon < config.RedmoonNightCooldown)
            {
                nightsSinceLastRedmoon++;
                return;
            }

            if (UnityEngine.Random.value <= config.RedmoonChancePerNight)
            {
                PrintToChat("<color=#ff0000>🐾 Wildlife flees in terror as the Redmoon rises!</color>");
                StartRedmoon();
                nightsSinceLastRedmoon = 0;
            }
            else
            {
                nightsSinceLastRedmoon++;
            }
        }

        private bool IsNight()
        {
            float hour = TOD_Sky.Instance.Cycle.Hour;
            return hour >= 18f || hour <= 5f;
        }

        private void StartRedmoon()
        {
            eventActive = true;
            PrintToChat("<color=#ff0000>⚠ REDMOON has begun! The undead are rising...</color>");
            RemoveAllAnimals();  // Remove all animals before spawning zombies
            SetRedmoonEffects();
            SpawnZombies();  // Call to the correct SpawnZombies method
            TrySpawnBoss();
            redmoonTimer = timer.Once(300f, EndRedmoon);
        }

        private void EndRedmoon()
        {
            eventActive = false;
            RemoveAllZombies(); // Despawn zombies when night ends
            ResetEnvironment();
            PrintToChat("<color=#ff0000>☀ The Redmoon fades. You survived... for now.</color>");
            PostLeaderboardToDiscord();
        }

        private void TrySpawnBoss()
        {
            var monuments = TerrainMeta.Path.Monuments
                .Where(m => Vector3.Distance(m.transform.position, Vector3.zero) > 10 && !IsNearSafeZone(m.transform.position, 100f))
                .ToList();

            if (monuments.Count == 0 || UnityEngine.Random.value > config.BossSpawnChance) return;

            var selected = monuments.GetRandom();
            Vector3 pos = selected.transform.position + new Vector3(5, 0, 5);
            pos.y = TerrainMeta.HeightMap.GetHeight(pos);

            bossZombie = GameManager.server.CreateEntity("assets/rust.ai/agents/npcplayerhuman/npcplayerhuman.prefab", pos);
            if (bossZombie == null) return;
            bossZombie.Spawn();

            var npc = bossZombie.GetComponent<BaseCombatEntity>();
            npc.health = config.BossHealth;

            var component = npc.gameObject.AddComponent<RedmoonBoss>();
            component.Init(this);

            npc.transform.localScale *= 1.3f;

            var light = bossZombie.gameObject.AddComponent<Light>();
            light.color = Color.red;
            light.intensity = 4f;

            PvPvE?.Call("AddTemporaryPvPZone", pos, config.BossPvPRadius, config.BossPvPDuration);

            PrintToChat("<color=#ff0000>☠ A Boss Zombie has spawned! Hunt it down!</color>");
        }

        private void RemoveAllAnimals()
        {
            int removed = 0;
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                if (entity is BaseAnimalNPC animal)
                {
                    animal.Kill();  // Remove the animal from the world
                    removed++;
                }
            }
            // Print a message informing players that wildlife is leaving the area
            PrintToChat("<color=#ff0000>🐾 Wildlife is fleeing in terror as the Redmoon rises!</color>");
            Puts($"Redmoon: Removed {removed} animals from the world.");
        }

        private string GetRandomZombieName()
        {
            string[] names = { "Walker", "Crawler", "Infected", "Ghoul", "Rotter", "Feeder" };
            return names.GetRandom();
        }

        public void OnEntityDeath(HitInfo info)
        {
            if (info?.InitiatorPlayer != null)
                AddPoints(info.InitiatorPlayer.userID, 1);

            OnZombieKilled();
            DropZombieLoot(info.HitEntity.transform.position, false);
            
            // Check if a boss zombie was killed and drop boss loot
            if (info.HitEntity == bossZombie)
            {
                DropBossLoot(info.HitEntity.transform.position); // Drop special boss loot
            }
        }

        private void PostLeaderboardToDiscord()
        {
            if (Client == null || string.IsNullOrEmpty(config.DiscordChannelId) || _guildId == default) return;

            var sorted = data.PlayerPoints.OrderByDescending(kv => kv.Value).Take(10);
            string leaderboard = "🧨 **Redmoon Stats Leaderboard** 🧨\n";

            int i = 1;
            foreach (var entry in sorted)
            {
                string name = covalence.Players.FindPlayerById(entry.Key.ToString())?.Name ?? $"Unknown ({entry.Key})";
                leaderboard += $"{i++}. **{name}** - {entry.Value} pts\n";
            }

            Client.Bot?.GetChannel(_guildId, new Snowflake(ulong.Parse(config.DiscordChannelId)))?.CreateMessage(Client, new MessageCreate
            {
                Content = leaderboard
            });
        }

        private void AddPoints(ulong playerId, int points)
        {
            if (!data.PlayerPoints.ContainsKey(playerId))
                data.PlayerPoints[playerId] = 0;

            data.PlayerPoints[playerId] += points;
        }

        private void ResetEnvironment()
        {
            var sky = TOD_Sky.Instance;
            if (sky == null) return;
            sky.AmbientColor = Color.white;
            sky.Atmosphere.Fogginess = 0.2f;
            RenderSettings.fog = false;
            RenderSettings.fogDensity = 0.005f;
        }

        private void SetRedmoonEffects()
        {
            var sky = TOD_Sky.Instance;
            if (sky == null) return;
            sky.AmbientColor = Color.red * 0.6f;
            sky.Atmosphere.Fogginess = 0.5f;
            RenderSettings.fog = true;
            RenderSettings.fogColor = new Color(0.4f, 0f, 0f);
            RenderSettings.fogDensity = 0.015f;
        }

private bool IsNearSafeZone(Vector3 position, float range)
{
    // Define a list of safe zones (such as Outpost, Bandit Camp, etc.)
    var safeZones = new List<string> { "Outpost", "Bandit Camp" };

    // Loop through all the monuments
    foreach (var monument in TerrainMeta.Path.Monuments)
    {
        if (safeZones.Contains(monument.displayPhrase.english)) // Check if this monument is a safe zone
        {
            // Check if the position is within the defined range of the safe zone
            if (Vector3.Distance(position, monument.transform.position) <= range)
                return true;
        }
    }

    return false; // Return false if no safe zone is nearby
}

        private class RedmoonBoss : MonoBehaviour
        {
            private BaseCombatEntity entity;
            private Redmoon plugin;

            // Initialize the Boss behavior
            public void Init(Redmoon plugin)
            {
                this.plugin = plugin;
                entity = GetComponent<BaseCombatEntity>();

                // Add red light to make the boss visually intimidating
                var light = entity.gameObject.AddComponent<Light>();
                light.color = Color.red;
                light.intensity = 4f;
                light.range = 10f;
            }

            // Called when the boss zombie dies
            public void OnEntityDeath(HitInfo info)
            {
                if (info?.InitiatorPlayer != null)
                {
                    plugin.AddPoints(info.InitiatorPlayer.userID, 5); // Give extra points for killing the boss
                    plugin.DropBossLoot(info.HitEntity.transform.position); // Drop special boss loot
                }
            }
        }

        private void RemoveAllZombies()
        {
            int removed = 0;
            foreach (var zombie in spawnedZombies)
            {
                if (zombie != null)
                {
                    zombie.Kill(); // Kill each zombie entity
                    removed++;
                }
            }
            spawnedZombies.Clear(); // Clear the list after removing the zombies
            Puts($"Redmoon: Removed {removed} zombies from the world.");
        }

        private void OnZombieKilled()
        {
            zombiesKilledThisWave++; // Increment the count of zombies killed in this wave
            
            // Check if the number of zombies killed reaches the threshold for spawning the next wave
            if (zombiesKilledThisWave >= waveThreshold)
            {
                waveMultiplier++;  // Increase the difficulty by spawning more zombies in the next wave
                zombiesKilledThisWave = 0; // Reset the kill count for the next wave
                PrintToChat("<color=#ff0000>⚡ A new wave of zombies is arriving!</color>");
            }
        }

        private void DropZombieLoot(Vector3 position, bool isBoss = false)
        {
            var lootItems = new List<string>
            {
                "bone.fragments", "skull.human", "fat.animal", "cloth", "metal.fragments", "stones"
            };

            int count = UnityEngine.Random.Range(config.MinLoot, config.MaxLoot + 1);
            for (int i = 0; i < count; i++)
            {
                string item = lootItems.GetRandom();
                int amount = UnityEngine.Random.Range(2, 10) * (isBoss ? config.BossLootMultiplier : 1);

                // Create the item using ItemManager
                Item itemToDrop = ItemManager.Create(ItemManager.FindItemDefinition(item), amount);

                if (itemToDrop != null)
                {
                    itemToDrop.Drop(position, Vector3.up); // Drop the item at the specified position
                }
            }
        }

        private void DropBossLoot(Vector3 position)
        {
            var lootItems = new List<string>
            {
                "bone.fragments", "skull.human", "fat.animal", "cloth", "metal.fragments", "stones",
                "rifle.body", "explosives", "shotgun.pump", "medkit" // Special boss loot
            };

            int count = UnityEngine.Random.Range(config.MinLoot, config.MaxLoot + 1) * config.BossLootMultiplier;
            for (int i = 0; i < count; i++)
            {
                string item = lootItems.GetRandom();
                int amount = UnityEngine.Random.Range(2, 10);

                // Create the item using ItemManager
                Item itemToDrop = ItemManager.Create(ItemManager.FindItemDefinition(item), amount);

                if (itemToDrop != null)
                {
                    itemToDrop.Drop(position, Vector3.up); // Drop the item at the specified position
                }
            }

            // Drop the hackable crate
            DropHackableCrate(position);
        }

        private void DropHackableCrate(Vector3 position)
        {
            // Define the prefab for the hackable crate
            string cratePrefab = "assets/rust.ai/agents/loot_crate/loot_crate.prefab";

            // Create the crate entity at the specified position
            BaseEntity crateEntity = GameManager.server.CreateEntity(cratePrefab, position);
            if (crateEntity != null)
            {
                crateEntity.Spawn();  // Spawn the crate at the position
                Puts("Dropped a hackable crate as part of the boss loot.");
            }
        }

private void SpawnZombie(Vector3 pos)
{
    // Define the prefab for the zombie (you can adjust this if you use a custom zombie prefab)
    string zombiePrefab = "assets/rust.ai/agents/zombie/zombie.prefab";  // Default zombie prefab

    // Create the zombie entity at the specified position
    BaseEntity zombie = GameManager.server.CreateEntity(zombiePrefab, pos);
    
    if (zombie != null)
    {
        zombie.Spawn();  // Spawn the zombie at the position
        spawnedZombies.Add(zombie); // Add the zombie to the list of spawned zombies
        Puts($"Spawned a zombie at position: {pos}");
    }
}

private void SpawnZombies()
{
    foreach (var player in BasePlayer.activePlayerList)
    {
        int minutesPlayed = Mathf.FloorToInt((float)(player.net?.connection?.GetSecondsConnected() ?? 0) / 60f);
        float playtimeFactor = Mathf.Clamp01(minutesPlayed / (float)config.MaxPlaytimeMinutes);
        int zombieCount = Mathf.RoundToInt(Mathf.Lerp(config.ZombiesPerPlayer, config.MaxZombiesPerPlayer, playtimeFactor) * waveMultiplier);

        for (int i = 0; i < zombieCount; i++)
        {
            // Calculate the position for zombie spawn
            Vector3 pos = player.transform.position + UnityEngine.Random.insideUnitSphere * config.ZombieSpawnRadius;
            pos.y = TerrainMeta.HeightMap.GetHeight(pos); // Adjust height based on terrain

            // Prevent zombies from spawning too close to safe zones
            if (IsNearSafeZone(pos, 80f)) continue;

            // Correctly pass the calculated position to SpawnZombie
            SpawnZombie(pos);  // This is where the `pos` argument is passed
        }
    }
}

    }
}
