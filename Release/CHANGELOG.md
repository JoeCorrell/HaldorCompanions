# Changelog

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
