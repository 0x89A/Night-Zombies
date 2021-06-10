## Configuration

```json
{
  "Spawn Settings": {
    "Spawn near players": true,
    "Minimum pop for near player spawn": 0,
    "Min distance from player": 30.0,
    "Max distance from player": 60.0,
    "Spawn Time": 19.8,
    "Destroy Time": 7.3,
    "Spawn delay": 0.35,
    "Zombie Settings": {
      "Murderer Population (total amount)": 50,
      "Murderer Health": 100.0,
      "Scarecrow Population (total amount)": 50,
      "Scarecrow Health": 200.0
    },
    "Chance Settings": {
      "Chance per cycle": 100.0,
      "Days betewen spawn": 0
    }
  },
  "Destroy Settings": {
    "Leave Corpse, when destroyed (can cause more lag if true)": true,
    "Leave Corpse, when killed by player": true
  },
  "Behaviour Settings": {
    "Attack sleeping players": false,
    "Zombies attacked by outpost sentries": true,
    "Ignore Human NPCs": true,
    "Ignored entities (full entity shortname)": [
      "scientistjunkpile.prefab",
      "scarecrow.prefab"
    ]
  },
  "Broadcast Settings": {
    "Broadcast spawn amount": false,
    "Broadcast types separately": false
  }
}
```

### Spawn Settings

* **Spawn near players** - Do zombies spawn near to players (randomly chosen), if false zombies will spawn randomly around the map.
* **Minimum pop for near player spawn** - The minimum server population for zombies to spawn near players.
* **Min distance from player** - When spawning near players, this is the minimum distance that zombies are allowed to spawn from the player.
* **Max distance from player** - When spawning near players, this is the maximum distance that zombies are allowed to spawn from the player.
* **Spawn Time** - The in-game time at which zombies will appear.
* **Destroy Time** - The in-game time at which zombies will disappear.
* **Spawn delay** - The time in seconds between zombie spawns, increasing this value may help with lag or crashes when spawning zombies.

#### Chance Settings

* **Chance per cycle** - Percentage chance that zombies will spawn each spawn attempt.
* **Days between spawn** - The number of days between spawn attempts.

### Destroy Settings

* **Leave Corpse, when destroyed** - Are corpses left when zombies disappear (can affect performance when set to true).
* **Leave Corpse, when killed by player** - Are corpses left when zombies are killed by players.

### Behaviour Settings

* **Zombies attacked by outpost sentries** - Are zombies attacked by the sentries at safezones.
* **Ignore Human NPCs** - Do zombies ignore npc player characters.
* **Ignored entities (full entity shortname)** - Zombies will not target entities in this list, must be the full short prefab name.

## Notes

The 0.9.0 and 2.0 versions are still available to download if you want them.

* You can find v0.9.0 and its documentation [here](https://github.com/0x89A/Night-Zombies/blob/deprecated-v0.9.0)
* You can find v2.0 and its documentation [here](https://github.com/0x89A/Night-Zombies/tree/deprecated-v2.0)
