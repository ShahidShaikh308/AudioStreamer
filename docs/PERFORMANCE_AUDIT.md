# AudioStreamer Performance Audit

**Date:** 2026-10-13  
**Scope:** Audio, Networking, IPC, UI, Service, Control Plane, and Engine  
**Purpose:** Record the performance paths inspected, measurements possible in this workspace, and evidence required before changing a hot path.

## Summary

No production implementation was changed. The only runnable runtime in this environment is Python 3.13.15; .NET, Android/Gradle, and ESP32/PlatformIO toolchains are unavailable. The Python measurements below establish a local protocol-reference baseline, not an estimate for the Windows, Android, or firmware implementations. There is no repeatable workload or platform profiler here to show that a source-level optimization improves end-to-end audio latency, throughput, CPU, or memory. Therefore no speculative hot-path change is justified.

The Python protocol loops completed 200,000 iterations on this host with a 1,049-byte Audio v3 datagram and a 15-byte Control v1 heartbeat. Timings include Python interpreter and `tracemalloc` overhead. The per-operation parser and serializer return short-lived objects; peak traced memory is the maximum simultaneously live traced allocation during a discard-on-each-iteration loop, **not cumulative allocated bytes**.

| Reference path | Iterations | Throughput | Elapsed | Peak traced memory |
|---|---:|---:|---:|---:|
| Audio v3 Python parse (1,049-byte packet) | 200,000 | 107,780 ops/s | 1.856 s | 1,566 B |
| Audio v3 Python serialize (1,049-byte packet) | 200,000 | 142,592 ops/s | 1.403 s | 1,232 B |
| Control v1 Python parse (15-byte heartbeat) | 200,000 | 77,916 ops/s | 2.567 s | 736 B |
| Control v1 Python serialize (15-byte heartbeat) | 200,000 | 51,168 ops/s | 3.909 s | 226 B |

These are microbenchmarks, not acceptance results. They exclude sockets, scheduling, devices, concurrent streams, queue pressure, and the .NET/Android/ESP32 runtimes. Do not compare them directly across different hosts or runtimes.

## Subsystem review

| Subsystem | Inspected paths and observed costs | Evidence and disposition |
|---|---|---|
| Audio | .NET Audio v3 parser copies payload into a new array per datagram. Sender packetization allocates a packet object and copies each payload; conversion rents and returns pooled memory. Receiver rents one reusable datagram buffer. The bounded receive queue locks around its state and allocates a `QueuedPacket` per accepted packet. Python serializer concatenates header and payload; parser slices payload and constructs a packet. Android parser/receiver and capture/sender handoff are allocation/copy candidates. | Python codec microbenchmarks are recorded above. No .NET/Android audio stream profiler or device is available. The .NET buffer copy and packet-object rate merit measurement under a real stream, but no ownership-sensitive zero-copy change is made without proving buffer lifetime and reduced GC/latency. Python receive payload slice is a copy by Python bytes semantics; it is not proven material relative to the rest of that runtime. |
| Networking | .NET UDP audio receiver reuses a rented receive buffer; it parses before enqueueing immutable packet data. Sender reuses a rented datagram buffer and a socket. Control runtime owns bounded channels and workers; control traffic includes low-rate discovery/heartbeat serialization and endpoint/message handling. Android uses datagram workers; ESP32 control paths use fixed-size buffers and bounded queues. | No sustained LAN traffic, packet capture, runtime counters, or device profiler was available. Existing bounded buffers and queues are retained. Need real Wi-Fi/LAN tests with packet rate, drops, CPU, allocation rate, and p95/p99 latency. |
| IPC | Named-pipe framing is bounded. `System.Text.Json` parses request envelopes and payload DTOs; each request has serialization/deserialization work and bounded client queues. This is control/state traffic, not PCM. | No .NET runtime or connected service/UI pair, so no throughput or allocation sampling. Current source provides no measured evidence that JSON/framing is material to end-user performance. Preserve the specified JSON contract. |
| UI | View models process snapshots/events and dispatch updates. Metric history is bounded to 60 samples. Event updates materialize immutable collections and formatted strings; peer/session updates sort or filter snapshots. | No WinUI runtime/Windows UI profiler. Snapshot/event frequency and peer/session cardinality under a representative service were unavailable. These paths are candidates for UI allocation and dispatcher timing traces only if profiling shows frame/render delays. |
| Service | Logging uses a bounded asynchronous channel, but formats and constructs structured properties before enqueue. The log writer selects the file and prunes retention per record, opens/writes/flushes for each record. Service startup composes workers and IPC. | No Windows service runtime or disk-load trace. Per-record file and directory work happens on the background sink, while log record creation happens on the producer. There is insufficient evidence to alter rotation/flush semantics; measure logging-enabled/disabled overhead and sink throughput on Windows. |
| Control Plane | Python codecs serialize/parse control packets; .NET ControlRuntime uses long-lived workers and bounded channels, with concurrent route/peer maps. Discovery/heartbeat is low-rate by design. ESP32 uses fixed peer/packet storage and bounded FreeRTOS queues. | Python heartbeat codec baseline recorded above. No real network, Android, ESP32, or .NET execution. No control-path optimization justified by the short codec benchmark. |
| Engine | EngineEventBus serializes publication, snapshots subscribers, and applies bounded backpressure. Statistics snapshots build immutable dictionaries; event publication and counters are not supposed to run per PCM frame. Audio counters use atomic/fixed storage at producer boundaries. | No .NET runtime profile or representative active-session workload. Need event rate, subscriber count, lock wait, allocation bytes, and Engine CPU sampling during peer-heavy and multi-lane runs. Do not optimize snapshot/event structure based on source inspection alone. |

## Measurement method

The measurements used a warm-up call, then 200,000 iterations of the Python protocol serializer/parser with `time.perf_counter()` and `tracemalloc`; each result was discarded immediately. The measured packet objects were a fixed 1,024-byte stereo PCM payload (plus the 25-byte Audio v3 header) and a canonical online heartbeat. The results are a single run on the current host and are sensitive to interpreter build, power policy, background load, and tracing overhead.

No implementation files were altered. No benchmark harness or package was added, so there is no permanent benchmark project yet. Record machine/runtime versions and repeat each case in a clean process before using measurements to accept an optimization.

## Hot-path candidates to revisit with platform profilers

These are hypotheses from code inspection, not confirmed bottlenecks:

1. **Audio receive allocation:** .NET `AudioProtocolV3Codec.Parse` creates payload storage per packet and `AudioUdpReceiver` enqueues that immutable packet. Measure allocated bytes/packet, Gen0 rate, CPU, queue wait, and p99 receive-to-playback time. Any pooled/zero-copy design must keep packet bytes valid until the queue consumer finishes and return storage exactly once.
2. **Audio send packetization:** `AudioUdpSender.EnqueuePayloads` copies each packet payload and creates one packet object. Measure allocations and packetizer CPU at configured sample rates and packet sizes. A change must retain exact v3 bytes, sequence ordering, drop behavior, and source-frame lifetime.
3. **Receive queue synchronization:** `AudioReceiveQueue` uses one lock for offer, take, jitter, overflow, and snapshot state, plus a dictionary scan for nearest packet on gaps/overflow. Measure contention and scan frequency at realistic packet rates and reordering; keep current semantics unless contention is observed.
4. **Engine event delivery:** each publish snapshots subscribers and may allocate a `Task` wrapper for noncritical bounded writes. Measure publication rate, subscriber count, wait time, queue occupancy, and allocation profile with a slow subscriber. PCM callbacks must continue to avoid this path.
5. **UI snapshot churn:** immutable array filtering/sorting, JSON event payload deserialization, history-array copies, and formatted properties occur on updates. Measure dispatcher queue delay and frame time with realistic event and peer counts before changing reconciliation behavior.
6. **Log sink overhead:** per-record open/flush/retention selection may limit sink throughput. Measure producer allocation/time, queue drops, records/s, and disk writes under normal and burst logging. Preserve bounded loss behavior, redaction, retention, and shutdown guarantees.

## Required follow-up measurements before accepting an optimization

- **Windows / .NET:** run Release builds with representative audio capture and playback. Capture EventPipe/PerfView CPU stacks, allocation stacks and GC pauses; collect process CPU/private bytes; add stage timestamps/counters outside the real-time callback. Test idle, one lane, simultaneous lanes, maximum configured peers, IPC clients, and logging bursts. Compare p50/p95/p99 latency and packet/drop counters before/after.
- **Android:** profile on representative physical devices using Android Studio CPU/Memory profilers or Perfetto; include capture-to-UDP and UDP-to-`AudioTrack` load, GC pauses, underruns, allocation rate, and thermal/power state.
- **ESP32:** use firmware heap/stack high-water marks, queue high-water marks, task runtime/latency instrumentation, and Wi-Fi reconnect/storm scenarios on the actual board. Confirm no dynamic allocation in timing-sensitive loop paths.
- **End-to-end LAN:** collect sender/receiver packet counters and external audio loopback latency on specified hardware/network. Run sustained and peer-heavy workloads and report p50/p95/p99, drops, queue duration, CPU, and memory. Preserve Audio v3 and Control v1 compatibility fixtures.

An optimization is accepted only when repeatable before/after data shows improvement in the target metric, behavior/protocol tests remain unchanged, and the optimization’s ownership and bounded-memory reasoning is documented alongside the code.
