# WTFnetApp

**WTFnetApp** is a lightweight Windows utility designed to improve gaming stability on **unstable LTE / 5G networks**.

The application monitors network quality in real time and can automatically control **Speedify bonding modes** to reduce lag spikes during gameplay.

Even without Speedify enabled, the tool continues to provide **network diagnostics, NAT stabilization, and a real-time ping overlay**.

---

# Why this tool exists

Mobile networks often suffer from:

- latency spikes
- jitter
- packet loss
- unstable routing
- aggressive NAT timeouts

These issues frequently cause:

- rubber-banding
- sudden lag spikes
- lobby disconnects
- dropped sessions

WTFnetApp attempts to **detect and mitigate these problems automatically**.

---

# Features

## Real-time network monitoring

The application continuously measures:

- latency
- jitter
- packet loss
- spike detection
- traffic conditions

Ping targets used:

- 8.8.8.8
- 1.1.1.1
- 9.9.9.9

---

## Smart Speedify mode control

When Speedify is enabled, the application can automatically switch between:

### Speed Mode
Normal bonding mode with minimal overhead and lower mobile data usage.

### Redundant Mode
Duplicate packets are transmitted across connections to protect against:

- packet loss
- LTE spikes
- unstable routing

The system activates redundant mode only when instability is detected.

---

## Works without Speedify

Speedify is **not required** for the application to function.

When Speedify is disabled, WTFnetApp still provides:

- network monitoring
- ping overlay
- NAT keep-alive
- network logging
- LTE quality analysis

This allows the tool to be used purely as a **diagnostic and monitoring utility**.

---

## Gaming ping overlay

A small overlay displays real-time ping while gaming.

Color indicators:

Green  < 50 ms
Orange < 90 ms
Red    > 90 ms

This allows latency spikes to be seen immediately during gameplay.

---

## NAT keep-alive

Some mobile providers aggressively close UDP NAT mappings.

The application periodically sends small UDP packets to keep the connection alive and prevent:

- sudden disconnects
- lobby drops
- session resets

---

## LTE / 5G network quality analysis

The built-in LTE diagnostic system estimates connection stability and provides a simplified quality indicator.

Quality states:

LOCKED
STABLE
PLAYABLE
WEAK
BAD

---

# Requirements

Operating system:

Windows 10
Windows 11

Runtime:

.NET Framework 4.8

Optional software:

Speedify

Expected Speedify CLI path:

C:\Program Files (x86)\Speedify\speedify_cli.exe

---

# Project Structure

Main components:

Form1.cs              Core monitoring and decision logic
PingOverlay.cs        On-screen ping overlay
LteProTool.cs         LTE network quality analysis
LteDashboardForm.cs   LTE diagnostic dashboard
Program.cs            Application entry point

---

# Logging

Network metrics and system decisions are written to log files for diagnostics.

Typical log categories include:

metrics
decisions
speedify
nat
errors
trace

---

# Project Status

Experimental project.

Primarily designed for **gaming on unstable mobile networks**.

---

# Disclaimer

This project is an independent tool and is **not affiliated with Speedify or any network provider**.

---

# License

This project is licensed under the **MIT License**.

You are free to:

- use
- modify
- distribute
- include the code in other projects

as long as the original copyright notice is included.

See the **LICENSE** file for full details.
