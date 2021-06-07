using System;
using System.Collections;
using System.Collections.Generic;

using UnityEngine;
using Random = UnityEngine.Random;
using Physics = UnityEngine.Physics;

using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Configuration;

using Rust.Ai.HTN.Murderer;

using ConVar;

using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("Night Zombies", "0x89A", "3.0.0")]
    [Description("Spawns and kills zombies at set times")]
    class NightZombies : RustPlugin
    {
        private Configuration config;
        private DynamicConfigFile dataFile;

        [PluginReference] Plugin ClothedMurderers, PlaguedMurderers;

        private SpawnController spawnController;

        #region -Init-

        void Init()
        {
            //Read saved number of days since last spawn
            dataFile = Interface.Oxide.DataFileSystem.GetFile("NightZombies-daysSinceSpawn");
            int i = 0;
            
            try
            {
                i = dataFile.ReadObject<int>();
            }
            catch //Default to 0 if error reading or data broken
            {
                PrintWarning("Failed to load saved days since last spawn, defaulting to 0");
                i = 0;
            }

            spawnController = new SpawnController(i, config, this);
        }

        //Start time check
        void OnServerInitialized()
        {
            TOD_Sky.Instance.Components.Time.OnMinute += spawnController.TimeTick;
        }

        void Unload()
        {
            TOD_Sky.Instance.Components.Time.OnMinute -= spawnController.TimeTick;

            spawnController?.Shutdown();
        }

        #endregion

        #region -Oxide Hooks-

        object OnNpcTarget(HTNPlayer npc, BaseEntity target)
        {
            return npc.ShortPrefabName == "scarecrow" ? CanAttack(target) : null;
        }

        object OnNpcTarget(NPCMurderer npc, BaseEntity target)
        {
            return CanAttack(target);
        }

        object OnTurretTarget(NPCAutoTurret turret, BaseCombatEntity entity)
        {
            if ((entity?.ShortPrefabName == "scarecrow" || entity is NPCMurderer) && !config.Behaviour.sentriesAttackZombies) return true;
            return null;
        }

        private object OnPlayerDeath(HTNPlayer entity, HitInfo info)
        {
            if (spawnController.spawned && entity.ShortPrefabName == "scarecrow")
            {
                if (entity.AiDefinition is MurdererDefinition)
                {
                    MurdererDefinition def = entity.AiDefinition as MurdererDefinition;

                    if (def.DeathEffect.isValid) 
                        Effect.server.Run(def.DeathEffect.resourcePath, entity.transform.position);
                }

                Respawn(entity);
                return true;
            }

            return null;
        }

        private object OnPlayerDeath(NPCMurderer entity, HitInfo info)
        {
            if (spawnController.spawned)
            {
                if (entity.DeathEffect.isValid) Effect.server.Run(entity.DeathEffect.resourcePath, entity.transform.position);

                Respawn(entity);
                return true;
            }

            return null;
        }

        #endregion -Oxide Hooks-

        #region -Helpers-

        private object CanAttack(BaseEntity target)
        {
            for (int i = 0; i < config.Behaviour.ignored.Count; i++)
                if ((config.Behaviour.ignoreHumanNPC && HumanNPCCheck(target)) || target.ShortPrefabName == config.Behaviour.ignored[i]) return true;

            if (config.Behaviour.ignored.Contains(target.ShortPrefabName) || (!config.Behaviour.attackSleepers && target is BasePlayer && (target as BasePlayer).IsSleeping())) return true;
            else return null;
        }

        private bool HumanNPCCheck(BaseEntity target)
        {
            BasePlayer player = target as BasePlayer;
            return player != null && !player.userID.IsSteamId() && !(target is Scientist) && !(target is HTNPlayer);
        }

        private void Respawn(BasePlayer entity)
        {
            if (entity == null) return;
            
            if (config.Destroy.leaveCorpseKilled) CreateCorpse(entity);

            //Returns to place of death if not deactivated
            entity.gameObject.SetActive(false);

            BasePlayer player;
            entity.transform.position = config.Spawn.spawnNearPlayers && spawnController.GetPlayer(out player) ? spawnController.GetRandomPositionAroundPlayer(player) : spawnController.GetRandomPosition();
            entity.gameObject.SetActive(true);

            entity.Heal(entity is NPCMurderer ? config.Spawn.Zombies.murdererHealth : config.Spawn.Zombies.scarecrowHealth);
            entity.SendNetworkUpdateImmediate();
        }

        //Basically just the code from LootableCorpse.TakeFrom, but modified slightly
        //Required because corspse is spawned after death, zombies are not killed instead recycled
        //Calling CreateCorpse on entity results in weird behaviour
        private void CreateCorpse(BasePlayer zombie)
        {
            NPCPlayerCorpse corpse = GameManager.server.CreateEntity("assets/prefabs/npc/murderer/murderer_corpse.prefab") as NPCPlayerCorpse;
            corpse.InitCorpse(zombie);

            corpse.SetLootableIn(2f);
            corpse.SetFlag(BaseEntity.Flags.Reserved5, zombie.HasPlayerFlag(BasePlayer.PlayerFlags.DisplaySash));
            corpse.SetFlag(BaseEntity.Flags.Reserved2, true);

            ItemContainer[] inventory = new ItemContainer[3]
            {
                zombie.inventory.containerMain,
                zombie.inventory.containerWear,
                zombie.inventory.containerBelt
            };

            if (corpse)
            {
                corpse.containers = new ItemContainer[3];
                for (int i = 0; i < 3; i++)
                {
                    ItemContainer container = corpse.containers[i] = new ItemContainer();
                    ItemContainer invContainer = inventory[i];

                    container.ServerInitialize(null, invContainer.capacity);
                    container.GiveUID();
                    container.entityOwner = zombie;

                    foreach (Item item in invContainer.itemList.ToArray())
                    {
                        if (item.info.shortname == "gloweyes") continue;

                        Item item2 = ItemManager.CreateByItemID(item.info.itemid, item.amount, item.skin);

                        if (!item2.MoveToContainer(container)) 
                            item2.DropAndTossUpwards(zombie.transform.position, 2f);
                    }
                }

                corpse.playerName = zombie.displayName;
                corpse.playerSteamID = zombie.userID;

                corpse.Spawn();

                SpawnLoot(zombie, corpse);
            }
        }

        private void SpawnLoot(BasePlayer player, NPCPlayerCorpse corpse)
        {
            LootContainer.LootSpawnSlot[] lootSlots = null;
            ItemContainer[] containers = corpse.containers;

            if (player is NPCMurderer) lootSlots = (player as NPCMurderer).LootSpawnSlots;
            else if (player is HTNPlayer) lootSlots = ((player as HTNPlayer)._aiDefinition as MurdererDefinition).Loot;

            for (int i = 0; i < containers.Length; i++)
                containers[i].Clear();

            if (lootSlots != null && lootSlots.Length > 0)
            {
                for (int i = 0; i < lootSlots.Length; i++)
                {
                    LootContainer.LootSpawnSlot slot = lootSlots[i];

                    for (int x = 0; x < slot.numberToSpawn; x++)
                    {
                        if (Random.Range(0f, 1f) <= slot.probability)
                            slot.definition.SpawnIntoContainer(corpse.containers[0]);
                    }
                }
            }
        }

        #endregion -Helpers

        private class SpawnController
        {
            private NightZombies instance;

            private const string murdererPrefab = "assets/prefabs/npc/murderer/murderer.prefab";
            private const string scarecrowPrefab = "assets/prefabs/npc/scarecrow/scarecrow.prefab";

            private List<Tuple<ItemDefinition, ulong>> murdererItems = new List<Tuple<ItemDefinition, ulong>>
            {
                new Tuple<ItemDefinition, ulong>(FindDefinition(1877339384), 807624505),
                new Tuple<ItemDefinition, ulong>(FindDefinition(-690276911), 0),
                new Tuple<ItemDefinition, ulong>(FindDefinition(223891266), 795997221),
                new Tuple<ItemDefinition, ulong>(FindDefinition(1366282552), 1132774091),
                new Tuple<ItemDefinition, ulong>(FindDefinition(1992974553), 806966575),
                new Tuple<ItemDefinition, ulong>(FindDefinition(-1549739227), 0)
            };

            private int daysSinceLastSpawn = 0, murdererCount, scarecrowCount, minPlayerPop;
            private float murdererHealth, scarecrowHealth, chance, days, minDist, maxDist, spawnTime, destroyTime;
            private bool leaveCorpse, doBroadcast, broadcastSeparate, nearPlayers;
            private bool isSpawnTime => spawnTime > destroyTime ? Env.time >= spawnTime || Env.time < destroyTime : Env.time <= spawnTime || Env.time > destroyTime;
            private bool isDestroyTime => spawnTime > destroyTime ? Env.time >= destroyTime && Env.time < spawnTime : Env.time <= destroyTime && Env.time > spawnTime;

            public Timer murdererTimer;
            public Timer scarecrowTimer;

            public bool spawned;

            private List<BaseCombatEntity> zombies = new List<BaseCombatEntity>();

            public SpawnController(int daysSinceLastSpawn, Configuration config, NightZombies instance)
            {
                this.instance = instance;

                this.daysSinceLastSpawn = daysSinceLastSpawn;
                chance = config.Spawn.Chance.chance;
                days = config.Spawn.Chance.days;
                leaveCorpse = config.Destroy.leaveCorpse;

                murdererCount = config.Spawn.Zombies.murdererPoluation;
                scarecrowCount = config.Spawn.Zombies.scarecrowPopulation;
                murdererHealth = config.Spawn.Zombies.murdererHealth;
                scarecrowHealth = config.Spawn.Zombies.scarecrowHealth;
                minPlayerPop = config.Spawn.spawnNearPlayersPop;
                nearPlayers = config.Spawn.spawnNearPlayers;

                maxDist = config.Spawn.maxDistance;
                minDist = config.Spawn.minDistance;

                spawnTime = config.Spawn.spawnTime;
                destroyTime = config.Spawn.destroyTime;

                doBroadcast = config.Broadcast.doBroadcast;
                broadcastSeparate = config.Broadcast.broadcastSeparate;
            }

            public IEnumerator SpawnZombies()
            {
                if (murdererCount > 0)
                {
                    murdererTimer = instance.timer.Repeat(0.15f, murdererCount, () =>
                    {
                        Spawn(murdererPrefab, murdererHealth, true);
                    });
                }
                
                if (scarecrowCount > 0)
                {
                    scarecrowTimer = instance.timer.Repeat(0.15f, scarecrowCount, () =>
                    {
                        Spawn(scarecrowPrefab, scarecrowHealth, false);
                    });
                }

                if (!spawned && doBroadcast)
                {
                    if (broadcastSeparate) Broadcast("ChatBroadcastSeparate", murdererCount, scarecrowCount);
                    else Broadcast("ChatBroadcast", murdererCount + scarecrowCount);
                }

                spawned = true;

                yield return null;
            }

            public IEnumerator RemoveZombies(bool configOverride = false)
            {
                ServerMgr.Instance.StopCoroutine(SpawnZombies());

                BaseCombatEntity[] array = zombies.ToArray();

                for (int i = 0; i < zombies.Count; i++)
                {
                    BaseCombatEntity entity = array[i];
                    if (entity == null) continue;

                    if (leaveCorpse && !configOverride) entity.DieInstantly();
                    else entity.AdminKill();
                }

                spawned = false;

                zombies.Clear();

                yield break;
            }

            public void TimeTick()
            {
                if (!spawned && CanSpawn())
                {
                    ServerMgr.Instance.StopCoroutine(RemoveZombies());
                    ServerMgr.Instance.StartCoroutine(SpawnZombies());
                }
                else if (isDestroyTime)
                {
                    ServerMgr.Instance.StopCoroutine(SpawnZombies());
                    ServerMgr.Instance.StartCoroutine(RemoveZombies());
                }
            }

            #region -Util-

            private BaseCombatEntity Spawn(string prefab, float health, bool murderer)
            {
                BasePlayer player;
                Vector3 position = nearPlayers && (minPlayerPop <= 0 || BasePlayer.activePlayerList.Count >= minPlayerPop) && GetPlayer(out player) ? GetRandomPositionAroundPlayer(player) : GetRandomPosition();

                BasePlayer entity = GameManager.server.CreateEntity(prefab, position, Quaternion.identity, false) as BasePlayer;

                if (entity)
                {
                    entity.gameObject.AwakeFromInstantiate();
                    entity.Spawn();
                    entity.SetMaxHealth(health);
                    entity.SetHealth(health);

                    if (murderer && (!instance.ClothedMurderers || !instance.ClothedMurderers.IsLoaded) && (!instance.PlaguedMurderers || !instance.PlaguedMurderers.IsLoaded))
                    {
                        foreach (Tuple<ItemDefinition, ulong> tuple in murdererItems)
                            entity.inventory.containerWear.AddItem(tuple.Item1, 1, tuple.Item2);
                    }

                    zombies.Add(entity);

                    return entity;
                }

                return null;
            }

            public bool GetPlayer(out BasePlayer player)
            {
                player = BasePlayer.activePlayerList[Random.Range(0, BasePlayer.activePlayerList.Count)];

                return player;
            }

            public Vector3 GetRandomPosition()
            {
                float x, y, z;
                Vector3 position = Vector3.zero;

                for (int i = 0; i < 2; i++)
                {
                    x = Random.Range(-TerrainMeta.Size.x / 2, TerrainMeta.Size.x / 2);
                    z = Random.Range(-TerrainMeta.Size.z / 2, TerrainMeta.Size.z / 2);
                    y = TerrainMeta.HeightMap.GetHeight(new Vector3(x, 0, z));

                    position = new Vector3(x, y, z);

                    if (IsInObject(position) || IsInOcean(position)) i = 0;
                    else break;
                }

                return position;
            }

            public Vector3 GetRandomPositionAroundPlayer(BasePlayer player)
            {
                Vector3 playerPos = player.transform.position;
                Vector3 position = default(Vector3);

                for (int i = 0; i < 2; i++)
                {
                    float x = Random.Range(playerPos.x - maxDist, playerPos.x + maxDist);
                    float z = Random.Range(playerPos.z - maxDist, playerPos.z + maxDist);

                    position = new Vector3(x, 0, z);
                    position.y = TerrainMeta.HeightMap.GetHeight(position);

                    if (IsInObject(position) || IsInOcean(position) || Vector3.Distance(playerPos, position) < minDist) i = 0;
                    else break;
                }

                return position;
            } 

            private bool CanSpawn()
            {
                return daysSinceLastSpawn >= days && Random.Range(0f, 100f) < chance && isSpawnTime;
            }

            private bool IsInObject(Vector3 position)
            {
                int layerMask = LayerMask.GetMask("Default", "Tree", "Construction", "World", "Vehicle_Detailed", "Deployed");

                return Physics.OverlapSphere(position, 0.5f, layerMask).Length > 0;
            }

            private bool IsInOcean(Vector3 position)
            {
                return WaterLevel.GetWaterDepth(position) > 0.25f;
            }

            public void SetMurdererCount(int count) => murdererCount = count;

            public void SetScarecrowCount(int count) => scarecrowCount = count;

            private void Broadcast(string key, params object[] values)
            {
                instance.Server.Broadcast(string.Format(instance.lang.GetMessage(key, instance), values));
            }

            private static ItemDefinition FindDefinition(int id) => ItemManager.FindItemDefinition(id);

            #endregion

            public void Shutdown()
            {
                ServerMgr.Instance.StopCoroutine(SpawnZombies());

                murdererTimer?.Destroy();
                scarecrowTimer?.Destroy();

                ServerMgr.Instance.StartCoroutine(RemoveZombies(true));
            }
        }

        #region -Configuration-

        class Configuration
        {
            [JsonProperty("Spawn Settings")]
            public SpawnSettings Spawn = new SpawnSettings();

            [JsonProperty("Destroy Settings")]
            public DestroySettings Destroy = new DestroySettings();

            [JsonProperty("Behaviour Settings")]
            public BehaviourSettings Behaviour = new BehaviourSettings();

            [JsonProperty("Broadcast Settings")]
            public ChatSettings Broadcast = new ChatSettings();

            public class SpawnSettings
            {
                [JsonProperty("Spawn near players")]
                public bool spawnNearPlayers = true;

                [JsonProperty("Minimum pop for near player spawn")]
                public int spawnNearPlayersPop = 0;

                [JsonProperty("Min distance from player")]
                public float minDistance = 30;

                [JsonProperty("Max distance from player")]
                public float maxDistance = 60f;

                [JsonProperty("Spawn Time")]
                public float spawnTime = 19.8f;

                [JsonProperty("Destroy Time")]
                public float destroyTime = 7.3f;

                [JsonProperty("Zombie Settings")]
                public ZombieSettings Zombies = new ZombieSettings();

                public class ZombieSettings
                {
                    [JsonProperty("Murderer Population (total amount)")]
                    public int murdererPoluation = 50;

                    [JsonProperty("Murderer Health")]
                    public float murdererHealth = 100f;

                    [JsonProperty("Scarecrow Population (total amount)")]
                    public int scarecrowPopulation = 50;

                    [JsonProperty("Scarecrow Health")]
                    public float scarecrowHealth = 200f;
                }

                [JsonProperty("Chance Settings")]
                public ChanceSetings Chance = new ChanceSetings();

                public class ChanceSetings
                {
                    [JsonProperty("Chance per cycle")]
                    public float chance = 100f;

                    [JsonProperty("Days betewen spawn")]
                    public int days = 0;
                }
            }

            public class DestroySettings
            {
                [JsonProperty("Leave Corpse, when destroyed")]
                public bool leaveCorpse = true;

                [JsonProperty("Leave Corpse, when killed by player")]
                public bool leaveCorpseKilled = true;
            }

            public class BehaviourSettings
            {
                [JsonProperty("Attack sleeping players")]
                public bool attackSleepers = false;

                [JsonProperty("Zombies attacked by outpost sentries")]
                public bool sentriesAttackZombies = true;

                [JsonProperty("Ignore Human NPCs")]
                public bool ignoreHumanNPC = true;

                [JsonProperty("Ignored entities (full entity shortname)")]
                public List<string> ignored = new List<string>();
            }

            public class ChatSettings
            {
                [JsonProperty("Broadcast spawn amount")]
                public bool doBroadcast = false;

                [JsonProperty("Broadcast types separately")]
                public bool broadcastSeparate = false;
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception();
                if (config.Spawn.spawnTime > 24 || config.Spawn.spawnTime < 0)
                {
                    PrintWarning("Invalid spawn time (must be in 24 hour time)");
                    config.Spawn.spawnTime = 19.5f;
                }
                if (config.Spawn.destroyTime > 24 || config.Spawn.destroyTime < 0)
                {
                    PrintWarning("Invalid destroy time (must be in 24 hour time)");
                    config.Spawn.destroyTime = 7f;
                }
                SaveConfig();
            }
            catch
            {
                PrintError("Failed to load config, using default values");
                LoadDefaultConfig();
            }
        }

        protected override void LoadDefaultConfig() => config = new Configuration
        {
            Behaviour = new Configuration.BehaviourSettings
            {
                ignored = new List<string>
                {
                    "scientistjunkpile.prefab",
                    "scarecrow.prefab"
                }
            }
        };

        protected override void SaveConfig() => Config.WriteObject(config);

        #endregion

        #region -Localisation-

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["ChatBroadcast"] = "[Night Zombies] Spawned {0} zombies",
                ["ChatBroadcastSeparate"] = "[Night Zombies] Spawned {0} murderers | Spawned {1} scarecrows"
            }, this);
        }

        #endregion
    }
}
