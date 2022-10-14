using System;
using System.Collections;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;
using Physics = UnityEngine.Physics;
using Time = UnityEngine.Time;

using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Configuration;

using ConVar;

using Pool = Facepunch.Pool;

using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("Night Zombies", "0x89A", "3.3.14")]
    [Description("Spawns and kills zombies at set times")]
    class NightZombies : RustPlugin
    {
        private static NightZombies _instance;
        private Configuration _config;
        private DynamicConfigFile _dataFile;

        [PluginReference("Kits")] private Plugin _kits;
        [PluginReference("DeathNotes")] private Plugin _deathNotes;
        [PluginReference("Quests")] private Plugin _quests;
        [PluginReference("XPerience")] private Plugin _xperience;
        [PluginReference("XPerienceAddon")] private Plugin _xperienceaddon;
        [PluginReference("Vanish")] private Plugin _vanish;

        private SpawnController _spawnController;

        #region -Init-
        
        void Init()
        {
            _instance = this;

            _spawnController = new SpawnController(_config);
            
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
        }
        
        void OnServerInitialized()
        {
            //Warn if kits is not loaded
            if (!_kits?.IsLoaded ?? false)
            {
                PrintWarning("Kits is not loaded, custom kits will not work");
            }
            
            //Start time check
            if (_config.Spawn.spawnTime >= 0 && _config.Spawn.destroyTime >= 0)
            {
                timer.Once(5f, () =>
                {
                    TOD_Sky.Instance.Components.Time.OnMinute += _spawnController.TimeTick;
                    TOD_Sky.Instance.Components.Time.OnDay += OnDay;
                });
            }
        }
        
        void Unload()
        {
            if (_config.Spawn.spawnTime >= 0 && _config.Spawn.destroyTime >= 0)
            {
                TOD_Sky.Instance.Components.Time.OnMinute -= _spawnController.TimeTick;
                TOD_Sky.Instance.Components.Time.OnDay -= OnDay;
            }

            _dataFile.WriteObject(_spawnController.DaysSinceLastSpawn);

            _spawnController?.Shutdown();
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
            return !_config.Behaviour.sentriesAttackZombies ? (object)true : null;
        }

        private object OnPlayerDeath(ScarecrowNPC entity, HitInfo info)
        {
            if (_spawnController.IsNightZombie(entity) && _spawnController.Spawned)
            {
                _spawnController.Respawn(entity);
                _deathNotes?.Call("OnEntityDeath", entity as BasePlayer, info);
                _quests?.Call("OnEntityDeath", entity, info);
                _xperience?.Call("OnEntityDeath", entity, info);
                _xperienceaddon?.Call("OnEntityDeath", entity, info);
                return true;
            }

            return null;
        }

        private void OnEntitySpawned(DroppedItemContainer container)
        {
            if (_config.Destroy.halfBodybagDespawn && container.lootPanelName == _config.Spawn.Zombies.displayName)
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

        public void PlaySound(BaseEntity ent, string sound)
        {
            Effect.server.Run(sound, ent, StringPool.Get("head"), Vector3.zero, Vector3.up);
        }
        
        #endregion

        #region -Classes-

        private class SpawnController
        {
            private const string _prefab = "assets/prefabs/npc/scarecrow/scarecrow.prefab";

            public bool Spawned;

            private readonly Configuration.SpawnSettings _spawnConfig;
            private readonly Configuration.SpawnSettings.ZombieSettings _zombiesConfig;
            
            public bool IsSpawnTime => _spawnTime > _destroyTime ? Env.time >= _spawnTime || Env.time < _destroyTime : Env.time <= _spawnTime || Env.time > _destroyTime;
            private bool IsDestroyTime => _spawnTime > _destroyTime ? Env.time >= _destroyTime && Env.time < _spawnTime : Env.time <= _destroyTime && Env.time > _spawnTime;

            public int DaysSinceLastSpawn;
            private readonly float _spawnTime;
            private readonly float _destroyTime;

            private Timer _spawnTimer;

            private readonly Dictionary<BaseCombatEntity, NightZombie> _zombies = new Dictionary<BaseCombatEntity, NightZombie>();

            public SpawnController(Configuration config)
            {
                _spawnConfig = config.Spawn;
                _zombiesConfig = config.Spawn.Zombies;
                
                _spawnTime = _spawnConfig.spawnTime;
                _destroyTime = _spawnConfig.destroyTime;
            }

            private void SpawnZombies()
            {
                if (ServerMgr.Instance.IsInvoking(nameof(RemoveZombies)))
                {
                    return;
                }
                
                if (_zombiesConfig.population > 0)
                {
                    _spawnTimer?.Destroy();
                    _spawnTimer = _instance.timer.Repeat(0.5f, _zombiesConfig.population, Spawn);
                }

                if (_instance._config.Broadcast.doBroadcast && !Spawned)
                {
                    Broadcast("ChatBroadcast", _zombiesConfig.population);
                }

                DaysSinceLastSpawn = 0;
                Spawned = true;
            }

            public IEnumerator RemoveZombies(bool configOverride = false, Action complete = null)
            {
                foreach (var pair in _zombies)
                {
                    BaseCombatEntity entity = pair.Key;
                    if (entity == null) continue;
                    
                    if (_instance._config.Destroy.leaveCorpse && !configOverride) pair.Value.CreateCorpse();

                    entity.AdminKill();
                }

                _zombies.Clear();
                Spawned = false;

                complete?.Invoke();

                yield break;
            }

            public void Respawn(BaseCombatEntity entity)
            {
                NightZombie zombie;
                if (_zombies.TryGetValue(entity, out zombie))
                {
                    zombie.OnDeath();
                }
            }
            
            public void TimeTick()
            {
                if (CanSpawn())
                {
                    ServerMgr.Instance.StartCoroutine(RemoveZombies(false, SpawnZombies));
                }
                else if (_zombies.Count > 0 && IsDestroyTime && Spawned)
                {
                    //Stop timer
                    _spawnTimer?.Destroy();
                    
                    ServerMgr.Instance.StartCoroutine(RemoveZombies());
                }
            }

            #region -Util-

            private void Spawn()
            {
                if (_zombies.Count >= _zombiesConfig.population)
                {
                    return;
                }

                BasePlayer player;
                Vector3 position = _spawnConfig.spawnNearPlayers && BasePlayer.activePlayerList.Count >= _spawnConfig.minNearPlayers && 
                                   GetPlayer(out player) ? GetRandomPositionAroundPlayer(player) : GetRandomPosition();

                BasePlayer entity = GameManager.server.CreateEntity(_prefab, position, Quaternion.identity, false) as BasePlayer;
                if (!entity)
                {
                    return;
                }

                NightZombie zombie = entity.gameObject.AddComponent<NightZombie>();
                entity.gameObject.AwakeFromInstantiate();
                entity.Spawn();
                
                entity.displayName = _zombiesConfig.displayName;
                entity._name = _zombiesConfig.displayName;

                //Initialise health
                float health = _spawnConfig.Zombies.health;
                entity.SetMaxHealth(health);
                entity.SetHealth(health);

                //Give kit
                if (_instance._kits != null && _zombiesConfig.kits.Count > 0)
                {
                    entity.inventory.containerWear.Clear();
                    ItemManager.DoRemoves();

                    _instance._kits.Call("GiveKit", entity, _zombiesConfig.kits.GetRandom());
                }

                _zombies.Add(entity, zombie);
            }

            public bool GetPlayer(out BasePlayer player)
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
                
                player = players[Random.Range(0, players.Count)];

                Pool.FreeList(ref players);
                
                return player;
            }

            public Vector3 GetRandomPosition()
            {
                Vector3 position = Vector3.zero;

                for (int i = 0; i < 2; i++)
                {
                    float x = Random.Range(-TerrainMeta.Size.x / 2, TerrainMeta.Size.x / 2),
                          z = Random.Range(-TerrainMeta.Size.z / 2, TerrainMeta.Size.z / 2),
                          y = TerrainMeta.HeightMap.GetHeight(new Vector3(x, 0, z));

                    position = new Vector3(x, y + 0.5f, z);

                    if (AntiHack.TestInsideTerrain(position) || IsInObject(position) || IsInOcean(position))
                    {
                        i = 0;
                    }
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

                float maxDist = _spawnConfig.maxDistance;

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

                    if (AntiHack.TestInsideTerrain(position) || IsInObject(position) || IsInOcean(position) || Vector3.Distance(playerPos, position) < _spawnConfig.minDistance)
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
                return !Spawned && DaysSinceLastSpawn >= _spawnConfig.Chance.days && Random.Range(0f, 100f) < _spawnConfig.Chance.chance && IsSpawnTime;
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

            public bool IsNightZombie(BaseCombatEntity entity) => _zombies.ContainsKey(entity);
            
            #endregion

            public void Shutdown()
            {
                //Stop timer
                _spawnTimer?.Destroy();

                ServerMgr.Instance.StartCoroutine(RemoveZombies(true));
            }
        }

        private class NightZombie : FacepunchBehaviour
        {
            private const string _corpsePrefab = "assets/rust.ai/agents/npcplayer/pet/frankensteinpet_corpse.prefab";
            private const string _breatheSound = "assets/prefabs/npc/murderer/sound/breathing.prefab";
            private const string _deathSound = "assets/prefabs/npc/murderer/sound/death.prefab";

            private float _lastSpawnTime;
            private ScarecrowNPC _scarecrow;
            private LootContainer.LootSpawnSlot[] _loot;
            private BrainState _brainState;
            private BaseNavigator _navigator;

            #region -Init-

            private void Awake()
            {
                _scarecrow = GetComponent<ScarecrowNPC>();
                _navigator = GetComponent<BaseNavigator>();
                _loot = _scarecrow.LootSpawnSlots;
            }

            private void Start()
            {
                _scarecrow.Brain.states.Remove(AIState.Chase);
                _brainState = new BrainState();
                _scarecrow.Brain.AddState(_brainState);
                _scarecrow.Brain.TargetLostRange = 30f;
                _scarecrow.Brain.SenseRange = 15;

                ItemManager.CreateByItemID(1840822026, 100).MoveToContainer(_scarecrow.inventory.containerBelt, 1);

                if (_instance._config.Spawn.Zombies.useBreathingSound)
                {
                    InvokeRepeating(() => _instance.PlaySound(_scarecrow, _breatheSound), 0f, 9f);
                }
                
                _navigator.ForceToGround();
                _navigator.PlaceOnNavMesh();
            }

            #endregion

            #region -Util

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

                _scarecrow.Brain.Senses.UpdateKnownPlayersLOS();
                
                _scarecrow.Teleport(position);
                _scarecrow.gameObject.SetActive(true);
                _navigator.ForceToGround();
                _navigator.PlaceOnNavMesh();

                _scarecrow.Heal(_instance._config.Spawn.Zombies.health);
                _scarecrow.metabolism.bleeding.SetValue(0f);
                _scarecrow.SendNetworkUpdateImmediate();
            }

            public void OnDeath()
            {
                if (Time.time - _lastSpawnTime < 0.5f)
                {
                    _scarecrow.AdminKill();
                    return;
                }
                
                if (_scarecrow == null || _scarecrow.IsDead())
                {
                    _scarecrow.AdminKill();
                    return;
                }

                _brainState.EndTimer();
                Respawn();
                
                _lastSpawnTime = Time.time;
            }

            public void CreateCorpse()
            {
                NPCPlayerCorpse corpse = GameManager.server.CreateEntity(_corpsePrefab) as NPCPlayerCorpse;

                if (corpse == null)
                {
                    return;
                }

                corpse.transform.SetPositionAndRotation(_scarecrow.ServerPosition + Vector3.up * 0.25f, _scarecrow.ServerRotation);
                    
                corpse.SetLootableIn(2f);
                corpse.SetFlag(BaseEntity.Flags.Reserved5, _scarecrow.HasPlayerFlag(BasePlayer.PlayerFlags.DisplaySash));
                corpse.SetFlag(BaseEntity.Flags.Reserved2, true);

                CopyContainers(corpse);

                corpse.playerName = _scarecrow.displayName;
                corpse.playerSteamID = _scarecrow.userID;

                corpse.Spawn();
                corpse.TakeChildren(_scarecrow);
                _instance.PlaySound(corpse, _deathSound);

                ItemContainer[] containers = corpse.containers;

                //Clear inventory
                for (int i = 0; i < containers.Length; i++)
                {
                    containers[i].Clear();
                }
                
                PopulateLoot(corpse);
            }

            private void CopyContainers(NPCPlayerCorpse corpse)
            {
                ItemContainer[] inventory = 
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

                    //Copy items from scarecrow to corpse
                    
                    if (i == 1) //if wear container
                    {
                        foreach (Item item in invContainer.itemList.ToArray())
                        {
                            if (item.info.shortname == "gloweyes")
                            {
                                continue;
                            }

                            Item item2 = ItemManager.Create(item.info, item.amount, item.skin);

                            if (!item2.MoveToContainer(container))
                            {
                                item2.DropAndTossUpwards(_scarecrow.transform.position);
                            }
                        }
                    }
                }
            }
            
            private void PopulateLoot(NPCPlayerCorpse corpse)
            {
                if (Interface.CallHook("OnCorpsePopulate", _scarecrow, corpse) != null)
                {
                    return;
                }
            
                if (!_instance._config.Destroy.spawnLoot || _loot.Length <= 0)
                {
                    return;
                }
                
                //Populate containers

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

            #endregion

            #region -Brain-

            private class BrainState : BaseAIBrain.BaseChaseState
            {
                private ScarecrowNPC _scarecrow;
                
                private readonly int _navMeshMask = 1 << NavMesh.GetAreaFromName("Walkable");
                private DateTime _nextGrenadeTime;
                private bool _isThrowing;

                private Timer _grenadeTimer;

                public override void StateEnter(BaseAIBrain brain, BaseEntity entity)
                {
                    base.StateEnter(brain, entity);
                    
                    _scarecrow = entity as ScarecrowNPC;
                }

                public override StateStatus StateThink(float delta, BaseAIBrain brain, BaseEntity entity)
                {
                    BasePlayer target = brain.Senses.GetNearestTarget(brain.SenseRange) as BasePlayer;

                    if (target != null)
                    {
                        if (_isThrowing)
                        {
                            _scarecrow.SetAimDirection((target.ServerPosition - _scarecrow.ServerPosition).normalized);
                            return StateStatus.Running;
                        }
                        
                        float distance = Vector3.Distance(_scarecrow.transform.position, target.transform.position);
                        
                        if (_scarecrow.CanAttack(target) && distance < 1.5f)
                        {
                            _scarecrow.StartAttacking(target);
                            
                            //Play weapon sound
                            BaseMelee weapon = _scarecrow.GetHeldEntity() as BaseMelee;;
                            
                            if (weapon != null && weapon.swingEffect != null && weapon.swingEffect.isValid)
                            {
                                _instance.PlaySound(_scarecrow, weapon.swingEffect.resourcePath);
                            }
                        }
                        else if (_instance._config.Behaviour.throwGrenades && CanThrow(target.transform.position) && target.IsOnGround() && distance < 5f)
                        {
                            //Throw grenade
                            ThrowGrenade(target);
                            _nextGrenadeTime = DateTime.UtcNow.AddSeconds(3f);
                        }
                    }
                    
                    base.StateThink(delta, brain, entity);

                    return StateStatus.Running;
                }

                private void ThrowGrenade(BaseEntity target)
                {
                    _isThrowing = true;
                    
                    //Equip grenade
                    Item grenade = _scarecrow.inventory.containerBelt.GetSlot(1);
                    if (grenade == null) return;
                    
                    _scarecrow.UpdateActiveItem(grenade.uid);

                    _grenadeTimer = _instance.timer.Once(1.5f, () =>
                    {
                        //Look at target 
                        _scarecrow.SetAimDirection((target.ServerPosition - _scarecrow.ServerPosition).normalized);

                        //Do throw
                        _scarecrow.SignalBroadcast(BaseEntity.Signal.Throw);
                        (grenade.GetHeldEntity() as ThrownWeapon)?.ServerThrow(target.transform.position);
                        
                        //Re-equip weapon and end throw
                        _grenadeTimer = _instance.timer.Once(1f, () =>
                        {
                            _isThrowing = false;
                            _scarecrow.UpdateActiveItem(_scarecrow.inventory.containerBelt.GetSlot(0).uid);
                        });
                    });
                }

                private bool CanThrow(Vector3 target)
                {
                    NavMeshHit hit;
                    return !NavMesh.SamplePosition(target, out hit, 1f, _navMeshMask) && DateTime.UtcNow > _nextGrenadeTime;
                }

                public void EndTimer()
                {
                    _grenadeTimer?.Destroy();
                }
            }

            #endregion

            private void OnDestroy()
            {
                _brainState.EndTimer();
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
                    [JsonProperty("Display Name")] 
                    public string displayName = "Scarecrow";
                    
                    [JsonProperty("Scarecrow Population (total amount)")]
                    public int population = 50;
                    
                    [JsonProperty("Scarecrow Health")]
                    public float health = 200f;

                    [JsonProperty("Make Breathing Sound")]
                    public bool useBreathingSound = true;

                    [JsonProperty("Scarecrow Kits")]
                    public List<string> kits = new List<string>();
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

                [JsonProperty("Spawn Loot")]
                public bool spawnLoot = true;

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
                
                [JsonProperty("Throw Grenades")]
                public bool throwGrenades = true;

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

                if (_config.Spawn.spawnTime >= 0 && _config.Spawn.destroyTime >= 0)
                {
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