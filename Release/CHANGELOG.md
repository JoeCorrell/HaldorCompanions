# Changelog

## 1.0.3

### Bug Fixes
- Fixed translations not loading — Unity 6's JsonUtility cannot deserialize arrays of serializable objects, replaced with regex-based JSON parser

## 1.0.2

### Translations
- Added translation files for all 22 Valheim-supported languages: Chinese, Chinese (Traditional), Czech, Danish, Dutch, Finnish, French, German, Greek, Hungarian, Italian, Japanese, Korean, Norwegian, Polish, Portuguese (Brazilian), Portuguese (European), Russian, Slovak, Spanish, Swedish, Turkish
- All UI labels, radial menu text, hover text, messages, and speech lines are fully translated

### Bug Fixes
- Fixed game freeze on startup caused by infinite recursion in localization initialization

## 1.0.0

### Homestead Mode (Stay Home Automation)
Companions left at home with Stay Home ON and Follow OFF now autonomously maintain your base. Tasks rotate every 15 seconds in a round-robin cycle: Repair, Refuel, Sort, then Smelt.

- **Repair building pieces** — scans for damaged player-built structures within 40m of home, walks to each one, plays hammer swing animation, and fully repairs them
- **Refuel fires and torches** — detects campfires, hearths, sconces, standing torches, and any refillable Fireplace below 30% fuel capacity, fetches the correct fuel type from nearby chests, and adds fuel one unit at a time
- **Sort and organize chests** — identifies items split across multiple chests, consolidates smaller stacks into larger ones with proper open/close animations and sound effects
- **Smelting rotation** — cycles smelting duties alongside other homestead tasks instead of running smelting exclusively
- All chest interactions are slow and animated: chest opens with creak sound, items transfer one-by-one at 0.6s intervals, chest closes with sound
- Only activates when Stay Home is ON, Follow is OFF, and the companion has a home position set

### Companion Respawn at Bed
- Companions now respawn at the last bed they slept in instead of the world spawn point
- Sleeping in a bed via the directed command sets that bed as the companion's spawn point
- On death, the companion's home position is preserved and restored on respawn
- Stay Home state is automatically restored when respawning at a home position
- Spawn position uses the bed's exact coordinates to prevent spawning on rooftops

### Portal and Dungeon Teleportation
- Companions now teleport with the player through portals and dungeon entrances
- Companions in Stay Home mode are excluded — only active followers teleport
- Works via ZDO position update so companions warp correctly even if their zone unloaded
- Cancels any active rest state (sitting/sleeping) before warping to prevent animation glitches

### Minimap Markers
- Companions are now marked on the minimap with a visible icon
- Markers are only visible to the companion's owner

### Skill Leveling
- Companions level up skills like the player with progressive buff gains
- Skill improvements affect combat effectiveness and gathering efficiency

### Rested Buff
- Companions receive the Rested buff when resting by a fire or sleeping in a bed
- Being in a Comfortable area (shelter + fire) triggers the buff automatically

### Smart Gathering Restrictions
- Companions won't chop trees if their equipped axe doesn't meet the tree's minimum tool tier requirement
- Prevents wasting durability on trees the companion can't actually damage

### Night Sleep Requirement
- Companions must be sleeping at night for the game's time-skip to pass
- Matches vanilla behavior where all players must be in bed to skip the night

### Drowning
- Companions slowly drown if their stamina is fully depleted while swimming
- Encourages feeding companions stamina food before ocean voyages

### Ship Boarding AI
- Point-to-Command on a ship tells companions to board and stay on deck
- Companions find the boat's ladder, path to it, interact with it, and teleport onto the deck
- Improved pathing AI for reliable ship boarding from shore

### Inventory UI Improvements
- Cloned vanilla container panel for authentic Valheim look (native icons, durability bars, tooltips, stack counts, quality indicators)
- Companion inventory grid now matches player grid size (8x4)
- Equipped item indicators match the player inventory style
- Added Split (Shift+Click) and Drop (Ctrl+Shift) modifiers to companion inventory
- Editable companion name input with custom background sprite
- Companion inventory weight display uses vanilla container weight style
- Food slots displayed at the bottom of the inventory panel

### Radial Menu Improvements
- Added configurable radial menu keybind (default E) — set to a different key to open the radial independently from interact
- Improved radial menu hold detection with raw Unity input for better reliability
- Dual-stick gamepad radial menu selection (whichever stick has greater magnitude)
- Camera direction lock on radial close to prevent camera jerk from held right stick
- Enlarged radial menu with two-ring layout: outer ring for action modes, inner ring for combat stances

### Combat
- Removed backstab damage multiplier from companions — enemies can no longer deal bonus damage by hitting companions from behind

### Localization Framework
- Full localization framework integrated with Valheim's built-in Localization system
- All UI labels, radial menu text, hover text, messages, and speech lines are translatable
- Translation files loaded from `Translations/{Language}.json` alongside the DLL
- English.json auto-generated on first run with all translation keys
- Speech lines support per-language files via `Translations/speech/{Language}.json`
- Backward compatible with existing `speech.json` customizations

### Other Changes
- Added voice audio system with gender-specific voice packs and per-gender config for text vs audio
- Added starter companion that spawns automatically in new worlds
- Externalized speech lines to `speech.json` for easy customization
- TraderOverhaul is now a soft dependency — mod works standalone without it
- Fixed smelter interaction deadlock at edge of use distance
- Fixed female companion invisible body (bodyfem mesh vertex stride mismatch)
- Fixed Stay Home + Wander/Gather not working correctly
- Fixed Eitr Weave and cross-tier items not being repaired
- Fixed follow target not restored after closing companion UI during harvest
- Fixed SuppressAttack not clearing when switching from Passive stance

## 0.1.1
- Improved companion inventory UI: cloned vanilla container panel for authentic Valheim look (native icons, durability bars, tooltips, stack counts, quality indicators)
- Companion inventory grid now matches player grid size (8x4)
- Equipped item indicators now match the player inventory style
- Added Split (Shift+Click) and Drop (Ctrl+Shift) modifiers to companion inventory
- Added configurable radial menu keybind (default E) — set to a different key to open the radial independently from interact
- Improved radial menu hold detection with raw Unity input for better reliability
- Added diagnostic logging for radial menu interaction to help troubleshoot reported issues
- Added editable companion name input with custom background sprite
- Companion inventory weight display now uses vanilla container weight style
- Food slots displayed at the bottom of the inventory panel

## 0.1.0
- Companions now teleport with the player through portals and dungeon entrances
- Companions in Stay mode are excluded — only active companions follow through teleports
- Works via ZDO position update so companions warp correctly even if their zone unloaded during the teleport
- TraderOverhaul is now a soft dependency — mod works standalone without it

## 0.0.9
- Companions now teleport with the player through portals and dungeon entrances
- Companions in Stay mode are excluded — only active companions follow through teleports
- Works via ZDO position update so companions warp correctly even if their zone unloaded during the teleport

## 0.0.8
- Added voice audio system: companions play MP3 voice clips alongside or instead of overhead speech text
- Gender-specific voice packs: separate `Audio/MaleCompanion/` and `Audio/FemaleCompanion/` folders, with automatic MaleCompanion fallback when female clips are missing
- Per-gender speech config: independent toggles for overhead text and voice audio per gender (male defaults to voice-only, female defaults to text-only)
- Added starter companion: a free companion automatically spawns when entering a new world for the first time (configurable via `SpawnStarterCompanion` setting)
- Externalized speech lines to `speech.json` for easy customization without recompiling
- Added 5-second per-companion speech cooldown to prevent overlapping text and audio
- Added voice audio categories for all speech events: Action, Combat, Follow, Forage, Gather, Hungry, Idle, Overweight, Repair, Smelt
- Merged RadialAction speech pool into Action (radial commands now use the same lines as mode changes)

## 0.0.7
- Fixed smelter interaction deadlock: companion got stuck when pathfinding reached closest point but 3D distance exceeded threshold (moveOk=True at 2.6m vs 2.5m UseDistance)
- SmeltController now accepts pathfinding "arrived" with relaxed distance tolerance for smelter insert and output collection
- Increased smelter interact offset from 1.2m to 1.3m for better switch-side navigation

## 0.0.6
- Improved controller support: dual-stick radial menu selection (whichever stick has greater magnitude)
- Added camera direction lock on radial close to prevent camera jerk from held right stick
- Added custom combat stance icons (Balanced, Aggressive, Defensive, Passive)
- Increased inner ring stance icon sizes to match outer ring icons
- Increased smelter interact offset so companions navigate to the correct side of smelters

## 0.0.5
- Added combat stances: Balanced, Aggressive, Defensive, and Passive selectable from inner radial ring
- Aggressive stance: extended 50m aggro range, no blocking/dodging, 15%/5% retreat thresholds, halved power attack cooldown, tighter flanking
- Defensive stance: only engages enemies within 5m or targeting the player, 45%/25% retreat thresholds, faster dodge cooldown, tighter formation
- Passive stance: suppresses all targeting and combat, companion only follows or idles
- Enlarged radial menu with two-ring layout: outer ring for action modes, inner ring for combat stances
- Added procedural fallback icons for all four combat stances
- Added stance info to periodic debug logs (CompanionAI state dump and CombatController heartbeat)
- Fixed SuppressAttack not clearing when switching from Passive to another stance without a combat target

## 0.0.4
- Added Smelt mode: companions autonomously refill kilns and furnaces with fuel and ore from nearby chests
- Companions collect smelted output (bars, coal) from furnace ground drops and queued output
- Companions deposit collected output into nearby chests with available space
- Smart per-smelter priority: ore first when fuel is adequate, fuel when critically low, kilns before furnaces
- Inventory-first refilling: companions use fuel/ore already in their inventory before making a chest trip
- Chest open/close animation and sound effects play when companions take or deposit items
- Smelter sound effects and feeding animation play when companions insert fuel or ore
- AI navigates to the correct side of smelters using switch interaction points
- Increased carry capacity per trip: 20 ore, 40 fuel (coal)
- Added Smelt segment to radial command wheel with custom icon
- Added Auto Pickup custom icon to radial command wheel
- Companions speak contextual lines while smelting ("Fetching fuel.", "Everything's running.", etc.)
- Updated README with Forage and Smelting Automation documentation

## 0.0.3
- Added Forage gather mode: companions find and pick berry bushes, mushrooms, flowers, branches, and stones
- Changed Follow from a mode to an independent toggle (Follow ON overrides Stay Home, works alongside gather modes)
- Added gather mode deselect: tapping an active gather mode turns it off
- Replaced procedural radial menu icons with custom PNG textures
- Added colored circle backgrounds behind each radial menu icon
- Enlarged radial menu and icons for better visibility
- Added foraging speech lines ("Found some berries!", "These mushrooms look good.", etc.)
- Fixed female companion invisible body (bodyfem mesh vertex stride mismatch in VisEquipment)
- Hidden overhead HUD bars when radial menu is open
- Suppressed companion speech bubbles when radial menu is open
- Lightened radial menu background and increased circle sprite resolution

## 0.0.2
- Fixed Stay Home + Wander not working (companions stood still due to suppressed move interval)
- Fixed Stay Home + Gather not working (gathering companions now wander to find new resource patches)
- Fixed companions unable to return home when wander is turned off while far away
- Fixed infinite re-approach cycle during harvesting causing companions to walk away and stand still
- Fixed Eitr Weave and other cross-tier items not being repaired (added vanilla worldLevel fallback)
- Fixed follow target not restored after closing companion UI during active harvest
- Fixed STUCK detection false positives when companion is intentionally stationary in StayHome mode
- Improved tree priority: fallen logs and stumps are now always preferred over standing trees (two-pass scan)
- Added inventory slot hover highlight for mouse and controller
- Added Command toggle to Dverger radial wheel
- Added overhead speech when executing radial commands
- Removed food slot separator line, tightened inventory panel spacing

## 0.0.1
- Initial beta release
- Hire NPC companions from Haldor with custom appearance (gender, hair, beard, skin/hair color)
- CompanionAI: custom BaseAI subclass with follow, combat, sleep/wake, and stuck detection
- Combat AI: melee engagement with retreat/re-engage, manual bow draw for ranged, defensive parry
- Harvest system: gather wood, stone, and ore with full state machine (Idle → Moving → Attacking → CollectingDrops)
- Auto-repair: companions walk to CraftingStations and repair worn gear
- Food system: 3 food slots with auto-consume, health/stamina regen
- Carry weight limit (300) with overweight speech and gather-stop
- Stay Home mode with 50m home leash, works alongside any action mode
- Auto-deposit: companions in Stay Home + gather mode deposit to nearest chest when overweight
- Wander toggle for idle roaming
- Auto-pickup toggle for nearby item collection
- Door handling: companions open doors when stuck, close them behind
- Directed commands: point at objects to command companions (repair at station, board ship, move to position, gather resource)
- Radial command wheel (Hold E) with all action toggles and mode selection
- Inventory-only interaction panel (Tap E) with 5x6 grid and 3 food slots
- Overhead health, stamina, and durability bars
- Context-aware overhead speech (combat, hunger, overweight, repair, gather, idle)
- Full gamepad/controller support for all UI and commands
- Companion tab injected into Haldor's TraderUI via Harmony patches
