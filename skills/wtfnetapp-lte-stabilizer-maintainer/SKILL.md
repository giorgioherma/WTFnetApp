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
