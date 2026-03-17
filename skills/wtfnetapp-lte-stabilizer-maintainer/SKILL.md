---
name: wtfnetapp-lte-stabilizer-maintainer
description: Maintain and refactor WTFnetApp LTE stabilizer architecture safely. Use for cleanup-only passes that consolidate control-path ownership, remove merge leftovers, delete dead or duplicate components, and verify DecisionEngine remains the single runtime state authority in the telemetry -> metrics -> decision -> mitigation pipeline.
---

# WTFnetApp LTE Stabilizer Maintainer

## Overview
Use this skill for strict architectural cleanup without feature work. Enforce one control path, remove conflicting ownership, and keep edits minimal, compile-safe, and reversible.

## Canonical ownership
- Runtime path: `NetworkController -> NetworkMetricsEngine -> DecisionEngine -> SpeedifyController`.
- `DecisionEngine` is the only owner of runtime `NetworkControlState` transitions.
- Learning/prediction components can provide signals, but cannot decide state or issue mitigation directly.

## Workflow
1. **Triage**
   - Classify request scope: cleanup-only or behavior-changing.
   - If mixed, perform cleanup items first and report behavior-change risks separately.

2. **Audit**
   - Map ownership of key concepts and control flow.
   - Locate duplicate declarations, merge leftovers, dead-end classes, and duplicate project includes.

3. **Clean safely**
   - Remove unused files only after explicit reference checks.
   - Remove duplicate fields/methods/enums and stale branches.
   - Keep public APIs unchanged unless required to restore compilation.

4. **Verify invariants**
   - Confirm no alternate path bypasses `DecisionEngine`.
   - Confirm suppression logic still covers loading/idle/degraded conditions.
   - Confirm `.csproj`: retained files exactly once, removed files absent.

5. **Validate and report**
   - Run static checks and build when toolchain is available.
   - Report: files changed, symbols removed, ownership consolidations, unresolved risks.

## Guardrails
- Do not add features during cleanup passes.
- Do not redesign thresholds or algorithms unless explicitly requested.
- Do not create or keep a second decision authority.
- Prefer deleting unintegrated code over preserving ambiguous ownership.
- Keep edits localized and auditable.

## Quick commands
```bash
rg -n "class DecisionEngine|enum NetworkControlState|ProcessSnapshot\(|Apply\(state, snapshot\)" *.cs
rg -n "LagScore|LagProbability|PredictionConfidence|DominantSignal|CongestionScore|ServerScore|latestExternalPing|persistenceTicks|decay|threshold" *.cs
rg -n "<Compile Include=\"" WTFnetApp.csproj
git diff --check
```

Use `references/maintenance-checklist.md` for deletion criteria and report structure.
