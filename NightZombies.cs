using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;

using UnityEngine;

using Newtonsoft.Json;

using Rust;
using Oxide.Game.Rust.Cui;

namespace Oxide.Plugins
{
    [Info("Night Zombies", "0x89A", "0.9.0")]
    [Description("Spawns murderers and scarecrows at night, kills them at sunrise.")]
    class NightZombies : RustPlugin
    {
        private bool halloweenActive;

        private Timer destroyTimer;

        #region -Configuration-

        private static Configuration _config;

        class Configuration
        {
            [JsonProperty(PropertyName = "Murderer Population")]
            public int murdererPopulation = 50;
            
            [JsonProperty(PropertyName = "Scarecrow Population")]
            public int scarecrowPopulation = 50;

            [JsonProperty(PropertyName = "Kill Murderers")]
            public bool killMurderers = true;

            [JsonProperty(PropertyName = "Play sound")]
            public bool playSound = false;

            [JsonProperty(PropertyName = "Broadcast Spawn amount")]
            public bool doBroadcast = false;

            [JsonProperty(PropertyName = "Broadcast murderer and scarecrow count separately")]
            public bool broadcastSeparate = false;

            [JsonProperty(PropertyName = "Slow destroy")]
            public bool slowDestroy = false;

            [JsonProperty(PropertyName = "Slow destroy time (seconds)")]
            public float destroyTime = 0.15f;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();
                SaveConfig();
            }
            catch
            {
                PrintError("Your config contains an error or does not exist, using default values");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        protected override void LoadDefaultConfig() => _config = new Configuration();

        #endregion

        void OnServerInitialized(bool serverInit)
        {
            TOD_Sky.Instance.Components.Time.OnSunset += SpawnZombies;
            TOD_Sky.Instance.Components.Time.OnSunrise += StartDestroyZombies;
        }

        void Unload()
        {
            TOD_Sky.Instance.Components.Time.OnSunset -= SpawnZombies;
            TOD_Sky.Instance.Components.Time.OnSunrise -= StartDestroyZombies;

            if (destroyTimer != null) destroyTimer.Destroy();
        }

        void SpawnZombies()
        {
            if (!halloweenActive)
            {
                Server.Command("halloween.enabled", true);
                Server.Command("halloween.murdererpopulation", _config.murdererPopulation);
                Server.Command("halloween.scarecrowpopulation", _config.scarecrowPopulation);
                
                halloweenActive = true;

                if (_config.doBroadcast) timer.Once(15f, () => { ChatBroadcast(); if (_config.playSound) PlaySound(); });
            }
        }

        void StartDestroyZombies()
        {
            if (halloweenActive)
            {
                Server.Command("halloween.enabled", false);
                Server.Command("halloween.murdererpopulation", 0);
                Server.Command("halloween.scarecrowpopulation", 0);

                timer.Once(5f, () => ServerMgr.Instance.StartCoroutine(DestroyZombies()));
            }
        }

        IEnumerator DestroyZombies()
        {
            if (_config.slowDestroy)
            {
                destroyTimer = timer.Every(_config.destroyTime, () =>
                {
                    if (BaseNetworkable.serverEntities.Any(x => (x is NPCMurderer || x is HTNPlayer) && !(x is Scientist || x is ScientistNPC || x.ShortPrefabName.Contains("scientist"))))
                    {
                        BaseCombatEntity ent = BaseNetworkable.serverEntities.FirstOrDefault(x => x is NPCMurderer || x is HTNPlayer) as BaseCombatEntity;

                        if (ent == null) destroyTimer.Destroy();

                        if (_config.killMurderers) ent.Hurt(ent.MaxHealth());
                        else
                        {
                            ent.Kill();
                            UnityEngine.Object.Destroy(ent);
                        }
                    }
                    else destroyTimer.Destroy();
                });
            }
            else
            {
                while (BaseNetworkable.serverEntities.Any(x => (x is NPCMurderer || x is HTNPlayer) && !(x is Scientist || x is ScientistNPC || x.ShortPrefabName.Contains("scientist"))))
                {
                    foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
                    {
                        if ((networkable is HTNPlayer || networkable is NPCMurderer) && !(networkable is Scientist || networkable is ScientistNPC || networkable.ShortPrefabName.Contains("scientist")))
                        {   
                            BaseCombatEntity ent = networkable as BaseCombatEntity;

                            if (_config.killMurderers) ent.Hurt(ent.MaxHealth());
                            else
                            {
                                ent.Kill();
                                UnityEngine.Object.Destroy(ent);
                            }
                        }
                    }
                    yield return null;
                }
            }

            halloweenActive = false;

            yield break;
        }

        private void ChatBroadcast()
        {
            float sqkm = (Terrain.activeTerrains[0].terrainData.size.x / 1000) * (Terrain.activeTerrains[0].terrainData.size.z / 1000);

            float murderers = _config.murdererPopulation * sqkm;

            var scarecrows = _config.scarecrowPopulation * sqkm;

            string text = _config.broadcastSeparate ? $"[Night Zombies] Spawned {Mathf.RoundToInt(murderers)} murderers | Spawned {Mathf.RoundToInt(scarecrows)} scarecrows" : $"[Night Zombies] Spawned {Mathf.RoundToInt(murderers + scarecrows)} zombies";

            PrintToChat(text);
        }

        [ChatCommand("test")]
        private void PlaySound()
        {
            foreach (BasePlayer player in Player.Players)
            {
                Effect effect = new Effect("assets/prefabs/misc/halloween/spookyspeaker/sound/spookysounds.asset", player, 0, Vector3.zero, Vector3.forward);
                EffectNetwork.Send(effect, player.net.connection);
            }
        }
    }
}
