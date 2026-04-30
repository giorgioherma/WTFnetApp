# WTFnetApp

**WTFnetApp (Wide-area Traffic Forensics net App)** is a real-time network analysis and route optimization tool designed to improve **gaming stability on unstable LTE / 5G connections**.

Instead of chasing the lowest ping, the application focuses on what actually matters for gameplay:

> **stability, consistency, and spike resistance**

---

# Why this tool exists

Mobile networks are fundamentally unstable compared to wired connections.

Typical issues include:

- latency spikes  
- jitter (inconsistent delay)  
- packet loss  
- route instability  
- aggressive NAT timeouts  

These lead to real in-game problems:

- rubber-banding  
- sudden lag spikes  
- hit registration issues  
- lobby disconnects  
- dropped sessions  

**WTFnetApp exists to detect and actively manage these conditions in real time.**

---

# Core Concept

Most tools measure **average latency**.

WTFnetApp instead evaluates:

> "How stable is this connection over time?"

The system continuously analyzes:

- spike behavior  
- jitter consistency  
- packet loss patterns  
- recovery time after instability  
- confidence in measurements  

and makes decisions based on **stability-first scoring**, not raw ping.

---

# Features

## Real-time network monitoring

Continuous active probing using external targets:

- 1.1.1.1  
- 8.8.8.8  

Measured metrics:

- latency (instant + smoothed)  
- jitter  
- packet loss  
- spike detection  
- stale / degraded conditions  
- confidence score  

---

## Advanced signal analysis

The system aggregates short and long windows of data to detect:

- burst packet loss vs isolated loss  
- spike density and severity  
- tail latency (p95 / p99 behavior)  
- degradation trends (improving / worsening)  

This allows accurate classification of:

> Playable vs unstable vs broken connections

---

## Smart route selection (startup probe)

At application startup, WTFnetApp performs a controlled evaluation:

1. Measures **raw (no VPN)** connection  
2. Tests multiple **Speedify routes**  
3. Stabilizes each candidate  
4. Compares using stability scoring  

Then selects the best route based on:

- stability score  
- confidence  
- loss / spike behavior  
- recovery characteristics  

This ensures the chosen route is **actually playable**, not just fast.

---

## Speedify integration (optional)

When Speedify is installed, WTFnetApp can:

- activate specific server routes  
- evaluate tunnel quality vs raw connection  
- interpret Speedify telemetry (latency, jitter, congestion)  
- detect bonding benefits  

Important:

> Speedify data is treated as **supporting context**, not ground truth.

FastPing measurements always remain authoritative.

---

## Works without Speedify

Speedify is **not required**.

Without it, WTFnetApp still provides:

- full network diagnostics  
- real-time monitoring  
- overlay display  
- NAT stabilization  
- route quality analysis  

This makes it usable as a standalone **network analysis tool**.

---

## Gaming overlay

A lightweight on-screen overlay displays:

- live latency  
- jitter  
- connection quality  

Color-coded for instant feedback:

- 🟢 Stable  
- 🟠 Degrading  
- 🔴 Unstable  

---

## NAT keep-alive

Mobile providers frequently drop UDP mappings.

WTFnetApp maintains NAT state using periodic packets to prevent:

- session drops  
- lobby disconnects  
- sudden connection resets  

---

## Learning system

The application builds a local learning profile:

- remembers historically good routes  
- adapts to time-of-day patterns  
- biases startup selection toward proven stable paths  

Stored locally:
