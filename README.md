# CraftingController Enhanced

A maintained, configuration-compatible fork of Whispers88's uMod/Oxide
Crafting Controller for Rust. Version 3.4.2 restores compatibility with the
current Rust server and fixes several inventory, shutdown, reload, and
configuration-safety defects while retaining the established commands,
permissions, and JSON property names.

## Install or upgrade

1. Back up `oxide/config/CraftingController.json`.
2. Copy `CraftingController.cs` to `oxide/plugins/`.
3. Watch the server console for `Crafting Controller was compiled successfully`.
4. Confirm the desired rate on that server with `/craftrate`.
5. Test one normal craft, one bulk craft near a full inventory, one VIP craft,
   and one blocked item before wider deployment.

Do not copy one server's JSON configuration over another. Each server keeps its
own existing rate, VIP multipliers, blocked items, custom times, and skins. A
rate of `0` means instant crafting; `3` means 3x speed; `5` means 5x speed.

The fork deliberately retains the upstream misspelled JSON key containing
`mulitplier`, because correcting it would silently reset existing VIP settings.

## Commands and permissions

| Command | Permission | Purpose |
| --- | --- | --- |
| `/craftrate [multiplier]` | `craftingcontroller.craftingrate` | Read or set the global crafting-speed multiplier. |
| `/crafttime item.shortname [seconds\|default]` | `craftingcontroller.itemrate` | Read or set an item's craft time. |
| `/blockitem item.shortname` | `craftingcontroller.blockitems` | Block crafting and research for an item. |
| `/unblockitem item.shortname` | `craftingcontroller.blockitems` | Restore crafting and research for an item. |
| `/benchlvl item.shortname [0-3\|default]` | `craftingcontroller.setbenchlvl` | Set or restore the required workbench level. |
| `/setcraftskin item.shortname workshopid` | `craftingcontroller.setskins` | Set the default craft skin. |

`craftingcontroller.instantbulkcraft` grants instant bulk crafting. Each key in
the configured bonus-multiplier object creates a permission named
`craftingcontroller.<key>` (for example, `craftingcontroller.vip1`).

## Compatibility guarantees

- Existing 3.3.7 configuration files load without migration.
- Existing command names and permission names are unchanged.
- Simple mode, random skins, full-inventory behavior, craft notes, blocked
  items, and per-item settings retain their prior meanings.
- Invalid JSON is never overwritten with generated defaults. The plugin logs an
  error, operates with in-memory defaults for that load, and leaves the file for
  an administrator to repair.

See [CHANGELOG.md](CHANGELOG.md) for release changes and
[docs/AUDIT.md](docs/AUDIT.md) for the full source and behavior review.

## Provenance

Based on Crafting Controller 3.3.7 by Whispers88, with credits retained for
previous authors Nivex and Mughisi. Distributed under the MIT License.
