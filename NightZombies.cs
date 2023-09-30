using System;
using System.Collections;
using System.Collections.Generic;

using UnityEngine;
using Random = UnityEngine.Random;
using Physics = UnityEngine.Physics;

using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Configuration;

using ConVar;

using Pool = Facepunch.Pool;

using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("Night Zombies", "0x89A", "3.4.1")]
    [Description("Spawns and kills zombies at set times")]
    class NightZombies : RustPlugin
    {
        private const string DeathSound = "assets/prefabs/npc/murderer/sound/death.prefab";
        private const string RemoveMeMethodName = nameof(DroppedItemContainer.RemoveMe);
        private const int GrenadeItemId = 1840822026;
        
        private static NightZombies _instance;
        private static Configuration _config;
        private DynamicConfigFile _dataFile;

        [PluginReference("Kits")]
        private Plugin _kits;

        [PluginReference("Vanish")]
        private Plugin _vanish;

        private SpawnController _spawnController;
        
        #region -Init-
        
        private void Init()
        {
            _instance = this;

            _spawnController = new SpawnController();
            
            //Read saved number of days since last spawn
            _dataFile = Interface.Oxide.DataFileSystem.GetFile("NightZombies-daysSinceSpawn");

            try
            {
                _spawnController.DaysSinceLastSpawn = _dataFile.ReadObject<int>();
            }
            catch //Default to 0 if error reading or data broken
            {
                PrintWarning("Failed to load saved days since last spawn, defaulting to 0");
                _spawnController.DaysSinceLastSpawn = 0;
            }

            if (_config.Behaviour.SentriesAttackZombies)
            {
                Unsubscribe(nameof(OnTurretTarget));
            }

            if (_config.Destroy.SpawnLoot)
            {
                Unsubscribe(nameof(OnCorpsePopulate));
            }

            if (_config.Behaviour.Ignored.Count == 0 && !_config.Behaviour.IgnoreHumanNpc && _config.Behaviour.AttackSleepers)
            {
                Unsubscribe(nameof(OnNpcTarget));
            }
        }
        
        private void OnServerInitialized()
        {
            //Warn if kits is not loaded
            if (!_kits?.IsLoaded ?? false)
            {
                PrintWarning("Kits is not loaded, custom kits will not work");
            }
            
            //Start time check
            if (!_config.Spawn.AlwaysSpawned && _config.Spawn.SpawnTime >= 0 && _config.Spawn.DestroyTime >= 0)
            {
                TOD_Sky.Instance.Components.Time.OnMinute += _spawnController.TimeTick;
                TOD_Sky.Instance.Components.Time.OnDay += OnDay;
            }
        }
        
        private void Unload()
        {
            TOD_Sky.Instance.Components.Time.OnMinute -= _spawnController.TimeTick;
            TOD_Sky.Instance.Components.Time.OnDay -= OnDay;

            _dataFile.WriteObject(_spawnController.DaysSinceLastSpawn);

            _spawnController?.Shutdown();

            _config = null;
            _instance = null;
        }

        private void OnDay() => _spawnController.DaysSinceLastSpawn++;

        #endregion

        #region -Oxide Hooks-

        private object OnNpcTarget(ScarecrowNPC npc, BaseEntity target)
        {
            return CanAttack(target);
        }

        private object OnTurretTarget(NPCAutoTurret turret, ScarecrowNPC entity)
        {
            if (entity == null)
            {
                return null;
            }
            
            return true;
        }

        private object OnPlayerDeath(ScarecrowNPC scarecrow, HitInfo info)
        {
            Effect.server.Run(DeathSound, scarecrow.transform.position);
            _spawnController.ZombieDied(scarecrow);

            if (_config.Destroy.LeaveCorpseKilled)
            {
                return null;
            }
            
            NextTick(() =>
            {
                if (scarecrow == null || scarecrow.IsDestroyed)
                {
                    return;
                }

                scarecrow.AdminKill();
            });
                
            return true;
        }

        private BaseCorpse OnCorpsePopulate(ScarecrowNPC npcPlayer, NPCPlayerCorpse corpse)
        {
            return corpse;
        }

        private void OnEntitySpawned(NPCPlayerCorpse corpse)
        {
            if (corpse.playerName == "Scarecrow")
            {
                corpse.playerName = _config.Spawn.Zombies.DisplayName;
            }
        }
        
        private void OnEntitySpawned(DroppedItemContainer container)
        {
            if (!_config.Destroy.HalfBodybagDespawn)
            {
                return;
            }
            
            NextTick(() =>
            {
                if (container != null && container.playerName == _config.Spawn.Zombies.DisplayName)
                {
                    container.CancelInvoke(RemoveMeMethodName);
                    container.Invoke(RemoveMeMethodName, container.CalculateRemovalTime() / 2);
                }
            });
        }

        #endregion

        #region -Helpers-

        private object CanAttack(BaseEntity target)
        {
            if (_config.Behaviour.Ignored.Contains(target.ShortPrefabName) || 
                (_config.Behaviour.IgnoreHumanNpc && HumanNPCCheck(target)) || 
                (!_config.Behaviour.AttackSleepers && target is BasePlayer player && player.IsSleeping()))
            {
                return true;
            }
            
            return null;
        }

        private bool HumanNPCCheck(BaseEntity target)
        {
            return target is BasePlayer player && !player.userID.IsSteamId() && target is not ScientistNPC &&
                   target is not ScarecrowNPC;
        }

        #endregion

        #region -Classes-

        private class SpawnController
        {
            private const string _zombiePrefab = "assets/prefabs/npc/scarecrow/scarecrow.prefab";

            private readonly Configuration.SpawnSettings _spawnConfig;
            private readonly Configuration.SpawnSettings.ZombieSettings _zombiesConfig;
            
            private readonly int _spawnLayerMask = LayerMask.GetMask("Default", "Tree", "Construction", "World", "Vehicle_Detailed", "Deployed");
            private readonly WaitForSeconds _waitTenthSecond = new(0.1f);

            private bool IsSpawnTime => _spawnConfig.AlwaysSpawned || _spawnTime > _destroyTime
                                            ? Env.time >= _spawnTime || Env.time < _destroyTime
                                            : Env.time <= _spawnTime || Env.time > _destroyTime;

            private bool IsDestroyTime => _spawnTime > _destroyTime
                                              ? Env.time >= _destroyTime && Env.time < _spawnTime
                                              : Env.time <= _destroyTime && Env.time > _spawnTime;
            
            public int DaysSinceLastSpawn;
            
            private readonly float _spawnTime;
            private readonly float _destroyTime;
            private readonly bool _leaveCorpse;
            
            private bool _spawned;

            private Coroutine _currentCoroutine;
            
            private readonly List<ScarecrowNPC> _zombies = new();
            
            public SpawnController()
            {
                _spawnConfig = _config.Spawn;
                _zombiesConfig = _config.Spawn.Zombies;
                
                _spawnTime = _spawnConfig.SpawnTime;
                _destroyTime = _spawnConfig.DestroyTime;
                
                // These might not be available after the plugin is unloaded, will cause NRE if trying to access in RemoveZombies
                _leaveCorpse = _config.Destroy.LeaveCorpse;
            }

            private IEnumerator SpawnZombies()
            {
                if (_zombiesConfig.Population <= 0)
                {
                    yield break;
                }
                
                if (_currentCoroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(_currentCoroutine);
                }
                
                _spawned = true;

                for (int i = 0; i < _zombiesConfig.Population; i++)
                {
                    SpawnZombie();
                    yield return _waitTenthSecond;
                }
                
                if (_config.Broadcast.DoBroadcast && !_spawned)
                {
                    Broadcast("ChatBroadcast", _zombiesConfig.Population);
                }

                DaysSinceLastSpawn = 0;

                _currentCoroutine = null;
            }

            private IEnumerator RemoveZombies(bool shuttingDown = false)
            {
                if (_zombies.Count == 0)
                {
                    yield break;
                }

                if (_currentCoroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(_currentCoroutine);
                }
                
                foreach (ScarecrowNPC zombie in _zombies.ToArray())
                {
                    if (zombie == null || zombie.IsDestroyed)
                    {
                        continue;
                    }

                    if (_leaveCorpse && !shuttingDown)
                    {
                        zombie.Die();
                    }
                    else
                    {
                        zombie.AdminKill();
                    }

                    yield return !shuttingDown ? _waitTenthSecond : null;
                }

                _zombies?.Clear();
                _spawned = false;

                _currentCoroutine = null;
            }

            public void TimeTick()
            {
                if (CanSpawn())
                {
                    _currentCoroutine = ServerMgr.Instance.StartCoroutine(SpawnZombies());
                }
                else if (_zombies.Count > 0 && IsDestroyTime && _spawned)
                {
                    _currentCoroutine = ServerMgr.Instance.StartCoroutine(RemoveZombies());
                }
            }

            public void ZombieDied(ScarecrowNPC zombie)
            {
                _zombies.Remove(zombie);
                
                if (!IsSpawnTime)
                {
                    return;
                }

                SpawnZombie();
            }

            #region -Util-

            private void SpawnZombie()
            {
                if (_zombies.Count >= _zombiesConfig.Population)
                {
                    return;
                }

                Vector3 position = _spawnConfig.SpawnNearPlayers && BasePlayer.activePlayerList.Count >= _spawnConfig.MinNearPlayers && 
                                   GetRandomPlayer(out BasePlayer player) ? GetRandomPositionAroundPlayer(player) : GetRandomPosition();

                ScarecrowNPC zombie = GameManager.server.CreateEntity(_zombiePrefab, position) as ScarecrowNPC;
                if (!zombie)
                {
                    return;
                }
                
                zombie.Spawn();
                
                zombie.displayName = _zombiesConfig.DisplayName;

                if (zombie.TryGetComponent(out BaseNavigator navigator))
                {
                    navigator.ForceToGround();
                    navigator.PlaceOnNavMesh();
                }

                //Initialise health
                float health = _spawnConfig.Zombies.Health;
                zombie.SetMaxHealth(health);
                zombie.SetHealth(health);

                //Give kit
                if (_instance._kits != null && _zombiesConfig.Kits.Count > 0)
                {
                    zombie.inventory.containerWear.Clear();
                    ItemManager.DoRemoves();

                    _instance._kits.Call("GiveKit", zombie, _zombiesConfig.Kits.GetRandom());
                }

                if (!_config.Behaviour.ThrowGrenades)
                {
                    foreach (Item item in zombie.inventory.FindItemsByItemID(GrenadeItemId))
                    {
                        item.Remove();
                    }

                    ItemManager.DoRemoves();
                }

                _zombies.Add(zombie);
            }

            private bool GetRandomPlayer(out BasePlayer player)
            {
                List<BasePlayer> players = Pool.GetList<BasePlayer>();

                foreach (BasePlayer bplayer in BasePlayer.activePlayerList)
                {
                    if (bplayer.IsFlying || _instance._vanish?.Call<bool>("IsInvisible", bplayer) == true)
                    {
                        continue;
                    }

                    players.Add(bplayer);
                }

                player = players.GetRandom();

                Pool.FreeList(ref players);
                
                return player;
            }

            private Vector3 GetRandomPosition()
            {
                Vector3 position = Vector3.zero;

                for (int i = 0; i < 6; i++)
                {
                    float x = Random.Range(-TerrainMeta.Size.x / 2, TerrainMeta.Size.x / 2),
                          z = Random.Range(-TerrainMeta.Size.z / 2, TerrainMeta.Size.z / 2),
                          y = TerrainMeta.HeightMap.GetHeight(new Vector3(x, 0, z));

                    position = new Vector3(x, y + 0.5f, z);

                    // If valid position
                    if (!AntiHack.TestInsideTerrain(position) && !IsInObject(position) && !IsInOcean(position))
                    {
                        break;
                    }
                }

                if (position == Vector3.zero)
                {
                    position.y = TerrainMeta.HeightMap.GetHeight(0, 0);
                }

                return position;
            }

            private Vector3 GetRandomPositionAroundPlayer(BasePlayer player)
            {
                Vector3 playerPos = player.transform.position;
                Vector3 position = Vector3.zero;

                float maxDist = _spawnConfig.MaxDistance;
                
                for (int i = 0; i < 6; i++)
                {
                    position = new Vector3(Random.Range(playerPos.x - maxDist, playerPos.x + maxDist), 0, Random.Range(playerPos.z - maxDist, playerPos.z + maxDist));
                    position.y = TerrainMeta.HeightMap.GetHeight(position);

                    // If valid position
                    if (!AntiHack.TestInsideTerrain(position) && !IsInObject(position) && !IsInOcean(position) && 
                        Vector3.Distance(playerPos, position) > _spawnConfig.MinDistance)
                    {
                        break;
                    }
                }

                if (position == Vector3.zero)
                {
                    position = GetRandomPosition();
                }

                return position;
            } 

            private bool CanSpawn()
            {
                return !_spawned && DaysSinceLastSpawn >= _spawnConfig.Chance.Days && Random.Range(0f, 100f) < _spawnConfig.Chance.Chance && IsSpawnTime;
            }

            private bool IsInObject(Vector3 position)
            {
                return Physics.OverlapSphere(position, 0.5f, _spawnLayerMask).Length > 0;
            }

            private bool IsInOcean(Vector3 position)
            {
                return WaterLevel.GetWaterDepth(position, true, true) > 0.25f;
            }

            private void Broadcast(string key, params object[] values)
            {
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    player.ChatMessage(string.Format(_instance.GetMessage(key, player.UserIDString), values));
                }
            }
            
            #endregion

            public void Shutdown()
            {
                ServerMgr.Instance.StartCoroutine(RemoveZombies(true));
            }
        }

        #endregion
        
        #region -Configuration-

        private class Configuration
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
                [JsonProperty("Always Spawned")]
                public bool AlwaysSpawned = false;

                [JsonProperty("Spawn Time")]
                public float SpawnTime = 19.8f;

                [JsonProperty("Destroy Time")]
                public float DestroyTime = 7.3f;
                
                [JsonProperty("Spawn near players")]
                public bool SpawnNearPlayers = false;

                [JsonProperty("Min pop for near player spawn")]
                public int MinNearPlayers = 10;

                [JsonProperty("Min distance from player")]
                public float MinDistance = 40;

                [JsonProperty("Max distance from player")]
                public float MaxDistance = 80f;

                [JsonProperty("Zombie Settings")]
                public ZombieSettings Zombies = new ZombieSettings();

                public class ZombieSettings
                {
                    [JsonProperty("Display Name")] 
                    public string DisplayName = "Scarecrow";
                    
                    [JsonProperty("Scarecrow Population (total amount)")]
                    public int Population = 50;
                    
                    [JsonProperty("Scarecrow Health")]
                    public float Health = 200f;

                    [JsonProperty("Scarecrow Kits")]
                    public List<string> Kits = new List<string>();
                }

                [JsonProperty("Chance Settings")]
                public ChanceSetings Chance = new ChanceSetings();

                public class ChanceSetings
                {
                    [JsonProperty("Chance per cycle")]
                    public float Chance = 100f;
                    
                    [JsonProperty("Days betewen spawn")]
                    public int Days = 0;
                }
            }

            public class DestroySettings
            {
                [JsonProperty("Leave Corpse, when destroyed")]
                public bool LeaveCorpse = false;
                
                [JsonProperty("Leave Corpse, when killed by player")]
                public bool LeaveCorpseKilled = true;

                [JsonProperty("Spawn Loot")]
                public bool SpawnLoot = true;

                [JsonProperty("Half bodybag despawn time")]
                public bool HalfBodybagDespawn = true;
            }

            public class BehaviourSettings
            {
                [JsonProperty("Attack sleeping players")]
                public bool AttackSleepers = false;

                [JsonProperty("Zombies attacked by outpost sentries")]
                public bool SentriesAttackZombies = true;
                
                [JsonProperty("Throw Grenades")]
                public bool ThrowGrenades = true;

                [JsonProperty("Ignore Human NPCs")]
                public bool IgnoreHumanNpc = true;

                [JsonProperty("Ignored entities (full entity shortname)")]
                public List<string> Ignored = new List<string>();
            }

            public class ChatSettings
            {
                [JsonProperty("Broadcast spawn amount")]
                public bool DoBroadcast = false;
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();

                if (_config.Spawn.SpawnTime >= 0 && _config.Spawn.DestroyTime >= 0)
                {
                    if (_config.Spawn.SpawnTime > 24 || _config.Spawn.SpawnTime < 0)
                    {
                        PrintWarning("Invalid spawn time (must be in 24 hour time)");
                        _config.Spawn.SpawnTime = 19.5f;
                    }
                    if (_config.Spawn.DestroyTime > 24 || _config.Spawn.DestroyTime < 0)
                    {
                        PrintWarning("Invalid destroy time (must be in 24 hour time)");
                        _config.Spawn.DestroyTime = 7f;
                    }
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
                Ignored = new List<string>
                {
                    "scientistjunkpile.prefab",
                    "scarecrow.prefab"
                }
            }
        };

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region -Localisation-

        private string GetMessage(string key, string userId = null)
        {
            return lang.GetMessage(key, this, userId);
        }
        
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