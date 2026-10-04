# AudioStreamer UDP Protocol

**Active wire format: protocol v3.** This document is the normative source for the packet layout and validation rules used by the Windows sender and Android receiver. V3 carries one audio packet per UDP datagram. It does not provide acknowledgements, retransmission, encryption, clock synchronization, or congestion control.

## V2 field audit and v3 decisions

V2's 40-byte base header grew around features this product does not implement. Changing its field meanings would silently break existing v2 peers, so v2 remains a retired wire format and the compact layout uses version 3. The release implementations send and accept v3 only.

| V2 field | Purpose in v2 | Required by this application? | V3 decision |
|---|---|---:|---|
| Magic (4 bytes) | Identifies AudioStreamer datagrams | Yes | Keep `ASAU`. |
| Version (1 byte) | Selects header interpretation | Yes | Keep and set to `3`; this layout is incompatible with v2. |
| Packet type (1 byte) | Distinguishes audio, control, and keepalive | No | Remove; v3 currently carries audio only. |
| Header length (2 bytes) | Locates payload after optional extensions | No | Remove; v3 has one fixed header. |
| Flags (2 bytes) | Discontinuity and end-of-stream hints | No | Remove; sequence numbers detect discontinuity; stream ID detects sender restart. There is no control/end message in the current one-way stream. |
| Sequence number (4 bytes) | Detects gaps, duplicates, and reordering | Yes | Keep; unsigned, wraps modulo 2^32. |
| Stream ID (8 bytes) | Separates a sender run from previous sequence space | Yes | Keep; nonzero random value selected once per sender run. |
| Timestamp (8 bytes) | Sender monotonic time of the first sample | No | Remove; this receiver has no cross-device clock synchronization or timestamp-based playout. Packet duration is derived from payload frames and sample rate. |
| Sample rate (4 bytes) | Describes PCM rate | Yes | Keep, in samples per second per channel. |
| Channel count (2 bytes) | Describes interleaved channel layout | Yes | Keep as one byte; this implementation requires stereo (`2`). |
| Sample format (1 byte) | Describes sample encoding | Yes | Keep as one byte; v3 currently assigns `1` to IEEE-754 float32 little-endian. |
| Reserved (1 byte) | Kept for alignment/future use | No | Remove; reserved bytes add no current value. |
| Payload length (2 bytes) | Checks declared payload against datagram | Yes | Keep and require exact equality with remaining datagram bytes. |
| Extension TLVs | Future metadata mechanism | No | Remove; no extension is currently needed. Any later incompatible header revision receives a new version. |

## V3 packet layout

Each datagram begins with this fixed **25-byte header**. Offsets are measured from the first byte of the UDP payload. All multi-byte integer fields are unsigned and big endian (network byte order). PCM sample bytes are little endian as specified by `sample_format`.

| Offset | Size | Field | Meaning |
|---:|---:|---|---|
| 0 | 4 | Magic | ASCII `ASAU` (`0x41534155`) |
| 4 | 1 | Version | `3` |
| 5 | 4 | Sequence number | Starts at zero for a stream; increments for each datagram; wraps modulo 2^32 |
| 9 | 8 | Stream ID | Nonzero random identifier, constant for a sender run |
| 17 | 4 | Sample rate | Samples per second per channel; valid range `1..2^31−1` for Android `AudioTrack` |
| 21 | 1 | Channel count | `2` for interleaved stereo |
| 22 | 1 | Sample format | `1` = `FLOAT32_LE` |
| 23 | 2 | Payload length | Number of PCM bytes following this header |
| 25 | variable | PCM payload | Interleaved stereo IEEE-754 binary32 samples, little endian |

The header is followed immediately by exactly `payload_length` bytes. There is no padding, extension area, packet type, or trailing data.

### Canonical example

This packet has sequence `0x01020304`, stream ID `0x0102030405060708`, sample rate 48000, stereo float32, and two sample values (`1.0`, `-1.0`). The whitespace separates fields only:

```text
41 53 41 55 | 03 | 01 02 03 04 | 01 02 03 04 05 06 07 08 |
00 00 BB 80 | 02 | 01 | 00 08 |
00 00 80 3F 00 00 80 BF
```

This canonical byte sequence is suitable for checking independent serializers and parsers.

## Limits and packet duration

The maximum UDP datagram size is **1200 bytes**, including the header. V3 therefore allows at most 1175 payload bytes by field/datagram size. Stereo float32 frames are 8 bytes, so a full-size audio payload must be frame-aligned and is at most **1168 bytes** (146 stereo frames); the 7 remaining bytes cannot hold another complete frame. Senders split larger capture blocks only at complete frames.

For each packet:

```text
packet_duration_ms = payload_length / (channels * 4) / sample_rate * 1000
```

The current Android software playback queue limits the sum of queued packet durations to **80 ms**, rather than limiting a fixed number of packets. It evicts oldest packets when that time budget would be exceeded. Thus packet-size or duration changes do not silently change the queue's time budget.

For equal-sized packets the equivalent packet count is `floor(target_buffer_ms / packet_duration_ms)`. The implementation uses the more exact sum-of-durations bound because the sender can produce a shorter final packet from a capture chunk.

## Sample format

| Value | Name | Bytes/sample | Representation |
|---:|---|---:|---|
| 1 | `FLOAT32_LE` | 4 | IEEE-754 binary32, little endian |

V3 implementations in this release accept only format `1`, stereo, and nonempty payloads containing complete frames. Adding a different sample format requires coordinated sender/receiver support and an update to this specification before use.

## Validation rules

Receivers must:

1. Reject datagrams longer than 1200 bytes or shorter than the 25-byte header.
2. Verify the magic and require version `3`; do not guess or reinterpret v1/v2 packets.
3. Require a nonzero stream ID, sample rate in `1..2^31−1`, channel count `2`, and supported format `1`.
4. Require a nonempty payload whose declared length equals all bytes after the fixed header.
5. Require payload size divisible by `channels × 4`, so every packet contains complete stereo float32 frames.
6. Use modulo-2^32 sequence comparisons within a stream ID. A changed stream ID starts a new sequence space. A forward distance below 2^31 is newer; a larger distance is treated as duplicate/reordered.

Senders must use a new nonzero stream ID at process start, sequence datagrams consecutively within that stream, describe the actual captured sample rate/channels, and keep every datagram within the limit.

## Versioning

V3 is deliberately smaller than the retired v2 format. V1/v2 datagrams are rejected by the v3 release implementations. Never change the meaning, size, or order of a v3 field while retaining version `3`. Any incompatible change requires a new version byte and a coordinated sender/receiver update. Do not reserve fields or extension machinery until a concrete use exists.

## End-to-end format agreement

- Windows WASAPI capture supplies interleaved float32 PCM at the selected default loopback endpoint's mix sample rate and stereo channel layout.
- The Python sender divides capture chunks into frame-aligned payloads, fills v3 format and sequence fields, and serializes each packet using this layout.
- The Android parser validates this header and copies the PCM bytes into an `AudioPacket` for asynchronous queueing.
- The time-bounded queue measures payload frames/sample rate; `AudioPlayer` converts each little-endian float bit pattern to a Kotlin `Float` and writes it to a stereo `AudioTrack` at the packet's sample rate.

No resampling or channel conversion is performed. Capture, packet, parser, queue, and AudioTrack formats must therefore agree on stereo float32 PCM and the advertised sample rate.
