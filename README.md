## Master branch (v0.9.0)
### Configuration

```json
{
  "Murderer Population": 50,
  "Scarecrow Population": 50,
  "Kill Murderers": false,
  "Broadcast spawn amount": true,
  "Broadcast murderer and scarecrow count separately": false
  "Slow Destroy": false,
  "Slow destroy time (seconds)": 0.15
}
```

* **Murderer population** - Number of murderers per square kilometre.
* **Scarecrow population** - Number of scarecrows per square kilometre.
* **Kill murderers** - If set to true, murderes and scarecrows will die at sunrise leaving a corpse, if set to false they will be destroyed leaving no corpse.
* **Broadcast spawn amount** - Send chat message with number of murderers/scarecrows spawned (only initial spawn).
* **Broadcast murderer and scarecrow count separately** - Separate the number of murderers and scarecrows spawned in the chat broadcast.
* **Slow Destroy** - Kills/destroys zombies gradually instead of all at once. Use if there are a large number of zombies on your server, causing lag when destroyed all at once.
* **Slow destroy time (seconds)** - Time in seconds between each zombie being killed/destroyed with slow destroy.

## Rewrite branch (2.0.0)
### Configuration

## To use this config you will need to install the V2.0 version of the plugin, found under the updates tab.

```json
{
  "Spawn Settings": {
    "Murderer Population": 50.0,
    "Murderer Health": 100.0,
    "Scarecrow Population": 50.0,
    "Scarecrow Health": 100.0,
    "Reverse Spawn Timings": false,
    "Chance Settings": {
      "Chance per cycle": 100.0,
      "Days betewen spawn": 0
    }
  },
  "Destroy Settings": {
    "Leave Corpse": true,
  },
  "Behaviour Settings": {
    "Count if shortname contains": false,
    "Ignored entities": [
      "scientist",
      "scientistjunkpile",
    ]
  },
  "Broadcast Settings": {
    "Broadcast spawn amount": false,
    "Broadcast types separately": false
  }
}
```
* **Reverse Spawn Timings** - Reverse the spawning times so zombies spawn in the day and die during the night.
* **Chance per cycle** - Chance for zombies to spawn each time a spawn is attempted.
* **Days between spawn** - Number of in-game days until a spawn is attempted.
* **Ignored entities** - The shortnames of the entities that zombies and scarecrows will ignore and not attack.