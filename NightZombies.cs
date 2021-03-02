using System;
using System.Collections;
using System.Collections.Generic;

using ConVar;

using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;

using UnityEngine;
using Random = UnityEngine.Random;

using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("Night Zombies", "0x89A", "2.1.0")]
    [Description("Spawns zombies at night, kills them at sunrise")]
    class NightZombies : CovalencePlugin
    {
        private Configuration config;

        DynamicConfigFile dataFile;

        private bool IsActive => Halloween.enabled;

        private int daysSinceSpawn = 0;

        List<BaseCombatEntity> serverZombies;

        [PluginReference] Plugin ConsoleFilter;

        #region -Oxide Hooks-

        void OnServerInitialized()
        {
            serverZombies = Facepunch.Pool.GetList<BaseCombatEntity>();

            TOD_Sky.Instance.Components.Time.OnSunrise += ToggleZombies;
            TOD_Sky.Instance.Components.Time.OnSunset += ToggleZombies;

            dataFile = Interface.Oxide.DataFileSystem.GetFile($"{Name}");
            if (dataFile == null) return;

            daysSinceSpawn = dataFile.ReadObject<int>();
        }

        void Unload()
        {
            TOD_Sky.Instance.Components.Time.OnSunrise -= ToggleZombies;
            TOD_Sky.Instance.Components.Time.OnSunset -= ToggleZombies;

            SaveData();

            ServerMgr.Instance.StartCoroutine(DestroyZombies());

            if (IsActive) RunCommands(false);

            if (!ConsoleFilter)
            {
                Application.logMessageReceived -= RemoveKillMessage;
                Application.logMessageReceived += Facepunch.Output.LogHandler;
            }

            Facepunch.Pool.FreeList(ref serverZombies);
        }

        void Loaded()
        {
            if (ConsoleFilter)
            {
                PrintWarning("Console Filter detected disabling filtering, It is recommended to add \"killed by Generic\" to you Console Filter config");
            }
            else
            {
                Application.logMessageReceived += RemoveKillMessage;
                Application.logMessageReceived -= Facepunch.Output.LogHandler;
            }
        }

        private void SaveData() => dataFile.WriteObject(daysSinceSpawn);

        void OnEntitySpawned(BaseNetworkable networkable)
        {
            if (IsZombie(networkable))
            {
                BaseCombatEntity combat = networkable as BaseCombatEntity;
                if (combat)
                {
                    combat.SetHealth(combat.ShortPrefabName == "scarecrow" ? config.SpawnSettings.scarecrowHealth : config.SpawnSettings.murdererHealth);
                    combat.SendNetworkUpdateImmediate();
                    serverZombies.Add(combat);
                }
            }
        }

        bool CanNpcAttack(HTNPlayer npc, BaseEntity target)
        {
            return CanAttack(target);
        }

        bool CanNpcAttack(NPCMurderer npc, BaseEntity target)
        {
            return CanAttack(target);
        }

        void RemoveKillMessage(string message, string stackTrace, LogType type)
        {
            if (!string.IsNullOrEmpty(message) && !message.Contains("was killed by Generic"))
            {
                Facepunch.Output.LogHandler(message, stackTrace, type);
            }
        }

        #endregion

        private void ToggleZombies()
        {
            if (TOD_Sky.Instance.IsDay) daysSinceSpawn++;

            if (IsActive) timer.Once(15f, () => ServerMgr.Instance.StartCoroutine(DestroyZombies()));
            else if (!CanSpawn()) return;

            RunCommands(!IsActive);
        }

        private bool CanSpawn()
        {
            if ((config.SpawnSettings.reverseTimings && TOD_Sky.Instance.IsDay || !config.SpawnSettings.reverseTimings && TOD_Sky.Instance.IsNight) && 
                daysSinceSpawn >= config.SpawnSettings.Chance.days && Random.Range(0, 101) < config.SpawnSettings.Chance.chance) return true;
            else return false;
        }

        private bool IsZombie(BaseNetworkable networkable)
        {
            return (networkable is NPCMurderer || networkable is HTNPlayer) && !(networkable is Scientist || networkable is ScientistNPC || networkable.ShortPrefabName.Contains("scientist"));
        }

        #region -Util-

        private bool CanAttack(BaseEntity target)
        {
            if (config.BehaviourSettings.ifContains)
            {
                for (int i = 0; i < config.BehaviourSettings.ignored.Count; i++)
                    if (target.ShortPrefabName.Contains(config.BehaviourSettings.ignored[i])) return false;
            }

            return config.BehaviourSettings.ignored.Contains(target.ShortPrefabName);
        }

        private void RunCommands(bool on)
        {
            server.Command("halloween.enabled", on);
            server.Command("halloween.murdererpopulation", on ? config.SpawnSettings.murdererPoluation : 0);
            server.Command("halloween.scarecrowpopulation", on ? config.SpawnSettings.scarecrowPopulation : 0);

            if (on) daysSinceSpawn = 0;
        }

        #endregion

        #region -Chat Broadcast-

        private void ChatBroadcast() //This is just an estimate but it is fine for now
        {
            float sqkm = (TerrainMeta.Size.x / 1000) * (TerrainMeta.Size.z / 1000);

            float murderers = Halloween.murdererpopulation * sqkm;

            float scarecrows = Halloween.scarecrowpopulation * sqkm;

            foreach (BasePlayer player in BasePlayer.activePlayerList)
                player.ChatMessage(config.BroadcastSettings.broadcastSeparate 
                    ? lang.GetMessage("ChatBroadcastSeparate", this, player.UserIDString).Replace("{murdererCount}", Mathf.RoundToInt(murderers).ToString()).Replace("{scarecrowCount}", Mathf.RoundToInt(scarecrows).ToString()) 
                    : lang.GetMessage("ChatBroadcast", this, player.UserIDString).Replace("{total}", (Mathf.RoundToInt(murderers + scarecrows).ToString())));
        }

        #endregion

        #region -Destroying Zombies-

        IEnumerator DestroyZombies()
        {
            BaseNetworkable[] array = serverZombies.ToArray();

            for (int i = 0; serverZombies.Count != 0; i++)
            {
                yield return new WaitForSeconds(0.15f);

                BaseCombatEntity entity = null;

                try
                {
                    entity = serverZombies[i];
                }
                catch
                {
                    continue;
                }

                if (entity == null) continue;

                if (config != null && config.DestroySettings.leaveCorpse) entity.Die();
                else entity.Kill();
            }

            yield break;
        }

        #endregion

        #region -Configuration-

        class Configuration
        {
            [JsonProperty(PropertyName = "Spawn Settings")]
            public SpawnSettingsClass SpawnSettings = new SpawnSettingsClass();

            [JsonProperty(PropertyName = "Destroy Settings")]
            public DestroySettingsClass DestroySettings = new DestroySettingsClass();

            [JsonProperty(PropertyName = "Behaviour Settings")]
            public BehaviourSettingsClass BehaviourSettings = new BehaviourSettingsClass();

            [JsonProperty(PropertyName = "Broadcast Settings")]
            public ChatSettings BroadcastSettings = new ChatSettings();

            public class SpawnSettingsClass
            {
                [JsonProperty(PropertyName = "Murderer Population")]
                public float murdererPoluation = 50;

                [JsonProperty(PropertyName = "Murderer Health")]
                public float murdererHealth = 100f;

                [JsonProperty(PropertyName = "Scarecrow Population")]
                public float scarecrowPopulation = 50;

                [JsonProperty(PropertyName = "Scarecrow Health")]
                public float scarecrowHealth = 200f;

                [JsonProperty(PropertyName = "Reverse Spawn Timings")]
                public bool reverseTimings = false;

                [JsonProperty(PropertyName = "Chance Settings")]
                public ChanceSetings Chance = new ChanceSetings();

                public class ChanceSetings
                {
                    [JsonProperty(PropertyName = "Chance per cycle")]
                    public float chance = 100f;

                    [JsonProperty(PropertyName = "Days betewen spawn")]
                    public int days = 0;
                }
            }

            public class DestroySettingsClass
            {
                [JsonProperty(PropertyName = "Leave Corpse")]
                public bool leaveCorpse = true;
            }

            public class BehaviourSettingsClass
            {
                [JsonProperty(PropertyName = "Count if shortname contains")]
                public bool ifContains = false;

                [JsonProperty(PropertyName = "Ignored entities")]
                public List<string> ignored = new List<string>();
            }

            public class ChatSettings
            {
                [JsonProperty(PropertyName = "Broadcast spawn amount")]
                public bool doBroadcast = false;

                [JsonProperty(PropertyName = "Broadcast types separately")]
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
            BehaviourSettings = new Configuration.BehaviourSettingsClass
            {
                ignored = new List<string>
                {
                    "scientist",
                    "scientistjunkpile"
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
                ["ChatBroadcast"] = "[Night Zombies] Spawned {murdererCount} murderers | Spawned {scarecrowCount} scarecrows",
                ["ChatBroadcastSeparate"] = "[Night Zombies] Spawned {total} zombies"
            }, this);
        }

        #endregion
    }
}
