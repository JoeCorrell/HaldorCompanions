# Changelog

## 1.1.2

### New
- **Point-to-command for smelters and kilns** — point at any smelter or kiln and press the command key to send your companion to tend it

### Bug Fixes
- Fixed companions only filling the first smelter or kiln when multiple are nearby — they now fill the emptiest one first and spread materials evenly
- Fixed items disappearing when a companion was interrupted while carrying materials to a smelter — items are now properly tracked in inventory
- Fixed companions planting seeds on top of wooden floors, walls, and other building pieces instead of on cultivated soil

## 1.1.1

### New Features

#### Autonomous Farming
Companions can now be set to **Farm** mode via the radial wheel. They will automatically harvest ripe crops, collect the drops, fetch seeds from nearby chests, plant them on cultivated soil in a grid, and deposit harvested crops into output chests.

- Supports all crops including modded ones
- Only harvests player-planted crops — ignores wild berries, mushrooms, etc.
- Needs a cultivator in the companion's inventory to plant; without one, they only harvest
- Alternates between harvesting and planting every 30 seconds so neither task gets neglected

#### Repair Buildings Mode
A new toggle on the radial wheel. When active, the companion walks around and repairs any damaged building pieces within 50m. Works while following you or while staying home.

#### Restock Mode
A new toggle on the radial wheel. When active, the companion refuels nearby fireplaces, campfires, and torches that are running low — pulling fuel from its own inventory or nearby chests.

#### In-Game Settings Panel (F8)
Press F8 to open a settings panel where you can tweak all mod options without editing config files. Also accessible from the "Mod Options" button added to the ESC pause menu.

### AI Improvements

#### Smarter Obstacle Avoidance
Companions now steer around walls, furniture, smelters, and other obstacles more smoothly when normal pathfinding can't find a route.

#### Half-Wall and Fence Navigation
Companions can now find their way around half-walls and fences by detecting them and looking for doorways and openings.

#### Movement Mirroring
Companions match your movement — walking when you walk, crouching when you crouch. They still run to catch up or during combat.

#### Better Stuck Recovery
When stuck behind obstacles, companions now try sidestepping in multiple directions. After repeated failures, they teleport to you.

#### Proactive Jumping
Companions automatically jump over small terrain ledges when they detect they're stuck on a step-up.

#### Hazard Awareness
Companions detect tar pits and deep water and actively try to reach shore. If they can't escape in time, they teleport to safety.

#### Water Avoidance
Companions check for water ahead and stop walking if their stamina is too low to swim safely.

#### Improved Pathfinding Over Water
Companions now use a pathfinding type that automatically routes around bodies of water.

#### Stuck Position Memory
Positions that keep getting the companion stuck are remembered for 30 seconds so they don't keep trying to go there.

#### Combat Flanking
If a companion gets stuck trying to reach an enemy, it tries approaching from the side instead.

#### Passive Resting
Companions now get the Rested buff just by standing near a fire inside a shelter — no need to tell them to sit down.

### Radial Menu
- Radial menu is 15% larger for better readability
- Added **Repair**, **Restock**, and **Farm** segments to the radial wheel

### Combat
- Added crossbow bolt support
- Defensive stance now only fights enemies that are directly attacking the companion or the player
- Companions walk instead of run when retreating with low stamina, and use their shield to block
- Companions can still hit enemies that get close during retreat
- Companions walk to conserve stamina when approaching enemies below 25% stamina

### Smelting
- Furnaces are now filled emptiest-first so all smelters get materials evenly
- "Nothing left to smelt" message only shows once per cycle instead of repeating
- Smelter positions that cause stuck navigation are remembered and skipped

### Harvesting
- Fixed trees on slopes being missed when the companion swings
- Drops are now picked up one at a time with a short pause between each for a more natural look
- Fixed companions getting stuck trying to re-approach a tree at exactly the right distance

### Death
- A minimap marker now appears at the companion's tombstone when they die, showing their name

### Bug Fixes
- Fixed minimap marker not clearing after reloading a scene
- Fixed MaxFoodSlots setting having no effect
- Fixed new modes (Repair/Restock) being reset back to Follow after loading
- Fixed restock fuel counter adding up across multiple fireplaces
- Fixed proactive jumping activating when the companion is just standing near you
- Fixed text not visible in the F8 settings panel
- Fixed stamina draining while standing still during combat
- Fixed Repair Buildings and Restock modes freezing after finding a target
- Fixed Farm mode not starting immediately when selected — now starts right away
- Fixed settings panel and radial menu not blocking player movement and camera

### Localization
- Added translation keys for all new features across all 22 languages

## 1.0.9

### Bug Fixes
- Fixed companions with Follow OFF still teleporting to the player when they get far away
- Fixed Stay Home companions chasing enemies too far from home — they now stop chasing after 50m and return home

### Balance
- Homestead task timer increased from 15 to 60 seconds — companions spend more time on each maintenance task before switching

### Death & Respawn
- Fixed Auto Pickup, Wander, Commandable, Formation Slot, and Action Mode settings being lost when a companion dies and respawns

## 1.0.8

### Bug Fixes
- Fixed companions not picking up their tombstone after opening their inventory
- Fixed tombstone recovery getting stuck when the path is blocked — they now teleport to it after 10 seconds
- Fixed other UI interactions interrupting tombstone recovery
- Fixed companions not respawning with follow target when they have a home position
- Fixed tombstone items being lost when leaving and re-entering the area
- Fixed invisible item slots in companion tombstones
- Fixed companions not teleporting through portals when Stay Home is set — Stay Home only means "return here when dismissed", not "never leave"
- Fixed companions not following through distance teleports when Stay Home is set
- Fixed Hunt, Farm, and Fish modes not restoring follow after task completes
- Fixed portal travel not clearing stale states from the previous zone
- Fixed companion inventory grid overlapping food slots

### New
- Point at a companion tombstone and press the command key to send the nearest companion to loot it

## 1.0.7

### Bug Fixes
- Fixed companion duplication on death
- Fixed companions teleporting through portals when set to Stay Home
- Fixed gamepad radial menu opening inventory instead of the radial wheel when holding X
- Fixed gamepad joystick direction being inverted on the radial menu
- Fixed B button not closing the radial menu on gamepad
- Fixed hover text showing "E" instead of the correct gamepad button
- Fixed homestead chest sorting losing items
- Fixed smelter output storage losing items

### Gamepad / Controller
- Improved gamepad hold detection for opening the radial menu
- Left stick controls action modes, right stick controls combat stances
- Added RB/LB to switch between companion and player inventory tabs
- Added D-pad navigation between companion and player inventory grids

### Performance
- Reduced memory allocations in armor durability, projectile scanning, door caching, and UI sprite handling

## 1.0.6

### Bug Fixes
- Fixed Companions tab not appearing in Trader UI when Bounties mod is installed
- Fixed controller radial menu not opening when holding X on gamepad

## 1.0.4

### New
- Press F7 while the companion inventory is open to drag and reposition it anywhere on screen — position saves across sessions

### Bug Fixes
- Fixed companion tombstone missing items
- Fixed companion inventory UI glitching on death
- Fixed controller radial menu not opening on gamepad

## 1.0.3

### Bug Fixes
- Fixed translations not loading

## 1.0.2

### Translations
- Added translations for all 22 Valheim-supported languages

### Bug Fixes
- Fixed game freeze on startup caused by localization initialization

## 1.0.0

### Homestead Mode (Stay Home Automation)
Companions left at home (Stay Home ON, Follow OFF) now automatically maintain your base. They rotate between tasks every 15 seconds:

- **Repair** — walks to damaged buildings and repairs them
- **Refuel** — finds low-fuel campfires and torches and refuels them from nearby chests
- **Sort chests** — consolidates split item stacks across multiple chests
- **Smelt** — handles smelting duties alongside other tasks

All chest interactions are animated with open/close sounds.

### Companion Respawn at Bed
- Companions respawn at the last bed they slept in instead of the world spawn point
- Home position and Stay Home state are preserved through death

### Portal and Dungeon Teleportation
- Companions follow you through portals and dungeon entrances
- Stay Home companions are excluded — only active followers teleport

### Minimap Markers
- Companions appear on the minimap (only visible to their owner)

### Skill Leveling
- Companions level up skills over time, improving combat and gathering

### Rested Buff
- Companions get the Rested buff from resting near a fire or sleeping in a bed

### Smart Gathering
- Companions won't try to chop trees if their axe isn't strong enough

### Night Sleep
- Companions must be sleeping at night for the time-skip to work (matches vanilla behavior)

### Drowning
- Companions slowly drown if their stamina runs out while swimming

### Ship Boarding
- Point at a ship to tell companions to board — they find the ladder and climb on

### Inventory UI
- Authentic Valheim-style companion inventory panel (8x4 grid)
- Equipped item indicators, split/drop modifiers, editable name, weight display, food slots

### Radial Menu
- Configurable radial menu key (default E)
- Dual-stick gamepad support
- Two-ring layout: outer ring for action modes, inner ring for combat stances

### Combat
- Removed backstab damage bonus against companions

### Localization
- Full translation framework integrated with Valheim's localization system
- Translation files loaded from `Translations/{Language}.json`

### Other
- Voice audio system with gender-specific voice packs
- Starter companion spawns automatically in new worlds
- Speech lines customizable via `speech.json`
- TraderOverhaul is now optional — mod works without it
- Various bug fixes for smelting, female companion appearance, Stay Home modes, repair, and combat

## 0.1.1
- Improved companion inventory UI to match vanilla Valheim style
- Added configurable radial menu keybind
- Added editable companion names
- Added food slot display at bottom of inventory

## 0.1.0
- Companions now teleport with you through portals and dungeons
- Stay Home companions don't teleport — only active followers do
- TraderOverhaul is now optional

## 0.0.9
- Companions now teleport with you through portals and dungeons

## 0.0.8
- Added voice audio system with gender-specific voice packs
- Added starter companion that spawns in new worlds
- Speech lines can now be customized via `speech.json`

## 0.0.7
- Fixed companion getting stuck when trying to use smelters at the edge of reach distance

## 0.0.6
- Improved controller support for radial menu
- Added custom combat stance icons
- Enlarged radial menu icons

## 0.0.5
- Added combat stances: Balanced, Aggressive, Defensive, and Passive
- Two-ring radial menu layout: outer ring for modes, inner ring for stances

## 0.0.4
- Added Smelt mode: companions automatically fuel and fill kilns and furnaces from nearby chests
- Companions collect smelted output and deposit it in chests
- Added Smelt and Auto Pickup icons to the radial wheel

## 0.0.3
- Added Forage mode: companions pick berries, mushrooms, flowers, branches, and stones
- Follow is now an independent toggle that works alongside gather modes
- Replaced procedural icons with custom PNG textures

## 0.0.2
- Fixed Stay Home + Wander and Stay Home + Gather not working
- Fixed various harvesting and repair bugs
- Improved tree priority — fallen logs preferred over standing trees
- Added Command toggle to radial wheel

## 0.0.1
- Initial beta release
- Hire companions from Haldor with custom appearance
- AI with follow, combat, sleep/wake, and stuck detection
- Harvest wood, stone, and ore
- Auto-repair gear at crafting stations
- Food system with 3 slots and auto-consume
- Stay Home mode, auto-deposit, wander, auto-pickup
- Door handling, directed commands, radial wheel, inventory panel
- Full gamepad/controller support
