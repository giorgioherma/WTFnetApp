Below is a **properly formatted `SKILL.md`** you can paste directly into a file without formatting breaking.
Use **copy-all** and save as:

```
skills/wtfnetapp-lte-stabilizer-maintainer/SKILL.md
```

---

```md
---
name: wtfnetapp-lte-stabilizer-maintainer
description: Maintain the WTFnetApp LTE/5G latency stabilizer architecture. Perform safe cleanup passes, enforce ownership rules, prevent behavioral drift, and keep the codebase production-grade while preserving runtime behavior.
---

# WTFnetApp LTE Stabilizer Maintenance Skill

## Purpose

This skill maintains the architecture and code quality of **WTFnetApp**, a Windows LTE/5G latency stabilizer designed to reduce jitter and lag spikes for real-time applications.

The system works by:

1. ingesting network telemetry
2. computing queue/latency/retransmission signals
3. predicting instability
4. applying mitigation through a control layer
5. learning from lag events
6. exposing diagnostics via logging/UI

The role of this skill is **maintenance and safe refactoring**, not feature development.

---

# Core Architecture

The system must follow this pipeline:

```

Telemetry
→ Metrics
→ Prediction
→ Learning
→ Decision
→ Control
→ Logging/UI

```

Subsystem responsibilities must remain clearly separated.

---

# Subsystem Ownership

## Telemetry Layer
Reads raw network information.

Examples:
- SpeedifyTelemetryReader
- SpeedifyMetricsAdapter
- SpeedifyServerTracker
- external ping callback

Allowed outputs:
- latency
- jitter
- packet loss
- throughput
- retransmission rate
- server latency

Must NOT:
- compute lag predictions
- change control state
- apply mitigation actions

---

## Metrics Layer

Owner: `NetworkMetricsEngine`

Responsible for computing derived signals.

Examples:

- QueueDelay
- QueueGrowth
- QueueAcceleration
- QueuePressure
- LatencyDrift
- RetransmissionGrowth
- CongestionScore
- LagScore
- HealthScore

Rules:

- metrics are computed once
- smoothing happens here
- results are written to `MetricsSnapshot`

---

## Prediction Layer

Owner: `PredictionEngine`

Produces:

- lag probability
- dominant signal
- prediction confidence

Prediction is **continuous**, not discrete.

Prediction must NOT directly trigger control actions.

---

## Learning Layer

Examples:

- LagLearningEngine
- AdaptiveThresholdController
- ConfidenceEstimator
- SlowLearningManager
- LearningGate
- LagPattern

Responsibilities:

- detect lag events
- store lag signatures
- adjust thresholds slowly
- manage learning confidence

Rules:

- learning must be bounded
- learning must be explainable in logs
- learning must be disable-able

---

## Decision Layer

Owner: `DecisionEngine`

Responsible for:

- state transitions
- hysteresis
- persistence
- degraded handling

Control states:

```

Calibration
Normal
Warning
Panic

```

DecisionEngine decides **intent**, not execution.

---

## Control Layer

Owner: `SpeedifyController`

Responsibilities:

- apply mitigation actions
- map control state → Speedify commands
- enforce cooldown rules

Control layer must never:

- recompute metrics
- recompute predictions
- learn patterns

---

## Safety Modules

Examples:

- FailureDetector
- ServerQualityEstimator
- LagPressureController
- PlayerLagDetector

These modules provide **diagnostics and safety signals**.

They influence the decision layer but do not override it.

---

# Canonical Ownership Map

Each concept must have exactly one owner.

| Concept | Owner |
|-------|------|
QueueDelay | NetworkMetricsEngine
QueueGrowth | NetworkMetricsEngine
LatencyDrift | NetworkMetricsEngine
CongestionScore | PredictionEngine
LagScore | PredictionEngine
ServerScore | ServerQualityEstimator
LagPressure | LagPressureController
NetworkControlState | DecisionEngine
PredictionConfidence | PredictionEngine
ExternalPingLatest | NetworkController

No duplicate ownership allowed.

---

# MetricsSnapshot Contract

`MetricsSnapshot` represents the **per-tick system state**.

Allowed contents:

- raw telemetry
- derived metrics
- prediction outputs
- current decision outputs
- diagnostics for logging/UI

Not allowed:

- learning buffers
- persistence counters
- training data
- hidden controller state

Snapshot must remain a **clean shared contract**.

---

# Naming Rules

Terms have fixed meanings.

| Term | Meaning |
|-----|--------|
Score | scalar measurement
Probability | 0-1 likelihood
Confidence | trust in estimate
State | discrete enum
Factor | multiplier
Pressure | accumulated urgency
Rate | per-time measurement
Growth | first derivative
Acceleration | second derivative

Names must respect these meanings.

---

# Numeric Range Rules

Important values must have defined ranges.

| Metric | Range |
|------|------|
PacketLoss | 0-1
RetransmissionRate | 0-1
ServerScore | 0-1
LagPressure | 0-1
PredictionConfidence | 0-1
LagProbability | 0-1
CongestionScore | fixed documented scale
LagScore | fixed documented scale

Values should be clamped where appropriate.

---

# Performance Rules

The system runs continuously and must remain lightweight.

Hot-path restrictions:

- no LINQ in tick loop
- no dynamic allocations per tick
- no per-tick sorting
- no string formatting in hot path
- no redundant metric recomputation

Preferred patterns:

- ring buffers
- fixed arrays
- explicit clamping
- early exit paths

---

# Logging Rules

Logging categories:

```

metrics
prediction
learning
decision
control
server
ui
system

```

Required logs:

- state transitions
- mitigation actions
- threshold adjustments
- degraded/recovery transitions

Logs must explain:

- what happened
- why it happened
- which signal triggered it
- what action was taken

---

# Safe Refactoring Guardrails

This skill must **not change behavior** unless explicitly instructed.

Never:

- redesign algorithms
- alter threshold values
- modify state transitions
- introduce new control states
- add new features
- add a second decision engine

Allowed actions:

- remove dead code
- consolidate duplicate logic
- enforce ownership boundaries
- centralize configuration
- rename private symbols for clarity

---

# Maintenance Workflow

## Pass 1 — Audit

Check for:

- duplicate symbols
- unused fields
- ownership violations
- scattered constants
- project-file issues

Do not modify code.

---

## Pass 2 — Safe Cleanup

Allowed:

- remove unused private fields
- remove unused private methods
- remove merge leftovers
- remove duplicate project includes
- replace repeated literals with constants

No behavior change allowed.

---

## Pass 3 — Ownership Enforcement

Ensure:

- each concept has one owner
- overlapping logic is consolidated
- no class mixes multiple subsystem responsibilities

---

## Pass 4 — Snapshot Cleanup

Ensure `MetricsSnapshot` only contains:

- per-tick data
- derived metrics
- prediction outputs

Move internal controller state elsewhere if necessary.

---

## Pass 5 — Config Centralization

Move tunable constants into a config class:

```

NetworkControlConfig

```

Examples:

- thresholds
- decay rates
- cooldown values
- confirmation counts

Values must remain unchanged.

---

# Reporting Format

Every maintenance pass must produce:

```

Audit report (pre-change)

Safe cleanup changes performed

Build step

Final summary

```

Final summary must include:

- files changed
- symbols removed
- duplicates consolidated
- unresolved risky areas

---

# Behavior Preservation Rule

Unless explicitly instructed:

- preserve runtime behavior
- preserve thresholds
- preserve formulas
- preserve state semantics

If a change might alter behavior:

**STOP and report instead of modifying code.**

---

# Definition of Done

A maintenance pass is complete when:

- the project builds
- duplicate symbols are removed
- ownership rules are respected
- no new architecture violations are introduced
- runtime behavior remains unchanged

---

# Quick Commands

Typical tasks:

```

run architecture audit
run safe cleanup pass
verify subsystem ownership
verify MetricsSnapshot contract

```

The skill must apply the workflow and guardrails defined above.
```

---

✅ This version is **safe to paste directly into a `.md` file** without formatting breaking.

---

If you'd like, I can also give you the **recommended folder layout for Codex skills** so your repo becomes a **fully automated engineering workflow** (audit → cleanup → refactor → feature passes).
# WTFnetApp Maintenance Checklist

## Ownership checks
- `NetworkControlState` defined once.
- `DecisionEngine` class defined once.
- `NetworkController.ProcessSnapshot` defined once and follows:
  1) assign `ExternalPingMs`
  2) `metricsEngine.Process(snapshot)`
  3) `decisionEngine.Update(snapshot, metricsEngine.DataSet)`
  4) `speedifyController.Apply(state, snapshot)`

## Dead-code checks
Run targeted searches before deleting files:

```bash
rg -n "ClassName|new ClassName|ClassName\." *.cs WTFnetApp.csproj
```

Delete only when:
- no runtime references remain
- no project include is needed
- removal does not break architectural invariants

## Project-file checks
- No duplicate `<Compile Include="..." />` entries.
- Removed files are absent from `.csproj`.
- Retained files appear once.

## Safe refactor boundary
Allowed:
- duplicate removal
- merge/collision cleanup
- unused private symbol removal
- constant extraction (no value change)

Not allowed in cleanup-only pass:
- state-machine redesign
- threshold tuning changes
- new predictor/control path
- public behavior changes

## Report template
- Files cleaned
- Files deleted
- Ownership consolidations
- Invariants verified
- Risks not changed (with reason)
