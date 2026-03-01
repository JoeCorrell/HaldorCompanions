<div align="center">

<br/>

<h1 align="center">Offline Companions</h1>

<h3 align="center">Hire NPC companions from Haldor's shop - persistent allies with their own AI, inventory, combat, and gathering systems.</h3>

<br/>

<p align="center">
<a href="https://github.com/JoeCorrell/OfflineCompanions/releases"><img src="https://img.shields.io/badge/Version-0.0.4--beta-c9a44a?style=for-the-badge&labelColor=0d1117" alt="Version"></a>
<a href="#-requirements"><img src="https://img.shields.io/badge/BepInEx-5.4.2200+-e06c20?style=for-the-badge&labelColor=0d1117" alt="BepInEx"></a>
<a href="#-requirements"><img src="https://img.shields.io/badge/Valheim-0.219+-4ade80?style=for-the-badge&labelColor=0d1117" alt="Valheim"></a>
<a href="#"><img src="https://img.shields.io/badge/License-MIT-7c3aed?style=for-the-badge&labelColor=0d1117" alt="License"></a>
</p>

<p align="center">
<a href="https://ko-fi.com/profmags">
<img src="https://storage.ko-fi.com/cdn/kofi3.png?v=3" alt="Support me on Ko-fi" width="280"/>
</a>
</p>

<p align="center">
<img src="https://img.shields.io/badge/%E2%9A%A0%EF%B8%8F_EARLY_ACCESS_BETA-cc3333?style=for-the-badge&labelColor=0d1117" alt="Warning">
</p>

<table><tr><td width="900" align="center">
<br/>

This is an early development build intended for testing. Expect bugs, rough edges, and incomplete features. Saves should be safe, but **back up your world before installing**. Feedback and bug reports are greatly appreciated. You're helping shape the mod by testing it now!

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%93%B8_SCREENSHOTS-4a4a4a?style=for-the-badge&labelColor=4a4a4a" alt="Screenshots">
</p>

<table><tr><td width="900" align="center">
<br/>

<img src="https://raw.githubusercontent.com/JoeCorrell/HaldorCompanions/main/Screenshots/Radial.png" alt="Radial Command Wheel" width="800"/>

<br/><br/>

<img src="https://raw.githubusercontent.com/JoeCorrell/HaldorCompanions/main/Screenshots/UI.png" alt="Companion Inventory Panel" width="800"/>

<br/><br/>

<img src="https://raw.githubusercontent.com/JoeCorrell/HaldorCompanions/main/Screenshots/Trader.png" alt="Trader Purchase Screen" width="800"/>

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%93%96_OVERVIEW-2b3a4a?style=for-the-badge&labelColor=2b3a4a" alt="Overview">
</p>

<table><tr><td width="900">
<br/>

Offline Companions adds persistent NPC allies to Valheim through Haldor's trader shop. Companions cost **2,000 coins** and come with their own inventory, equipment, stamina, food system, and custom AI. They aren't pets or tames, they're **teammates**.

Customize their appearance at purchase, gear them up with weapons and armor, feed them food for bonus stats, and command them through a radial wheel or point-and-click hotkey system. They'll fight beside you, gather resources while you build, haul your cart, repair their own gear, sit by the fire with you, and sleep in beds when you tell them to.

Everything persists across sessions, zone transitions, server restarts, and player deaths.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%9B%92_GETTING_STARTED-3a2b4a?style=for-the-badge&labelColor=3a2b4a" alt="Getting Started">
</p>

<table><tr><td width="900">
<br/>

### Purchase
Visit Haldor and open the **Companions** tab to recruit a new companion. Each costs **2,000 coins** from your bank balance. Customize their gender, hair, beard, skin tone, and hair color in the 3D preview before confirming.

### Interact
**Tap E** on your companion to open their inventory panel. Manage gear, feed food, rename them. **Hold E** (or **X on gamepad**) to open the radial command wheel for quick access to all action modes and toggles.

### Command
Point your crosshair at objects in the world and press the command hotkey to issue contextual orders. The companion figures out what to do based on what you're looking at.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%8E%AF_RADIAL_COMMAND_WHEEL-d4a017?style=for-the-badge&labelColor=0d1117" alt="Radial Menu">
</p>

<table><tr><td width="900">
<br/>

**Hold E** (keyboard) or **X** (gamepad) on a companion to open the radial command wheel. Move the mouse or gamepad stick to highlight an option, then click or press E to select. You can select multiple options before closing. Press **Escape** or **B** to close.

The companion's name is shown in the center. Active toggles show their current ON/OFF state.

| Segment | Type | Description |
|:---|:---|:---|
| **Follow** | Toggle | Companion follows you (default ON). Can be combined with any gather mode. Overrides Stay Home when ON |
| **Gather Wood** | Mode | Autonomously find and chop trees, logs, and stumps nearby |
| **Gather Stone** | Mode | Autonomously find and mine rocks nearby |
| **Gather Ore** | Mode | Autonomously find and mine ore deposits nearby |
| **Forage** | Mode | Autonomously find and pick berry bushes, mushrooms, flowers, and ground items nearby |
| **Smelt** | Mode | Autonomously refill kilns and furnaces with fuel/ore from chests, collect smelted output |
| **Stay Home** | Toggle | Patrol the home position instead of following you |
| **Set Home** | Action | Save the companion's current position as their home point |
| **Wander** | Toggle | Roam up to 50m around home (ON) or stay put (OFF) |
| **Auto Pickup** | Toggle | Automatically pick up nearby dropped items |
| **Command** | Toggle | Accept directed commands from the point-to-command hotkey |

> Gather modes are mutually exclusive. Selecting a gather mode switches away from the previous one. Tapping an active gather mode deselects it. Follow is an independent toggle that can be combined with any mode.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%8E%92_INVENTORY_%26_EQUIPMENT-2896a5?style=for-the-badge&labelColor=0d1117" alt="Inventory">
</p>

<table><tr><td width="900">
<br/>

**Tap E** on a companion to open the inventory panel alongside the standard inventory GUI. The panel shows:

- **Name field** - rename your companion (persists in save)
- **5x6 inventory grid** (30 slots, 300 weight capacity) - drag items in and out, view durability bars, equipped items highlighted in blue
- **3 food slots** - showing active food effects and remaining duration

Companions **auto-equip the best gear** from their inventory: best weapon, shield, chest, legs, helmet, shoulder, and utility item. Items equip one at a time with proper animation. Broken items (0 durability) are skipped and unequipped.

Right-click an item to use/equip it. Right-click food to feed it to the companion. Drag items between your inventory and theirs using vanilla drag-and-drop.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%A7%A0_AI_SYSTEM-5b3a8a?style=for-the-badge&labelColor=0d1117" alt="AI System">
</p>

<table><tr><td width="900">
<br/>

Companions run on a **custom AI system** (`CompanionAI`) built from scratch on top of Valheim's `BaseAI` pathfinding. This is not a repurposed MonsterAI. It's a purpose-built AI loop designed specifically for companion behavior.

### Follow & Formation
When following you, each companion is assigned a formation slot. Multiple companions spread out around you instead of stacking on top of each other. When far away (>15m), they sprint straight to you; when close, they maintain formation offset. They use vanilla pathfinding and navigation mesh for movement.

### Target Management
Companions scan for enemies every 2-6 seconds depending on distance from you. Once they lock onto a target, they **commit** and won't bounce between enemies. A directed target from the command hotkey locks for 10 seconds. In gather modes, targeting is suppressed unless an enemy enters **self-defense range** (10m).

### Sleep & Wake
Companions support Valheim's sleep/wake RPC system. They can be directed to sleep in beds and will wake automatically when enemies approach.

### Stuck Detection
Built-in stuck detection nudges companions clear of furniture colliders, beds, and chairs that block pathfinding. Door handling detects when a companion is stuck behind a closed door and automatically opens, passes through, and closes it.

### Stay Home Patrol
When Stay Home is active, the AI switches from following you to patrolling the home position. Combined with gather modes, they'll autonomously harvest resources near home without you being present.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%E2%9A%94%EF%B8%8F_COMBAT_AI-c9444a?style=for-the-badge&labelColor=0d1117" alt="Combat AI">
</p>

<table><tr><td width="900">
<br/>

Companions use a **defensive-first combat system**. They actively scan for incoming threats and react before attacking.

### Melee Combat
- **Threat detection** - scans nearby enemies for attack animations and incoming projectiles
- **Shield blocking** - raises shield when threats are active, holds block through the attack
- **Perfect parry** - every timed block is a perfect parry (block timer reset on impact)
- **Counter-attacks** - drops shield and strikes immediately after blocking
- **Power attacks** - delivers a heavy attack when an enemy is staggered (3s cooldown)
- **Dodge** - sidesteps perpendicular to incoming attacks when stamina allows

### Ranged Combat
- Equips a bow when the target is beyond 20m, switches back to melee under 12m
- Arrows are aimed at the target's center mass with **velocity leading** (aims ahead of moving targets) and **gravity compensation** (aims higher for arrow drop)

### Combat Behavior
- **Retreats** when health drops below 30% or stamina below 15%
- **Re-engages** after recovering above 50% health and 30% stamina
- Retreat distance is 12m from the target
- Tools and pickaxes are **never used in combat**. Auto-equip forces a switch to a proper weapon

### Stamina System
Companions have their own stamina pool (base 25 + food bonus) with regeneration. Stamina is consumed by attacks, blocking, running, and swimming. When stamina hits zero, attacks fail and blocks don't hold.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%AA%93_RESOURCE_GATHERING-4a9c5e?style=for-the-badge&labelColor=0d1117" alt="Resource Gathering">
</p>

<table><tr><td width="900">
<br/>

Set a companion to **Gather Wood**, **Gather Stone**, **Gather Ore**, or **Forage** via the radial wheel or directed command. They'll autonomously find, walk to, and harvest nearby resources.

### Gather Behavior
- **Wood** - chops trees, fallen logs, and stumps. Prioritizes fallen logs and stumps over standing trees (3x distance penalty on standing trees).
- **Stone** - mines rock formations (MineRock, MineRock5).
- **Ore** - mines ore deposits (pickaxe-vulnerable destructibles that are chop-immune).
- **Forage** - walks to and picks berry bushes, mushrooms, flowers, dandelions, branches, and stones. No tool required.

### Smart Tool Use
The companion automatically equips the best matching tool from their inventory. Axe for wood, pickaxe for stone and ore. The tool stays equipped until gathering stops.

### Drop Collection
After destroying a resource, the companion scans within 8m for item drops and picks them all up before moving to the next target.

### Overweight
Gathering stops automatically at **298/300 weight**. The companion reverts to Follow mode and announces they're overweight.

### Self-Defense
If an enemy enters within 10m during gathering, the companion pauses to fight, then resumes gathering once the threat is gone.

> Point at a specific tree, rock, or ore node and press the command hotkey to direct the companion straight to it.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%94%A5_SMELTING_AUTOMATION-e06c20?style=for-the-badge&labelColor=0d1117" alt="Smelting Automation">
</p>

<table><tr><td width="900">
<br/>

Set a companion to **Smelt** via the radial wheel and they'll autonomously manage nearby kilns and furnaces. Place them near your smelting setup with chests of ore and fuel, and they'll handle the rest.

### How It Works
The companion continuously scans for smelters within 25m and keeps them running:

1. **Refill kilns** first (they produce charcoal for furnaces)
2. **Refill furnaces** with smart priority: ore first when fuel is adequate, fuel when critically low
3. **Collect smelted output** (bars, coal) from ground drops and queued output
4. **Deposit output** into nearby chests with available space

### Smart Behavior
- **Inventory-first**: if the companion already has fuel or ore in their inventory, they go straight to the smelter instead of visiting a chest
- **Chest animations**: opening and closing chests plays the proper animation and sound effects
- **Smelter effects**: inserting fuel or ore plays the smelter's sound effects and feeding animation
- **Correct positioning**: the AI navigates to the correct interaction side of each smelter (fuel side, ore side, output side)
- **Carry limits**: up to 20 ore or 40 fuel per trip to prevent overweighting

### Combine with Stay Home
Set **Stay Home + Smelt** and the companion will manage your smelting operation autonomously near their home position. Perfect for unattended base smelting while you're out exploring.

> Companions will pause smelting to fight any enemies that enter self-defense range, then resume when the threat is gone.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%91%86_POINT--TO--COMMAND-d4a017?style=for-the-badge&labelColor=0d1117" alt="Directed Commands">
</p>

<table><tr><td width="900">
<br/>

Point your crosshair at objects in the world and press the **command hotkey** to issue contextual orders. All owned commandable companions receive the command simultaneously.

| Target | Action |
|:---|:---|
| **Enemy** | Direct attack, locks target for 10 seconds |
| **Tree / Rock / Ore** | Enter gather mode for that resource, directed to that specific node |
| **Crafting Station** | Walk to station and repair all compatible gear |
| **Cart** | Closest companion attaches to cart and hauls it (press again to detach) |
| **Ship** | Board the ship and stay on deck |
| **Bed** | Walk to bed and sleep (press again to wake) |
| **Fireplace** | Walk to fire and sit down |
| **Chest** | Walk to chest and deposit non-essential items (keeps equipped gear, food, weapons, armor) |
| **Door** | Walk to door and open it |
| **Ground / Terrain** | Walk to that position and wait |
| **Nothing / Sky** | Cancel all commands, return to following you |

**Long press** the command hotkey (0.4s) to recall all companions. Cancels everything and restores follow mode. They'll say "Coming!" and head straight to you.

> The command hotkey must be configured in the BepInEx config file. Works on both keyboard and gamepad.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%8F%A0_STAY_HOME_%26_AUTO--DEPOSIT-4a7cc9?style=for-the-badge&labelColor=0d1117" alt="Stay Home">
</p>

<table><tr><td width="900">
<br/>

Toggle **Stay Home** in the radial to anchor a companion near their home position. Use **Set Home** to mark where they should stay. They'll patrol within range instead of following you.

Combine **Stay Home + Gather mode** and they'll harvest resources near home autonomously. When their inventory fills up (298 weight), direct them to a chest and they'll walk over and deposit everything except equipped gear, food, and weapons, then go right back to gathering.

Toggle **Wander** to control patrol range:
- **Wander ON** - roams up to 50m around home
- **Wander OFF** - stays exactly at the home point

> Perfect for base-side wood farms and mining outposts. Set them up and leave.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%94%A7_AUTO--REPAIR-e06c20?style=for-the-badge&labelColor=0d1117" alt="Auto-Repair">
</p>

<table><tr><td width="900">
<br/>

Companions periodically scan their equipped gear. When any item drops below **50% durability**, they'll walk to the nearest compatible crafting station (workbench, forge, etc.) and repair everything they can.

Point at a specific crafting station and press the command hotkey to direct them there immediately, regardless of durability threshold.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%8D%96_FOOD_SYSTEM-d4577a?style=for-the-badge&labelColor=0d1117" alt="Food System">
</p>

<table><tr><td width="900">
<br/>

Companions have **three food slots** that work exactly like player food. Same bonuses, same burn timers, same front-loaded curve.

- **Base health:** 25 HP + food bonus
- **Base stamina:** 25 + food bonus
- Food provides health regen, stamina regen, and Eitr bonuses
- Companions **auto-consume food** from their inventory when a slot is empty
- **Meads are used automatically** - health meads when below 50% HP, stamina meads when below 25% stamina (10s cooldown)
- Status effects from food and meads are applied on consumption

Feed food by right-clicking consumables in the companion's inventory, or let auto-consume handle it. They'll speak up when hungry.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%9B%A1%EF%B8%8F_EQUIPMENT_%26_DURABILITY-7c3aed?style=for-the-badge&labelColor=0d1117" alt="Durability">
</p>

<table><tr><td width="900">
<br/>

Companion gear works like player gear:

- **Weapons** lose durability on every attack. When broken, they're unequipped automatically.
- **Armor** absorbs damage and loses durability when hit. Full vanilla armor reduction formula applies.
- **Durability bars** appear on inventory slots and overhead when looking at the companion.
- Broken items are skipped by auto-equip and must be repaired at a crafting station.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%9A%AA_DOOR_HANDLING-7c6c4f?style=for-the-badge&labelColor=0d1117" alt="Door Handling">
</p>

<table><tr><td width="900">
<br/>

Companions detect when they're stuck behind a closed door and automatically open it, walk through, and close it behind them. They also proactively scan for doors when circling a building trying to reach you.

Respects ward protection and locked doors. Companions won't open doors they shouldn't.

> You can also point at a door and press the command hotkey to tell them to open it directly.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%94%A5_REST_%26_CAMPFIRE-e06c20?style=for-the-badge&labelColor=0d1117" alt="Rest">
</p>

<table><tr><td width="900">
<br/>

### Campfire Sitting
When you sit by a burning campfire (using the sit emote), nearby companions in Follow mode will join you. They walk to the fire and sit down facing it. They'll stand up if you do, if enemies appear, or if the fire goes out.

Point at a fireplace and press the command hotkey to explicitly tell them to sit.

### Bed Sleeping
Point at a bed and press the command hotkey to tell companions to sleep. They'll walk to the bed, lie down, and stay asleep until you wake them (same command again) or enemies appear.

### Resting Benefits
While sitting or sleeping, companions heal **2 HP/sec** and their stamina regeneration is **doubled**.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%92%AC_COMPANION_SPEECH-4a7cc9?style=for-the-badge&labelColor=0d1117" alt="Speech">
</p>

<table><tr><td width="900">
<br/>

Companions have context-aware overhead speech that plays every 20-40 seconds. Lines are chosen based on what's happening:

| Context | Example Lines |
|:---|:---|
| **Combat** | "Take this!", "For Odin!", "I've got your back!" |
| **Overweight** | "My back is hurting...", "I can't carry any more..." |
| **Hungry** | "I'm starving...", "Got any food?" |
| **Needs Repair** | "My gear is worn.", "Need repairs." |
| **Gathering** | "Found some!", "This looks promising." |
| **Foraging** | "Found some berries!", "These mushrooms look good." |
| **Smelting** | "Fetching fuel.", "Fetching materials.", "Everything's running. I'll keep watch." |
| **Following** | "Right behind you.", "Lead the way.", "Nice day for an adventure." |
| **Commands** | "On it!", "As you wish." |

Each directed command type (attack, sit, sleep, repair, deposit, etc.) also triggers its own immediate speech line.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%93%8A_OVERHEAD_HUD-2b4a3a?style=for-the-badge&labelColor=0d1117" alt="HUD">
</p>

<table><tr><td width="900">
<br/>

When looking at a companion, the vanilla enemy HUD is extended with two extra bars below the health bar:

- **Yellow bar** - current stamina
- **Brown bar** - current inventory weight (percentage of 300 max)

This lets you check a companion's status at a glance from a distance.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%93%8B_COMPANION_STATS-2b4a3a?style=for-the-badge&labelColor=2b4a3a" alt="Stats">
</p>

<table><tr><td width="900">
<br/>

| Stat | Value |
|:---|:---|
| **Price** | 2,000 coins (from bank) |
| **Base Health** | 25 HP (+ food bonus) |
| **Base Stamina** | 25 (+ food bonus) |
| **Carry Weight** | 300 |
| **Walk Speed** | 2 m/s |
| **Run Speed** | 7 m/s |
| **Gather Stop Weight** | 298 (stops harvesting) |
| **Home Leash Radius** | 50m (Stay Home mode) |
| **Auto-Repair Threshold** | 50% durability |
| **Retreat Threshold** | 30% HP or 15% stamina |
| **Re-engage Threshold** | 50% HP and 30% stamina |

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%92%BE_PERSISTENCE-2d7d4f?style=for-the-badge&labelColor=0d1117" alt="Persistence">
</p>

<table><tr><td width="900">
<br/>

Everything about a companion is stored in ZDO and persists across:

- Game sessions and server restarts
- Zone transitions and area loading
- Player deaths and respawns

Saved state includes: appearance, name, inventory, equipment, action mode, home position, all toggle states, food timers, and ownership. Follow targets are automatically restored after player respawn.

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%93%A6_INSTALLATION-4a3a2b?style=for-the-badge&labelColor=4a3a2b" alt="Installation">
</p>

<table><tr><td width="900">
<br/>

**1.** Install [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) for Valheim<br/>
**2.** Install [Trader Overhaul](https://github.com/JoeCorrell/TraderOverhaul) (required dependency)<br/>
**3.** Download the latest release from [Releases](https://github.com/JoeCorrell/OfflineCompanions/releases)<br/>
**4.** Extract to `BepInEx/plugins/Companions/`<br/>
**5.** Launch the game

<br/>
</td></tr></table>

<br/>

> [!IMPORTANT]
> **Trader Overhaul** must be installed first. The Companions tab is injected into its custom trader UI.

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%93%8B_REQUIREMENTS-3a4a2b?style=for-the-badge&labelColor=3a4a2b" alt="Requirements">
</p>

<table><tr><td width="900">
<br/>

| Dependency | Version | Link |
|:---|:---|:---|
| Valheim | `0.219+` | |
| BepInEx | `5.4.2200+` | [Download](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) |
| Trader Overhaul | `latest` | [GitHub](https://github.com/JoeCorrell/TraderOverhaul) |

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%94%97_COMPATIBILITY-4a2b3a?style=for-the-badge&labelColor=4a2b3a" alt="Compatibility">
</p>

<table><tr><td width="900">
<br/>

- Requires **Trader Overhaul**. The Companions tab is injected into its custom trader UI
- Companions use a custom `HC_Companion` prefab registered at startup
- Other NPC / follower mods should be compatible unless they patch `BaseAI` broadly
- **Multiplayer:** companions are owned by the spawning player via ZDO ownership. Other players cannot interact with companions they don't own

<br/>
</td></tr></table>

<br/>

<p align="center">
<img src="https://img.shields.io/badge/%F0%9F%99%8F_CREDITS-2b2b4a?style=for-the-badge&labelColor=2b2b4a" alt="Credits">
</p>

<table><tr><td width="900" align="center">
<br/>

Built on [BepInEx](https://github.com/BepInEx/BepInEx) and [Harmony](https://github.com/pardeike/Harmony)
<br/>
Integrates with [Trader Overhaul](https://github.com/JoeCorrell/TraderOverhaul) for trader UI

<br/>
</td></tr></table>

<br/>

<p align="center">
<a href="https://github.com/JoeCorrell/OfflineCompanions/issues"><img src="https://img.shields.io/badge/GitHub-Issues-181717?style=for-the-badge&logo=github&labelColor=0d1117" alt="GitHub Issues"></a>
<a href="https://discord.com"><img src="https://img.shields.io/badge/Discord-@profmags-5865F2?style=for-the-badge&logo=discord&logoColor=white&labelColor=0d1117" alt="Discord"></a>
</p>

<p align="center">
<sub>Forged for the Valheim community ❤️ Skol, Vikings.</sub>
</p>

</div>
