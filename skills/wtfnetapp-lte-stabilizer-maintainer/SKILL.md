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
---
name: wtfnetapp-lte-stabilizer
description: >
  Build, refactor, and extend WTFnetApp into a production-grade LTE/5G network
  stabilizer for low-latency gaming and real-time traffic. Preserve behavior unless
  explicitly asked to change it. Prefer small, verifiable, architecture-aligned edits.
---

# WTFnetApp LTE Stabilizer Skill

## Mission

WTFnetApp is a **host-side LTE/5G latency stabilizer** for Windows that ingests live
network telemetry, detects queue buildup and lag precursors early, predicts instability,
and applies safe mitigation policy through the control layer.

The product goal is:

- lower tail latency
- lower jitter spikes
- earlier lag prediction
- fewer false positives
- stable control behavior
- low CPU and low allocation overhead
- clear professional architecture
- safe future expansion

This skill exists to keep the codebase moving toward that product shape.

---

# Product definition

## What the finished app is

A professional Windows network-control application that:

1. reads live network/tunnel telemetry
2. reads external ping / real path latency
3. calculates normalized delay / queue / retransmission / drift signals
4. predicts lag before the player feels it
5. classifies network condition and control response separately
6. applies mitigation through a dedicated actuator layer
7. logs and visualizes decisions clearly
8. learns carefully over time without becoming unstable

## What the finished app is NOT

It is not:

- a random collection of heuristics
- a giant god-class controller
- a logging-first prototype
- a feature pile with overlapping ownership
- a system where the same concept is computed in 3 places

---

# Canonical product architecture

## Core architecture rule

The app must always follow this pipeline:

```text
Telemetry Input
  -> Metrics Extraction
  -> Signal Normalization
  -> Prediction
  -> Learning
  -> Decision
  -> Actuation
  -> Logging / UI

  Telemetry Input
  -> Metrics Extraction
  -> Signal Normalization
  -> Prediction
  -> Learning
  -> Decision
  -> Actuation
  -> Logging / UI

  No class may skip layers or own logic from multiple layers unless explicitly approved.

Canonical subsystems
1. Telemetry layer
Responsibility

Read raw inputs only.

Current examples

SpeedifyTelemetryReader

SpeedifyMetricsAdapter

external ping callback

server ping scan / server tracker

Allowed outputs

Raw values such as:

latency

jitter

packet loss

bytes/sec

congestion flags

active connections

retransmission rate

external ping

server ping

Must NOT do

decision making

lag prediction

state transitions

long-term learning

2. Metrics layer
Responsibility

Convert raw data into stable per-tick metrics.

Canonical owner

NetworkMetricsEngine

Owned concepts

QueueDelay

QueueGrowth

QueueAcceleration

QueuePressure

LatencyDrift

RetransmissionGrowth

CongestionScore

LagScore

HealthScore

IssueSource

Rules

compute each metric once

smooth only here unless explicitly defined elsewhere

keep formulas readable

preserve fixed output ranges where defined

write outputs into MetricsSnapshot

add history into DataSet

Must NOT do

control state transitions

mitigation policy

learning memory ownership

UI behavior

3. Prediction layer
Responsibility

Estimate instability and lag risk from current metrics.

Canonical owners

PredictionEngine

future LagPredictor if separated more cleanly

Owned concepts

lag probability

lag score

dominant signal

prediction confidence

Rules

prediction is continuous, not discrete

prediction output should be scale-stable

prediction must not directly trigger actuator behavior

prediction should not mutate unrelated state

Must NOT do

apply Speedify actions

own control state

own UI state

4. Learning layer
Responsibility

Learn from actual lag events and slowly improve prediction thresholds/signatures.

Current owners

LagLearningEngine

AdaptiveThresholdController

ConfidenceEstimator

SlowLearningManager

LearningGate

Owned concepts

learned lag patterns

training samples

pattern confidence

adaptive threshold movement

learning enable/disable gating

slow epoch adjustment

Rules

learning must be conservative

learning must be bounded

learning must be disable-able

learning must not silently change unrelated logic

threshold movement must be clamped

learning changes must be explainable in logs

Must NOT do

direct actuator control

raw telemetry parsing

ownership of network state machine

5. Decision layer
Responsibility

Convert prediction + current network condition into discrete control intent.

Canonical owner

DecisionEngine

Owned concepts

control state transitions

persistence / hysteresis

degraded handling

panic hold behavior

use of lag pressure in decisions

gating between normal / warning / panic behavior

Current control state

Calibration

Normal

Warning

Panic

Product direction

Eventually split into two distinct state models:

Prediction state

Describes what the network seems to be doing:

Stable

CongestionBuilding

LagSpike

Control state

Describes how aggressively the controller should react:

Calibration

Normal

Warning

Panic

Rules

state transitions belong here only

hysteresis belongs here only

state must not be owned by controller/UI/metrics classes

state transitions must be easy to trace in logs

Must NOT do

raw metric calculation

server scanning

UI formatting

6. Control / actuator layer
Responsibility

Apply control intent to the actual network/tunnel system.

Canonical owner

SpeedifyController

Owned concepts

mapping control state -> action

mitigation strength

cooldown policy

bond / prioritize / keep-fastest style actuator behavior

Rules

actuator must be side-effect focused only

actuator should accept already-decided intent

actuator should not recalculate prediction or state

actuator logs must be explicit and human-readable

Must NOT do

own thresholds

compute risk

learn patterns

7. Safety / health layer
Responsibility

Prevent bad decisions when the network is already broken or in abnormal transition.

Current owners

FailureDetector

ServerQualityEstimator

LagPressureController

PlayerLagDetector

Owned concepts

degraded vs healthy classification

recovery handling

server score

lag pressure

event severity

burst escalation

Rules

safety modules may influence decision inputs

safety modules must not directly own final state transitions unless explicitly routed through DecisionEngine

8. Presentation / observability layer
Responsibility

Expose what the app is doing.

Current owners

Logger

tray

overlay

dashboard

Rules

logging must reflect system truth, not invent it

logs should be structured and stable

diagnostics should come from canonical owners

UI should read state, not create it

Canonical file ownership

Use this as the target ownership map.

Root orchestration

NetworkController

orchestration only

no heavy logic

no duplicate scoring

no mixed ownership

Input

SpeedifyTelemetryReader

SpeedifyMetricsAdapter

external ping callback in derGrosseGeneralstab

SpeedifyServerTracker

Metrics

NetworkMetricsEngine

DataSet

NetSample

MetricsSnapshot

NetworkAnalysisFrame

Prediction

PredictionEngine

Learning

LagLearningEngine

AdaptiveThresholdController

ConfidenceEstimator

SlowLearningManager

LearningGate

LagPattern

Safety / classification

FailureDetector

ServerQualityEstimator

LagPressureController

PlayerLagDetector

Decision

DecisionEngine

Actuation

SpeedifyController

Presentation

Logger

Tray

overlay / dashboard forms

Single-owner concept map

Every important concept must have one owner.

Must converge to this
Concept	Owner
QueueDelay	NetworkMetricsEngine
QueueGrowth	NetworkMetricsEngine
QueueAcceleration	NetworkMetricsEngine
QueuePressure	NetworkMetricsEngine
LatencyDrift	NetworkMetricsEngine
RetransmissionGrowth	NetworkMetricsEngine
CongestionScore	PredictionEngine called from NetworkMetricsEngine
LagScore	PredictionEngine called from NetworkMetricsEngine
baseline RTT window	LagLearningEngine or dedicated baseline estimator
control state	DecisionEngine
lag pressure	LagPressureController
lag event type	PlayerLagDetector
server score	ServerQualityEstimator
adaptive learning threshold	AdaptiveThresholdController
learning confidence	ConfidenceEstimator
external ping latest value	NetworkController input handoff only
actuator behavior	SpeedifyController

No duplicate ownership is allowed without explicit adapter/wrapper reasoning.

Data contract rules
MetricsSnapshot

MetricsSnapshot is the per-tick shared contract.

It SHOULD contain

raw snapshot input

derived metrics

prediction outputs

current read-only diagnostics

current control outputs needed by UI/logging

It SHOULD NOT contain

long-term ring buffers

learning windows

confirmation counters

hidden timestamps

persistence memory

training-set memory

mutable ownership confusion

Allowed categories

raw input

derived metric

prediction output

current decision output

read-only diagnostic

If a field does not fit one of these, it probably belongs elsewhere.

Naming standard

These meanings are fixed.

Allowed semantic meanings

Score = bounded scalar measure on a defined scale

Probability = 0..1 likelihood estimate

Confidence = trust in estimate / learning signal

State = discrete enum value

Factor = multiplier

Pressure = accumulated urgency

Rate = per-time ratio/value

Growth = first derivative or recent increase

Acceleration = second derivative or change-of-growth

Baseline = slowly moving reference

Threshold = configurable trigger boundary

Do not use these words loosely.

Range standard

All important numeric outputs must have explicit intended ranges.

Canonical ranges
Field	Range
PacketLoss	0..1
RetransmissionRate	0..1
ServerScore	0..1
HealthScore	0..1 or clearly documented fixed scale
LagPressure	0..1
PredictionConfidence	0..1
LearningConfidence	0..1
LagProbability	0..1
CongestionScore	fixed scale, currently 0..15
LagScore	fixed documented scale

If a scale exists, document it once and clamp consistently.

Performance rules

This is a real-time app. Performance discipline is mandatory.

Hard rules for hot path

no LINQ in per-tick path

no per-tick list creation

no per-tick sorting unless explicitly justified

no repeated string allocations in hot path

no duplicate metric recomputation in one tick

no hidden blocking work in the tick pipeline

Preferred patterns

ring buffers

fixed-size arrays

small helper methods

explicit clamping

explicit fast-path exits

throttled logging

CPU budget mindset

The app should feel invisible on the machine.

Target mindset:

easy CPU

easy RAM

predictable behavior

no GC spikes caused by the control loop

Logging rules
Logging categories

Use stable categories only.

Canonical categories

metrics

prediction

learning

decision

control

server

ui

system

Logging behavior

per-tick logs must be throttled

state changes must always be logged

mitigation actions must always be logged

threshold changes must always be logged

learning enable/disable changes must always be logged

degraded/recovery transitions must always be logged

Log format expectation

A human should be able to answer:

what happened

why it happened

which signal caused it

what action was taken

whether learning changed anything

Refactor doctrine
Golden rule

Do not add new features on top of unclear ownership.

Always prefer:

cleanup

ownership clarification

config centralization

state cleanup

performance cleanup

then feature work

Canonical development passes

Run work in these passes only.

Pass 1 — audit only

Goal:

identify duplicate concepts

identify dead code

identify mixed responsibilities

identify scattered tunables

identify snapshot abuse

Do not edit.

Pass 2 — safe cleanup

Allowed:

remove unused private fields/methods

remove duplicate declarations

remove merge leftovers

remove repeated csproj includes

remove obsolete commented code

No behavior change.

Pass 3 — ownership cleanup

Goal:

one concept, one owner

No formula change.

Pass 4 — state cleanup

Goal:

decision state only in DecisionEngine

prepare two-layer model cleanly

No functional redesign unless explicitly requested.

Pass 5 — snapshot cleanup

Goal:

keep MetricsSnapshot clean and professional

Pass 6 — config centralization

Goal:

all tunables into one config object

Pass 7 — naming cleanup

Goal:

semantic naming consistency

Pass 8 — hot-path cleanup

Goal:

lower allocations

shorter orchestration path

clearer data flow

Pass 9 — feature work

Only after passes 1-8 are stable.

Definition of professional product quality

The app is considered architecturally professional when:

each major concept has one owner

orchestration is short and readable

state transitions are centralized

learning is bounded and explainable

actuator logic is isolated

logging is structured

thresholds are centralized

snapshot contract is clean

performance hazards are minimized

new features can be added without breaking ownership

Future feature foundation

These are valid future directions, but must be built on the clean architecture above.

Priority future features

path ranking

server ranking

burst/scheduler pattern detection

better lag signature matching

safer threshold adaptation

confidence decay

pattern pruning

calibration mode UX

dry-run mode vs live-actuation mode

replay/testing harness from recorded traces

Rule

Every new feature must declare:

which subsystem owns it

what inputs it reads

what outputs it writes

what state it owns

what config it needs

what logs it emits

If that is unclear, do not implement yet.

After any modification:

1 run dotnet build
2 report build result
3 stop if compilation fails
4 do not attempt speculative fixes


Small-file consolidation rule

If a file:
- is under ~40 lines
- has one helper function
- is referenced by only one class

it should be merged into its parent subsystem.

Small-file consolidation rule

If a file:
- is under ~40 lines
- has one helper function
- is referenced by only one class

it should be merged into its parent subsystem.


After any modification:

1 run dotnet build
2 report build result
3 stop if compilation fails
4 do not attempt speculative fixes
