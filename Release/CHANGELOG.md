# Changelog

## 1.0.0
- Added Stay Home mode — companions remain near a set home position instead of following the player
- Stay Home works alongside any action mode (gather, follow, etc.) as a separate toggle
- Added auto-deposit — companions in Stay Home + gather mode automatically find the nearest chest and deposit when overweight
- Added 50m home leash — companions in Stay Home mode only harvest resources within 50m of their home position
- Added deferred tool equip — tool swaps that fail mid-attack animation now retry automatically during movement
- Fixed deer engage/clear log spam — abandoned targets are now silently cleared before logging engagement
- Fixed DoorHandler scanning while companion is sleeping or sitting
- Fixed false stuck timeouts caused by attack animation locks during harvest movement
- Fixed deposit re-trigger loop when walking to chest while overweight
- Fixed CancelPendingCart not respecting Stay Home mode

## 0.0.13
- Added randomized speech pools for all companion actions (combat, hunger, repair, gather, idle)
- Added directed repair — point at a CraftingStation to send companion to repair gear there
- Added directed board — point at a ship to make companion board it
- Added directed move — point at the ground to send companion to that position
- Added directed gather — point at a resource to start gathering that type
- Fixed bed attach/detach positioning
- Added action preemption — new commands cancel in-progress actions cleanly

## 0.0.12
- Rewrote CompanionAI as a custom BaseAI subclass replacing MonsterAI entirely
- Added universal companion commands via gamepad/keyboard hotkey system
- Added directed interactions — point at objects to command companions contextually
- Full controller support for all companion commands and UI navigation
- Eliminated all MonsterAI/BaseAI reflection — CompanionAI owns targeting, follow, and combat directly

## 0.0.11
- Added defensive parry AI — companions block and parry incoming attacks
- Added workbench auto-repair — companions walk to CraftingStations and repair worn gear
- Added UI freeze support — companion stays still while interaction panel is open
- Full controller support for the companion interaction panel (JoyTabLeft/Right tab navigation)

## 0.0.10
- Added manual bow draw system for ranged combat
- Fixed harvest approach distance calculation
- Added deer abandon cooldown — companions stop chasing fleeing animals they can't catch

## 0.0.9
- Added combat AI with melee engagement, retreat, and re-engage logic
- Added door handling — companions open doors when stuck, close them behind
- Added weapon/armor durability bars on companion UI
- Fixed follow target restoration and weapon swap issues

## 0.0.8
- Added tabbed companion interaction UI (Customize, Inventory, Actions)
- Added overhead stamina and health bars
- Added carry weight limit — companions stop gathering at 298 weight and speak about being overweight
- Added inventory logging and diagnostic tools

## 0.0.7
- Prioritize fallen logs and stumps over standing trees (3x distance penalty for TreeBase)
- Improved target selection for wood gathering

## 0.0.6
- Added drop collection after destroying resources — companion picks up nearby items
- Added stump targeting for wood gathering
- Added attack shuffle-closer when out of range
- Added UI freeze — companion pauses during interaction panel

## 0.0.5
- Rebuilt harvest system from scratch with reliable movement and pathfinding
- Fixed companion getting stuck during resource gathering
- Improved state machine transitions

## 0.0.4
- Refactored companion AI into modular subsystem architecture
- Separated combat, harvest, repair, food, and stamina into individual controllers

## 0.0.3
- Internal updates and stability improvements

## 0.0.2
- Refined companion UI layout, inventory slots, and action controls
- Improved panel styling and button layout

## 0.0.1
- Initial release — placeholder Companions tab integrated into Haldor's TraderUI
- Dynamic tab injection via Harmony patches on TraderOverhaul
