using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;
using Random = UnityEngine.Random;
using Physics = UnityEngine.Physics;
using Time = UnityEngine.Time;

using Oxide.Core;
using Oxide.Core.Configuration;

using ConVar;

using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("Night Zombies", "0x89A", "3.2.6")]
    [Description("Spawns and kills zombies at set times")]
    class NightZombies : RustPlugin
    {
        private static NightZombies _instance;
        private Configuration _config;
        private DynamicConfigFile _dataFile;
        
        private SpawnController _spawnController;

        #region -Init-

        void Init()
        {
            _instance = this;
            
            //Read saved number of days since last spawn
            _dataFile = Interface.Oxide.DataFileSystem.GetFile("NightZombies-daysSinceSpawn");
            int i = 0;
            
            try
            {
                i = _dataFile.ReadObject<int>();
            }
            catch //Default to 0 if error reading or data broken
            {
                PrintWarning("Failed to load saved days since last spawn, defaulting to 0");
                i = 0;
            }

            _spawnController = new SpawnController(i, _config);
        }

        //Start time check
        void OnServerInitialized()
        {
            timer.Once(5f, () =>
            {
                TOD_Sky.Instance.Components.Time.OnMinute += _spawnController.TimeTick;
                TOD_Sky.Instance.Components.Time.OnDay += () => _spawnController.daysSinceLastSpawn++;
            });
        }

        void Unload()
        {
            TOD_Sky.Instance.Components.Time.OnMinute -= _spawnController.TimeTick;
            TOD_Sky.Instance.Components.Time.OnDay -= () => _spawnController.daysSinceLastSpawn++;

            _dataFile.WriteObject(_spawnController.daysSinceLastSpawn);

            _spawnController?.Shutdown();
        }

        #endregion

        #region -Oxide Hooks-

        object OnNpcTarget(ScarecrowNPC npc, BaseEntity target)
        {
            return CanAttack(target);
        }

        object OnTurretTarget(NPCAutoTurret turret, ScarecrowNPC entity)
        {
            return !_config.Behaviour.sentriesAttackZombies ? (object)true : null;
        }

        private object OnPlayerDeath(ScarecrowNPC entity, HitInfo info)
        {
            if (_spawnController.spawned)
            {
                Respawn(entity);
                return true;
            }

            return null;
        }

        private void OnEntitySpawned(DroppedItemContainer container)
        {
            if (_config.Destroy.halfBodybagDespawn && (container.lootPanelName == "scarecrow" || container.lootPanelName == "murderer"))
            {
                string methodName = nameof(DroppedItemContainer.RemoveMe);

                container.CancelInvoke(methodName);
                container.Invoke(methodName, container.CalculateRemovalTime() / 2);
            }
        }

        #endregion -Oxide Hooks-

        #region -Helpers-

        private object CanAttack(BaseEntity target)
        {
            for (int i = 0; i < _config.Behaviour.ignored.Count; i++)
                if ((_config.Behaviour.ignoreHumanNPC && HumanNPCCheck(target)) || target.ShortPrefabName == _config.Behaviour.ignored[i]) return true;

            if (_config.Behaviour.ignored.Contains(target.ShortPrefabName) || (!_config.Behaviour.attackSleepers && target is BasePlayer && (target as BasePlayer).IsSleeping())) return true;
            else return null;
        }

        private bool HumanNPCCheck(BaseEntity target)
        {
            BasePlayer player = target as BasePlayer;
            return player != null && !player.userID.IsSteamId() && !(target is ScientistNPC) && !(target is ScarecrowNPC);
        }

        private void Respawn(BasePlayer entity)
        {
            if (entity == null) return;

            _spawnController.Respawn(entity);
        }

        #endregion -Helpers

        private class SpawnController
        {
            private const string prefab = "assets/prefabs/npc/scarecrow/scarecrow.prefab";

            private Configuration.SpawnSettings spawnConfig;
            private Configuration.SpawnSettings.ZombieSettings zombiesConfig;

            private float spawnTime, destroyTime;
            private bool isSpawnTime => spawnTime > destroyTime ? Env.time >= spawnTime || Env.time < destroyTime : Env.time <= spawnTime || Env.time > destroyTime;
            private bool isDestroyTime => spawnTime > destroyTime ? Env.time >= destroyTime && Env.time < spawnTime : Env.time <= destroyTime && Env.time > spawnTime;

            public int daysSinceLastSpawn;
            
            public Timer scarecrowTimer;

            public bool spawned;

            private Dictionary<BaseCombatEntity, NightZombie> zombies = new Dictionary<BaseCombatEntity, NightZombie>();

            public SpawnController(int daysSinceLastSpawn, Configuration config)
            {
                this.daysSinceLastSpawn = daysSinceLastSpawn;

                spawnTime = config.Spawn.spawnTime;
                destroyTime = config.Spawn.destroyTime;

                spawnConfig = config.Spawn;
                zombiesConfig = config.Spawn.Zombies;
            }

            public void SpawnZombies()
            {
                if (spawned) return;

                ServerMgr.Instance.StopCoroutine(RemoveZombies());

                if (zombiesConfig.scarecrowPopulation > 0)
                {
                    scarecrowTimer?.Destroy();
                    scarecrowTimer = _instance.timer.Repeat(0.5f, zombiesConfig.scarecrowPopulation, () =>
                    {
                        if (zombies.Count <= zombiesConfig.scarecrowPopulation)
                        {
                            Spawn();
                        }
                    });
                }

                if (_instance._config.Broadcast.doBroadcast)
                {
                    Broadcast("ChatBroadcast", zombiesConfig.scarecrowPopulation);
                }

                daysSinceLastSpawn = 0;
                spawned = true;
            }

            public IEnumerator RemoveZombies(bool configOverride = false, Action complete = null)
            {
                foreach (BaseCombatEntity entity in zombies.Keys.ToArray())
                {
                    if (entity == null) continue;
                    
                    if (_instance._config.Destroy.leaveCorpse && !configOverride) zombies[entity].CreateCorpse();

                    entity.AdminKill();
                }

                zombies.Clear();
                spawned = false;

                complete?.Invoke();

                yield break;
            }

            public void Respawn(BaseCombatEntity entity)
            {
                NightZombie zombie;
                if (zombies.TryGetValue(entity, out zombie))
                {
                    zombie.OnDeath();
                }
            }
            
            public void TimeTick()
            {
                if (!spawned && CanSpawn()) ServerMgr.Instance.StartCoroutine(RemoveZombies(false, SpawnZombies));
                else if (spawned && isDestroyTime)
                {
                    StopTimers();
                    ServerMgr.Instance.StartCoroutine(RemoveZombies());
                }
            }

            #region -Util-

            private void Spawn()
            {
                if (zombies.Count >= zombiesConfig.scarecrowPopulation) return;

                BasePlayer player;
                Vector3 position = spawnConfig.spawnNearPlayers && BasePlayer.activePlayerList.Count >= spawnConfig.minNearPlayers && GetPlayer(out player) ? GetRandomPositionAroundPlayer(player) : GetRandomPosition();

                BasePlayer entity = GameManager.server.CreateEntity(prefab, position, Quaternion.identity, false) as BasePlayer;

                if (entity)
                {
                    NightZombie zombie = entity.gameObject.AddComponent<NightZombie>();
                    entity.gameObject.AwakeFromInstantiate();
                    entity.Spawn();

                    float health = spawnConfig.Zombies.scarecrowHealth;
                    entity.SetMaxHealth(health);
                    entity.SetHealth(health);

                    zombies.Add(entity, zombie);
                }
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

                    position = new Vector3(x, y + 0.5f, z);

                    if (AntiHack.TestInsideTerrain(position) || IsInObject(position) || IsInOcean(position)) i = 0;
                    else break;
                }

                if (position == Vector3.zero)
                {
                    position.y = TerrainMeta.HeightMap.GetHeight(0, 0);
                }

                return position;
            }

            public Vector3 GetRandomPositionAroundPlayer(BasePlayer player)
            {
                Vector3 playerPos = player.transform.position;
                Vector3 position = default(Vector3);

                float maxDist = spawnConfig.maxDistance;

                int attempts = 0;

                for (int i = 0; i < 2; i++)
                {
                    if (attempts >= 4)
                    {
                        position = GetRandomPosition();
                        break;
                    }

                    position = new Vector3(Random.Range(playerPos.x - maxDist, playerPos.x + maxDist), 0, Random.Range(playerPos.z - maxDist, playerPos.z + maxDist));
                    position.y = TerrainMeta.HeightMap.GetHeight(position);

                    if (AntiHack.TestInsideTerrain(position) || IsInObject(position) || IsInOcean(position) || Vector3.Distance(playerPos, position) < spawnConfig.minDistance)
                    {
                        i = 0;
                        attempts++;
                    }
                    else break;
                }

                if (position == Vector3.zero)
                {
                    position.y = TerrainMeta.HeightMap.GetHeight(0, 0);
                }

                return position;
            } 

            private bool CanSpawn()
            {
                return daysSinceLastSpawn >= spawnConfig.Chance.days && Random.Range(0f, 100f) < spawnConfig.Chance.chance && isSpawnTime;
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

            private void Broadcast(string key, params object[] values)
            {
                try
                {
                    _instance.Server.Broadcast(string.Format(_instance.lang.GetMessage(key, _instance), values));
                }
                catch
                {
                    _instance.Server.Broadcast(values.Length == 1 ? _instance.lang.GetMessage(key, _instance).Replace("{0}", (string)values[0]) : _instance.lang.GetMessage(key, _instance).Replace("{0}", (string)values[0]).Replace("{1}", (string)values[1]));
                }
            }

            private static ItemDefinition FindDefinition(int id) => ItemManager.FindItemDefinition(id);

            #endregion

            private void StopTimers()
            {
                scarecrowTimer?.Destroy();
            }

            public void Shutdown()
            {
                StopTimers();

                ServerMgr.Instance.StartCoroutine(RemoveZombies(true));
            }
        }
        
        private class NightZombie : FacepunchBehaviour
        {
            private const string _corpsePrefab = "assets/rust.ai/agents/npcplayer/pet/frankensteinpet_corpse.prefab";
        
            public float LastSpawnTime { get; private set; }
            private ScarecrowNPC _scarecrow;
            private LootContainer.LootSpawnSlot[] _loot;

            private void Awake()
            {
                _scarecrow = GetComponent<ScarecrowNPC>();
                _loot = _scarecrow.LootSpawnSlots;
                
                InvokeRepeating(AttackTick, 0f, 0.5f);
            }

            private void AttackTick()
            {
                BaseEntity entity = _scarecrow.Brain.Senses.GetNearestTarget(100);
                
                if (entity != null && _scarecrow.CanAttack(entity) && Vector3.Distance(entity.transform.position, _scarecrow.transform.position) < 1.5f)
                {
                    _scarecrow.StartAttacking(entity);
                }
            }

            private void Respawn()
            {
                if (_instance._config.Destroy.leaveCorpseKilled) 
                {
                    CreateCorpse();
                }
                
                //Returns to place of death if not deactivated
                _scarecrow.gameObject.SetActive(false);

                BasePlayer player;

                Vector3 position = _instance._config.Spawn.spawnNearPlayers && _instance._spawnController.GetPlayer(out player)
                    ? _instance._spawnController.GetRandomPositionAroundPlayer(player)
                    : _instance._spawnController.GetRandomPosition();

                if (position == Vector3.zero)
                {
                    _scarecrow.AdminKill();
                    return;
                }
                
                _scarecrow.Teleport(position);
                _scarecrow.gameObject.SetActive(true);

                _scarecrow.Heal(_instance._config.Spawn.Zombies.scarecrowHealth);
                _scarecrow.SendNetworkUpdateImmediate();
            }

            public void OnDeath()
            {
                if (Time.time - LastSpawnTime < 0.5f)
                {
                    _scarecrow.AdminKill();
                    return;
                }
                
                if (_scarecrow == null || _scarecrow.IsDead())
                {
                    return;
                }

                Respawn();
                
                LastSpawnTime = Time.time;
            }

            public void CreateCorpse()
            {
                NPCPlayerCorpse corpse = GameManager.server.CreateEntity(_corpsePrefab) as NPCPlayerCorpse;

                if (corpse)
                {
                    corpse.InitCorpse(_scarecrow);

                    corpse.SetLootableIn(2f);
                    corpse.SetFlag(BaseEntity.Flags.Reserved5, _scarecrow.HasPlayerFlag(BasePlayer.PlayerFlags.DisplaySash));
                    corpse.SetFlag(BaseEntity.Flags.Reserved2, true);

                    ItemContainer[] inventory = new ItemContainer[3]
                    {
                        _scarecrow.inventory.containerMain,
                        _scarecrow.inventory.containerWear,
                        _scarecrow.inventory.containerBelt
                    };

                    corpse.containers = new ItemContainer[3];
                    for (int i = 0; i < 3; i++)
                    {
                        ItemContainer container = corpse.containers[i] = new ItemContainer();
                        ItemContainer invContainer = inventory[i];

                        container.ServerInitialize(null, invContainer.capacity);
                        container.GiveUID();
                        container.entityOwner = _scarecrow;

                        foreach (Item item in invContainer.itemList.ToArray())
                        {
                            if (item.info.shortname == "gloweyes") continue;

                            Item item2 = ItemManager.CreateByItemID(item.info.itemid, item.amount, item.skin);

                            if (!item2.MoveToContainer(container))
                                item2.DropAndTossUpwards(_scarecrow.transform.position, 2f);
                        }
                    }

                    corpse.playerName = _scarecrow.displayName;
                    corpse.playerSteamID = _scarecrow.userID;

                    corpse.Spawn();

                    //Spawn loot
                    ItemContainer[] containers = corpse.containers;

                    //Clear inventory
                    for (int i = 0; i < containers.Length; i++)
                        containers[i].Clear();

                    //Populate containers
                    if (_loot.Length > 0)
                    {
                        for (int i = 0; i < _loot.Length; i++)
                        {
                            LootContainer.LootSpawnSlot slot = _loot[i];

                            for (int x = 0; x < slot.numberToSpawn; x++)
                            {
                                if (Random.Range(0f, 1f) <= slot.probability)
                                    slot.definition.SpawnIntoContainer(corpse.containers[0]);
                            }
                        }
                    }
                }
            }

            private void OnDestroy()
            {
                CancelInvoke(nameof(CreateCorpse));
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
                public bool spawnNearPlayers = false;

                [JsonProperty("Min pop for near player spawn")]
                public int minNearPlayers = 10;

                [JsonProperty("Min distance from player")]
                public float minDistance = 40;

                [JsonProperty("Max distance from player")]
                public float maxDistance = 80f;

                [JsonProperty("Spawn Time")]
                public float spawnTime = 19.8f;

                [JsonProperty("Destroy Time")]
                public float destroyTime = 7.3f;

                [JsonProperty("Zombie Settings")]
                public ZombieSettings Zombies = new ZombieSettings();

                public class ZombieSettings
                {
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
                public bool leaveCorpse = false;

                [JsonProperty("Leave Corpse, when killed by player")]
                public bool leaveCorpseKilled = true;

                [JsonProperty("Half bodybag despawn time")]
                public bool halfBodybagDespawn = true;

                [JsonProperty("Quick destroy corpses")]
                public bool quickDestroyCorpse = true;
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
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();
                if (_config.Spawn.spawnTime > 24 || _config.Spawn.spawnTime < 0)
                {
                    PrintWarning("Invalid spawn time (must be in 24 hour time)");
                    _config.Spawn.spawnTime = 19.5f;
                }
                if (_config.Spawn.destroyTime > 24 || _config.Spawn.destroyTime < 0)
                {
                    PrintWarning("Invalid destroy time (must be in 24 hour time)");
                    _config.Spawn.destroyTime = 7f;
                }
                SaveConfig();
            }
            catch
            {
                PrintError("Failed to load _config, using default values");
                LoadDefaultConfig();
            }
        }

        protected override void LoadDefaultConfig() => _config = new Configuration
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

        protected override void SaveConfig() => Config.WriteObject(_config);

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
