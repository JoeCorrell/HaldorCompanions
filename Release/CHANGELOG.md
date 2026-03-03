# Changelog

## 1.0.9

### Bug Fixes
- Fixed companions with Follow OFF still teleporting via distance warp — `ApplyFollowMode` default case (modes 7/8/9: Hunt, Farm, Fish) set follow target to the player without checking the Follow toggle, causing the 50m distance warp to fire; now checks Follow toggle in all branches
- Fixed companions with Follow OFF still distance-warping — added explicit `GetFollow()` check to the distance teleport in `UpdateAI` as a safety net, so even if `m_follow` is set by a stale code path the companion won't warp
- Fixed StayHome companions wandering indefinitely during combat — companions with StayHome ON and Follow OFF could chase enemies far beyond their home area with no distance limit; added two-layer enforcement: target is dropped when the enemy exceeds 50m from home (soft leash), and companion is teleported back to home if they physically stray past 50m (hard leash)

### Balance
- Increased homestead task rotation timer from 15s to 60s per task (repair, refuel, sort, smelt) — companions now spend longer on each maintenance task before rotating to the next

### Death/Respawn State Persistence
- Fixed AutoPickup toggle lost on death — now saved in `RespawnData` and restored on respawn
- Fixed Wander toggle lost on death — now saved in `RespawnData` and restored on respawn
- Fixed Commandable toggle lost on death — now saved in `RespawnData` and restored on respawn
- Fixed FormationSlot lost on death — now saved in `RespawnData` and restored on respawn
- Fixed ActionModeSchema lost on death — now saved in `RespawnData` and restored on respawn, preventing legacy mode migration from re-running on respawned companions

## 1.0.8

### Bug Fixes
- Fixed companions not collecting their tombstone after UI interaction — opening the companion inventory during tombstone recovery caused `ApplyFollowMode` to override navigation, pulling the companion back to the player instead of the tombstone
- Fixed tombstone recovery stuck when pathfinding fails — added 10-second warp fallback for tombstones within 10m when NavMesh path cannot be found, so companions teleport directly to the tombstone instead of standing still
- Fixed tombstone recovery follow target leak — external systems (UI freeze/restore, CompanionSetup follow restoration) could re-set follow target mid-navigation; tombstone nav now forcefully clears follow every frame
- Fixed orphaned tombstone ID — `OnCompanionDeath` no longer generates a new tombstoneId when the companion has no items, preserving the existing ID so the respawned companion can still find its previous tombstone
- Fixed companions not respawning with follow target when home position is set — `DoRespawn` now restores follow target and home position independently instead of treating them as mutually exclusive
- Fixed companion tombstone inventory items lost on zone reload — vanilla `Inventory.Save/Load` does not persist grid dimensions; companion tombstones (8x4) reverted to default Container size (3x2), silently dropping items at out-of-bounds positions. Added ZDO-persisted dimensions (`HC_TombInvW`/`HC_TombInvH`) and a Container.Awake patch to restore them before the first Load cycle
- Fixed companion tombstone item positions causing invisible slots — items transferred via `MoveInventoryToGrave` retained their original grid positions (e.g., slot 28 in an 8x4 grid) which broke `InventoryGrid.UpdateGui`; items are now repacked to sequential positions after grave transfer
- Fixed companions not teleporting through portals when Stay Home is set — `StayHome` means "where to return when dismissed", not "never leave"; portal teleport now only checks Follow toggle and action mode, not StayHome flag
- Fixed companions not distance-teleporting (50m warp) when Stay Home is set — same StayHome misinterpretation; if `m_follow` is actively set, the companion teleports regardless of home position
- Fixed Hunt, Farm, and Fish modes not restoring follow target — follow restoration in `CompanionSetup.Update()` only covered modes 0-6; changed to `mode != ModeStay` to cover all current and future active modes
- Fixed portal warp not clearing directed states — companions arriving in a new zone via portal could retain stale cart attach, move target, deposit container, ship boarding, and tombstone recovery states from the old zone
- Fixed companion inventory grid overlapping food slots — expanding the panel height for food slots caused vanilla `InventoryGrid.UpdateGui` to re-center the grid content within the now-taller area, shifting it downward; the InventoryGrid's bottom edge is now inset to exclude the food slot region

### Directed Commands
- Added directed tombstone recovery — point at any companion tombstone and press the command key to send the nearest companion to walk over and loot it
- Added tombstone recovery speech lines ("I'll grab my things.", "Let me get that.", "On my way to pick that up.")

### Logging
- Upgraded tombstone navigation progress log from Debug to Info level — now shows `dist` and `pathOK` status for easier troubleshooting

## 1.0.7

### Bug Fixes
- Fixed companion duplication on death — added three-layer defense: `_dead` guard flag in `OnCompanionDeath` prevents re-entry, queue-level deduplication rejects identical owner+name pairs, and scene-level check aborts respawn if a living companion with the same identity already exists
- Fixed companion teleporting through portals when set to Stay Home — `CompanionAI.TeleportToFollowTarget()` (50m distance warp) now checks `GetStayHome()` before warping, so stay-home companions remain at their home position even when Follow is ON
- Fixed gamepad radial menu also opening inventory when holding X — moved the gamepad check in `Container.Interact` prefix before all mode-specific logic so gamepad always routes through `HandleGamepadHold()` regardless of RadialMenuKey config
- Fixed gamepad radial menu joystick control inverted — `GetJoyLeftStickY()`/`GetJoyRightStickY()` already negate raw Y; negating again so Atan2 receives positive=up for correct radial angles
- Fixed gamepad B button not closing radial menu — changed from `"JoyBack"` (Select/View button) to `"JoyButtonB"` (B/Circle) matching vanilla's cancel convention
- Fixed hover text showing keyboard binding "E" instead of gamepad button glyph — `GetHoverText()` now explicitly resolves `ZInput.GetBoundKeyString("JoyUse")` when gamepad is active
- Fixed homestead chest sorting losing items — `AddItem()` return value is now checked before removing from source inventory
- Fixed smelter output storage losing items — `AddItem()` return value is now checked; failed adds are logged and skipped instead of removing the item
- Fixed `Container.OnContainerChanged` duplicate subscription — `ZNetScenePatch` now removes existing callback before re-subscribing to prevent double-fire on inventory changes

### Gamepad / Controller
- Added independent gamepad hold detection — monitors `ZInput.GetButton("JoyUse")` directly while hovering a companion, bypassing the `Container.Interact` prefix chain and vanilla's 0.2s debounce entirely
- Split radial menu stick control — left stick controls outer ring (action modes), right stick controls inner ring (combat stances)
- Added gamepad inventory tab switching (RB/LB) — companion grid's `UIGroupHandler` is injected into `InventoryGui.m_uiGroups[0]` when the panel opens, enabling vanilla's `SetActiveGroup()` to cycle between companion and player grids
- Added D-pad edge navigation — pressing Up at the top of the companion grid switches focus to the player grid with correct column mapping

### Performance
- Armor durability patch reuses a static list instead of allocating per-hit
- Projectile threat scan uses a shared cache refreshed every 0.25s instead of `FindObjectsByType` per frame per companion
- Door cache uses absolute expiry time instead of per-frame countdown
- Sprite and texture caches (`CompanionPanel`, `CompanionRadialMenu`) now properly `Destroy()` native assets on teardown to prevent GPU memory leaks

## 1.0.6

### Bug Fixes
- Fixed Companions tab not appearing in Trader UI when Bounties mod is installed — assembly reference mismatch (`HaldorOverhaul` vs `TraderOverhaul`) caused `Prepare()` to silently disable all trader tab patches
- Fixed controller radial menu not opening — holding X on gamepad opened inventory instead of radial; `ZInput.GetButton("JoyUse")` is unreliable for continuous polling so gamepad hold detection now tracks `GetButtonUp` as a positive release signal instead

## 1.0.4

### Features
- Added UI Reposition Mode (F7) — press F7 while the companion inventory panel is open to drag and reposition it anywhere on screen; position persists across sessions

### Bug Fixes
- Fixed companion tombstone missing items — stale `m_equipped` flags from ZDO deserialization caused `MoveInventoryToGrave` to skip items not currently in a humanoid equipment slot
- Fixed companion inventory UI glitching on death — panel now closes immediately before inventory is emptied, preventing empty-grid rendering and vanilla container panel flash
- Fixed controller radial menu not opening — holding X on gamepad opened inventory instead of radial due to `ZInput.GetButton("JoyUse")` returning false on the deferred frame; added 60ms grace period before accepting button release

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
