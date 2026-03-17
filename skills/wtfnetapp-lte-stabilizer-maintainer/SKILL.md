---
name: wtfnetapp-lte-stabilizer-maintainer
description: Maintain and refactor the WTFnetApp LTE stabilizer architecture safely. Use when cleaning merge leftovers, enforcing single ownership of control flow, removing dead/duplicate components, validating DecisionEngine as the sole runtime state authority, or performing bounded non-behavior-changing cleanup in the telemetry→metrics→decision→mitigation pipeline.
---

# WTFnetApp LTE Stabilizer Maintainer

## Overview
Use this skill to perform safe architecture maintenance on WTFnetApp without introducing feature drift. Keep the fast control path authoritative, remove conflicting branches, and prefer minimal compile-safe edits.

## Workflow

1. **Audit before editing**
   - Map the active runtime path and ownership boundaries.
   - Detect duplicate declarations, duplicate project includes, and dead-end components.
   - Confirm whether requested changes are cleanup-only or behavior-changing.

2. **Enforce control-path ownership**
   - Keep runtime flow as: `NetworkController -> NetworkMetricsEngine -> DecisionEngine -> SpeedifyController`.
   - Ensure `DecisionEngine` is the only authority for `NetworkControlState`.
   - Reject parallel decision paths or side systems that issue mitigation directly.

3. **Apply minimal safe cleanup**
   - Remove unused files only after reference checks.
   - Remove duplicate fields/methods/enums and merge leftovers.
   - Keep public API stable unless compile errors force changes.

4. **Verify architecture invariants**
   - Confirm predictive/learning components support decisioning but do not bypass it.
   - Confirm learning suppression logic remains respected (loading/degraded/idle conditions).
   - Confirm `.csproj` includes each retained file once.

5. **Validate and report**
   - Run static checks (and build if toolchain exists).
   - Report exactly what was removed, what was consolidated, and any risky unresolved areas.

## Non-Negotiable Guardrails
- Do not add new features during cleanup passes.
- Do not redesign thresholds/algorithms unless explicitly requested.
- Do not create a second decision engine.
- Prefer deletion of unintegrated code over keeping conflicting ownership.
- Keep changes localized and reversible.

## Quick Commands
Use these checks first:

```bash
rg -n "class DecisionEngine|enum NetworkControlState|ProcessSnapshot\(|Apply\(state, snapshot\)" *.cs
rg -n "<Compile Include=\"" WTFnetApp.csproj
git diff --check
```

Then reference-specific checks from `references/maintenance-checklist.md`.
