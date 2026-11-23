# Changelog

## 3.4.1 - 2026-11-23

### Fixed

- Added a pre-collection capacity check so a craft with no room for even one
  complete recipe batch is rejected before Rust removes its ingredients.
- Made that check simulate Rust's ingredient collection order, including slots
  freed by fully consumed stacks, redirectable ingredients, unusable broken
  items, queued output reservations, and target-item stack room.
- Strengthened rejected-craft refunds to merge into main or belt containers
  before dropping. If an admin-created or plugin-controlled overstack cannot be
  restored under the current normal limit, the refund may reconstruct that
  pre-craft overstack rather than discarding the split on the ground.
- Kept temporary-blueprint crafts on the post-collection safety path because
  the `CanCraft` hook does not identify the blueprint item that will be removed.

## 3.4.0 - 2026-11-23

### Fixed

- Restored current Rust compatibility after removal of
  `ItemCraftTask.cancelled`.
- Restored non-zero craft times after switching a server away from instant
  crafting, including the active Rust era override.
- Reworked partial bulk crafting around whole recipe batches and exact
  ingredient refunds, preserving item metadata when a stack is split.
- Prevented malformed configuration files from being overwritten by defaults.
- Completed one active recipe batch safely during shutdown and refunded the
  uncrafted remainder instead of discarding its task and ingredients.
- Restored queued VIP tasks to durable original blueprints before plugin unload.
- Applied and restored workbench levels consistently in active-era overrides.
- Added null and bounds checks for item queries, workshop skins, task hooks,
  stack sizes, and numeric configuration values.

### Improved

- Added `/benchlvl item.shortname default`.
- Avoided rewriting unchanged configuration files on every startup.
- Included sleeping/offline players when protecting queued crafts at shutdown
  or plugin unload.
- Removed unused serialization and inventory-slot helpers.
- Made command numeric parsing invariant and reject NaN/infinity.

### Preserved

- All existing configuration property names, including upstream spelling.
- All commands, permissions, skins, blocked-item settings, and server-specific
  rate behavior.
