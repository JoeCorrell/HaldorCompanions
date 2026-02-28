<div align="center">

# Haldor's Companions

Hire NPC companions from Haldor that **fight**, **gather**, **haul**, **repair**, and **follow** you across the tenth world.

[![Version](https://img.shields.io/badge/Version-1.0.0-blue?style=for-the-badge)](https://github.com/JoeCorrell/HaldorCompanions/releases)
[![BepInEx](https://img.shields.io/badge/BepInEx-5.4.2200+-orange?style=for-the-badge)](#requirements)
[![Valheim](https://img.shields.io/badge/Valheim-0.219+-green?style=for-the-badge)](#requirements)

---

<p align="center">
<a href="https://ko-fi.com/profmags">
<img src="https://storage.ko-fi.com/cdn/kofi3.png?v=3" alt="Support me on Ko-fi" width="300" style="border-radius: 0;"/>
</a>
</p>

---

## Overview

Companions are persistent NPC allies purchased from Haldor's trader shop for **2,000 coins**. Each companion has their own inventory, equipment, stamina, food system, and AI — they aren't pets or tames, they're teammates. Customize their appearance, gear them up, set their combat stance, and command them with a point-and-click hotkey system. They'll fight beside you, chop wood while you build, haul your cart to the next biome, and repair their own gear at your workbench.

---

<h3>Purchase</h3>
<p>Visit Haldor and open the <strong>Companions</strong> tab to recruit a new companion. Each companion costs 2,000 coins from your bank balance. Customize their name, gender, hair, beard, and colors before confirming.</p>

<hr/>

<h3>Inventory</h3>
<p>Companions carry their own 5x6 inventory (300 weight capacity). Equip them with weapons, shields, and armor — they'll auto-equip the best available gear. Feed them food for bonus health and stamina, just like a player.</p>

<hr/>

<h3>Commands</h3>
<p>Point your crosshair and press the command hotkey to issue contextual orders. The companion figures out what to do based on what you're looking at.</p>

<hr/>

## Features

### Combat AI
Companions engage enemies automatically with melee or ranged weapons. They dodge incoming attacks, parry with shields, deliver power attacks on staggered foes, and retreat to recover when low on health or stamina. Four combat stances let you control their aggression:

- **Balanced** — default behavior, fights when threatened
- **Aggressive** — engages at longer range, retreats only when critical
- **Defensive** — blocks more, retreats earlier, stays closer
- **Passive** — never initiates combat, follows only

<hr/>

### Resource Gathering
Set a companion to **Gather Wood**, **Gather Stone**, or **Gather Ore** and they'll autonomously find, walk to, and harvest nearby resources. They prioritize fallen logs and stumps over standing trees, collect all dropped items after each hit, and stop when overweight. Point at a specific resource to direct them to it.

<hr/>

### Stay Home + Auto-Deposit
Toggle **Stay Home** to anchor a companion near a set position. They'll patrol within 50m of home instead of following you. Combine with a gather mode and they'll harvest resources near home, then automatically find the nearest chest and deposit their haul when full — no player interaction needed. Perfect for base-side wood farms and mining outposts.

<hr/>

### Directed Commands
Point your crosshair at objects and press the command hotkey:

| Target | Action |
|---|---|
| **Resource** (tree, rock, ore) | Enter gather mode for that resource type |
| **CraftingStation** (forge, workbench) | Walk to station and repair all compatible gear |
| **Cart** | Attach to cart and haul it (press again to detach) |
| **Ship** | Board the ship |
| **Bed** | Go to sleep |
| **Chest** | Walk to chest and deposit non-equipped items |
| **Ground** | Walk to that position and wait |
| **Hold hotkey** | Cancel all commands, come back to player |

<hr/>

### Auto-Repair
Companions periodically scan their equipped gear. When durability drops below threshold, they'll walk to the nearest compatible CraftingStation and repair everything they can. Point at a specific station to prioritize it.

<hr/>

### Door Handling
Companions detect when they're stuck behind a closed door and automatically open it, walk through, and close it behind them. They also proactively find doors when circling a building trying to reach the player.

<hr/>

### Food System
Companions have three food slots, just like players. Feed them via the inventory panel — food provides bonus health, stamina, and passive regeneration. They'll speak up when hungry.

<hr/>

### Equipment & Durability
Companions auto-equip the best weapon, shield, and armor from their inventory. Weapons lose durability on use. Armor reduces incoming damage. Overhead durability bars show equipment condition at a glance.

<hr/>

### Companion Interaction Panel
Press **E** on a companion to open the tabbed interaction panel:

- **Customize** — rename, change appearance (gender, hair, beard, skin/hair color)
- **Inventory** — drag items in/out, feed food, view durability bars
- **Actions** — set action mode, combat stance, toggle Stay Home, set home position

Full mouse and controller support with tab navigation via bumpers.

<hr/>

### Persistence
Companions persist across sessions, zone transitions, and player deaths. Their appearance, name, inventory, equipment, action mode, home position, and all state is stored in ZDO and survives server restarts. Follow targets are automatically restored after player respawn.

<hr/>

## Installation

1. Install [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) for Valheim
2. Install [Trader Overhaul](https://github.com/JoeCorrell/TraderOverhaul) (required dependency)
3. Download the latest release
4. Extract to `BepInEx/plugins/Companions/`
5. Launch the game

<hr/>

## Requirements

- Valheim
- BepInEx 5.4.2200 or newer
- [Trader Overhaul](https://github.com/JoeCorrell/TraderOverhaul) (provides the trader UI that Companions integrates into)

<hr/>

## Companion Stats

| Stat | Value |
|---|---|
| **Price** | 2,000 coins (from bank) |
| **Base Health** | 25 HP (+ food bonus) |
| **Base Stamina** | 25 (+ food bonus) |
| **Carry Weight** | 300 |
| **Walk Speed** | 2 m/s |
| **Run Speed** | 7 m/s |
| **Gather Stop Weight** | 298 (stops harvesting) |
| **Home Leash Radius** | 50m (Stay Home mode) |

<hr/>

## Compatibility Notes

- Requires **Trader Overhaul** — the Companions tab is injected into its custom trader UI
- Companions use a custom `HC_Companion` prefab registered at startup
- Other NPC/follower mods should be compatible unless they patch `BaseAI` broadly
- Multiplayer: companions are owned by the spawning player via ZDO ownership

<hr/>

## Credits

Built on [BepInEx](https://github.com/BepInEx/BepInEx) and [Harmony](https://github.com/pardeike/Harmony)<br/>
Integrates with [Trader Overhaul](https://github.com/JoeCorrell/TraderOverhaul) for trader UI

[![GitHub](https://img.shields.io/badge/GitHub-Issues-181717?style=for-the-badge&logo=github)](https://github.com/JoeCorrell/HaldorCompanions/issues)
[![Discord](https://img.shields.io/badge/Discord-@profmags-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.com)

</div>
