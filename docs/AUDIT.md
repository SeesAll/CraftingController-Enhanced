# CraftingController 3.3.7 audit

Audit date: 2026-11-23  
Enhanced release: 3.4.2

## Cancellation observation resolved in 3.4.2

Cancelling a queued gunpowder craft with a full inventory caused Rust to drop
the held sulfur. Rust's cancellation path tries only the main container before
dropping a refund; it does not try the belt or reconstruct a pre-existing
overstack. Oxide calls `OnItemCraftCancelled` before that refund loop. Version
3.4.2 restores the held items through the plugin's main/belt/overstack-safe
path and clears the list so vanilla cannot refund or drop them again. Temporary
blueprints retain Rust's crafted-use accounting before they are returned.

## Second TEST observation resolved in 3.4.2

The next live test exposed a flaw in partial bulk trimming. The pre-collection
check admitted a request when at least one batch could fit. After Rust removed
ingredients for the complete request, the post-collection check shortened the
task and returned the rejected batches' ingredients. A two-pipe refund then
occupied the output slot that the capacity calculation had reserved, so the
accepted rockets completed onto the ground.

3.4.2 makes the capacity decision all-or-nothing. Before Rust mutates the
inventory, the projected post-collection space must hold every output item in
the requested task. Otherwise the request is rejected with all ingredients
untouched. The post-collection fallback applies the same rule rather than
returning a partial refund into output space.

## TEST observation resolved in 3.4.1

The first live inventory test exposed a remaining edge case: Rust collected a
recipe-sized split from an admin-created stack of 100 pipes before
`OnItemCraft`, then the no-space rejection could not merge that split under the
active stack limits and dropped it on the ground.

3.4.1 introduced uMod's pre-collection `CanCraft` hook. The check projects
Rust's own ingredient collection without mutating items, so it includes slots
that the recipe will genuinely free and does not reject merely because the
inventory starts full. Version 3.4.2 strengthens the decision to require room
for the complete requested craft rather than admitting a partial fit.

The refund fallback was also hardened. It first attempts normal main/belt stack
merging. If an overstack cannot be reconstructed under the current normal
limit, it retries with Rust's `ignoreStackLimit` move specifically to restore
the pre-craft state before considering a ground drop. Temporary-blueprint
crafts remain on the post-collection path because `CanCraft` does not receive
the blueprint item that Rust removes later.

## Scope and evidence

The review covered the complete 3.3.7 source, the current Rust Dedicated Server
assemblies, the uMod `OnItemCraft` contract, a deployed TEST copy, live TEST and
10x configurations, and 11 offline configuration backups. The deployed TEST
source and the official 3.3.7 download had the same normalized SHA-256:

`f3c7c1148c74f88d0f8aa58d4ddcfc971464f19f559f4ea1e796736a47b1b08b`

The current TEST compiler failure was reproduced as:

`'ItemCraftTask' does not contain a definition for 'cancelled'`

Rust's current `ItemCraftTask` no longer has that field. In uMod's patched craft
flow, a Boolean return from `OnItemCraft` overrides normal handling before the
task is queued. Both affected plugin branches already return `true`; one creates
the output and consumes the collected ingredients, and the other returns the
collected ingredients. The removed assignments were therefore redundant, not
the mechanism that protected those items.

## Findings resolved in 3.4.0

### Critical: current Rust would not compile

Two writes to the removed `ItemCraftTask.cancelled` member prevented the entire
plugin from loading. They were removed after verifying the hook injection and
both ownership paths.

### High: partial bulk crafting was not transactional (finalized in 3.4.2)

The old code repeatedly scaled collected ingredient stacks by a fraction, up to
20 passes, before rounding the output back to recipe batches. It could reject a
craft that actually fit, return incorrect material quantities, or recreate
refund items without their original metadata.

Version 3.4.0 first replaced the fractional loop with whole-batch trimming and
exact stack splits. Live testing showed that even an exact partial refund can
consume a slot reserved for output. Version 3.4.2 closes the issue by requiring
the complete requested output to fit before ingredient collection; otherwise
the request is rejected without moving any items.

### High: shutdown could discard a multi-craft remainder

With "Complete crafting on server shut down" enabled, 3.3.7 called Rust's
`FinishCrafting` once and then removed the linked-list node directly. That
completed only one batch, bypassed Rust's cancellation cleanup, failed to refund
the remainder, and orphaned a pooled task.

3.4.0 completes one active batch, then calls Rust's `CancelTask` so the remaining
materials are refunded and the task is returned to its pool. All other queued
tasks are then cancelled normally. A reflection failure is contained and falls
back to safe cancellation instead of aborting shutdown processing.

### High: instant crafting could remain stuck after a rate change

The active-era craft override was only updated when its current value was above
zero. Once instant mode set it to zero, changing `/craftrate` back to a positive
value could leave the era override at zero. 3.4.0 always updates the matching
active-era override.

### High: invalid JSON could be destroyed on startup

3.3.7 caught a read error, loaded defaults, populated every blueprint, and then
saved those defaults over the malformed file. 3.4.0 records the failed load and
refuses all configuration writes for that plugin load. It logs the condition so
the administrator can repair or restore the original file.

### Medium: queued VIP crafts referenced unloadable clones

VIP rates use cloned `ItemBlueprint` components. Unloading the plugin destroyed
those components while queued tasks could still reference them. 3.4.0 walks all
players' queues and swaps clone references back to their original blueprints
before destroying the clones.

### Medium: workbench overrides were inconsistent

The command updated only the blueprint's base workbench field. An active-era
override could continue to win, and there was no supported way to restore the
captured default. 3.4.0 updates both representations and accepts `default`.

### Medium/low: defensive and maintenance defects

- Item-time queries could dereference a missing blueprint.
- Approved workshop records could have a missing `Skinnable` object.
- Hook inputs and finished items were assumed non-null.
- A non-positive stack size could silently abandon instant output.
- NaN and infinity were accepted by numeric parsing; negative custom craft times
  could be written during the same load.
- Every startup rewrote a large configuration even when nothing changed.
- Two serialization helpers and the old free-slot helper were dead code.

These are guarded or removed in 3.4.0.

## Configuration review

All 11 offline `oxide/config/CraftingController.json` files parsed successfully
with the existing schema. Backups contain 687 or 694 item entries, three blocked
items, no custom per-item times, no configured skins, Simple Mode disabled, and
full-inventory crafting disabled. The historical former 2x backup is retained
only as compatibility evidence; that server is now RustMania 10x.

Observed rate families are intentionally not normalized:

- RustMania and the ordinary 10x backups use global rate `0` (instant), usually
  with VIP multipliers `1.11` and `1.25`.
- 3x backups use global rate `3`, usually with VIP multipliers `1.25` and `2.25`.
- The 5x backup uses global rate `5`, with VIP multipliers `7.25` and `8.25`.
- AU/EU 10x backups contain VIP values `90` and `80`. Those mean 90x and 80x,
  not percentages, and are effectively instant. They are preserved but should
  be reviewed by an operator.

No server identity or rate is hard-coded in the plugin.

## Compatibility-sensitive behavior deliberately deferred

The instant-bulk implementation still creates output itself rather than running
Rust's private normal finish loop. This means Rust workbench upgrade effects,
bonus-item behavior, and some first-party crafting analytics may differ for
instant crafts, as they did in 3.3.7. Replacing that path would be a behavioral
change and should be tested as a separate release.

Pending workshop-skin substitutions are in memory only. A reload or disconnect
can lose the cosmetic substitution for a queued craft; no materials are lost.
Persisting it would require a task-lifecycle data design and migration policy.

The plugin does not attempt to free an `ItemCraftTask` inside `OnItemCraft` after
returning `true`. Hook dispatch and other plugins may still reference the task;
freeing it inside this hook could introduce a use-after-pool defect. Rust/uMod
owns that lifecycle.

## Verification completed

- Compiled the complete enhanced source against the current Rust Dedicated
  Server assemblies using a publicized `Assembly-CSharp` reference: zero code
  errors.
- Decompiled and compared the current `ItemCrafter` collection, completion,
  cancellation, and queue paths used by the fixes.
- Verified exact ingredient arithmetic against Rust's own
  `CollectIngredients` and `FinishCrafting` integer consumption.
- Parsed all 11 offline server configuration files and confirmed that no schema
  migration is required.
- Confirmed no remaining `ItemCraftTask.cancelled` references or single-use
  private helper dead code.
- Uploaded only `CraftingController.cs` to the TEST server and confirmed that
  its live Oxide plugin list reports Crafting Controller 3.4.1 by Whispers88 &
  SeesAll. The TEST JSON configuration was not replaced.

Interactive TEST-server crafting checks are still required before production
rollout because a successful live compile/load cannot validate inventory
networking, third-party hook ordering, or wipe-specific item behavior.
