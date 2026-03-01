# Changelog

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
