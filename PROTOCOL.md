# AudioStreamer UDP Protocol

**Current wire version: 2.** This document defines the on-wire format for
independent sender and receiver implementations. The protocol carries PCM; it
does not provide retransmission, encryption, congestion control, or clock sync.

## Transport and byte order

Packets are sent as one UDP datagram each. The maximum protocol datagram size
is **1200 bytes**, including the protocol header. This conservative cap avoids
IP fragmentation on common IPv4 and IPv6 LAN paths. All multi-byte integer
fields, including extension TLV fields, are unsigned and use **network byte
order (big endian)**. PCM sample bytes use the endianness specified by the
sample-format identifier.

The header has a fixed **40-byte v2 base**. A v2 packet may append a TLV
extension area to that base; `header_length` gives the complete header size.
The audio payload starts at `header_length`.

## Version 2 packet layout

Offsets are from the first datagram byte.

| Offset | Size | Field | Meaning |
|---:|---:|---|---|
| 0 | 4 | Magic | ASCII `ASAU` (`0x41534155`) |
| 4 | 1 | Version | `2` |
| 5 | 1 | Packet type | See packet types below |
| 6 | 2 | Header length | Bytes from offset 0 through extensions; minimum 40 |
| 8 | 2 | Flags | Bit field; defined flags below |
| 10 | 4 | Sequence number | Increments once per datagram within a stream; wraps modulo 2^32 |
| 14 | 8 | Stream ID | Nonzero identifier chosen once per sender run |
| 22 | 8 | Timestamp | Sender monotonic-clock nanoseconds at the first sample in this packet |
| 30 | 4 | Sample rate | Samples per second per channel; zero for non-audio packets |
| 34 | 2 | Channel count | Interleaved channels; zero for non-audio packets |
| 36 | 1 | Sample format | See sample formats below; zero for non-audio packets |
| 37 | 1 | Reserved | Must be zero |
| 38 | 2 | Payload length | Number of bytes after the complete header |
| 40 | variable | Header extensions | Zero or more padded TLVs; ends at `header_length` |
| `header_length` | variable | Payload | Packet-type-specific bytes; audio payload is interleaved PCM |

The timestamp uses the sender's monotonic clock. It is suitable for ordering
and relative timing within one stream, but must not be compared directly with a
different device's monotonic clock. The stream ID distinguishes a sender run
from an earlier run whose sequence numbers may also start at zero.

### Header extensions

The extension area is a sequence of TLVs. Each TLV is:

| Size | Field |
|---:|---|
| 2 | Extension type (nonzero) |
| 2 | Value length in bytes |
| variable | Value |
| 0–3 | Zero padding so the TLV occupies a multiple of four bytes |

Unknown extension types must be skipped using their length and padding. A v2
implementation must not reject a packet merely because it contains an
extension it does not understand. Extension meanings will be assigned by a
future protocol registry before use; implementations must ignore unassigned
types. The complete base plus extension area must fit within the 1200-byte
datagram cap.

### Flags

| Bit | Name | Meaning |
|---:|---|---|
| 0 | `DISCONTINUITY` | Sender indicates a gap or reset in the audio timeline |
| 1 | `END_OF_STREAM` | Sender will send no further audio for this stream |
| 2–15 | Unassigned | Receivers must ignore unknown bits |

Flags do not replace sequence validation. Unknown flag bits must be ignored so
future senders can add noncritical behavior without breaking v2 receivers.

## Packet types

| Value | Name | Payload and format fields |
|---:|---|---|
| 1 | `AUDIO` | Nonempty PCM payload; sample rate and channel count are nonzero |
| 2 | `CONTROL` | Control-message payload; audio format fields are zero |
| 3 | `KEEPALIVE` | Empty payload; audio format fields are zero |
| Other | Unassigned | Ignore the packet type; do not interpret its payload as audio |

Control payloads are opaque in v2. A future control-message definition must
specify its own versioning and byte order.

## Sample formats

| Value | Name | Bytes per sample | Representation |
|---:|---|---:|---|
| 0 | `UNKNOWN` | — | Non-audio packets only |
| 1 | `FLOAT32_LE` | 4 | IEEE-754 binary32, little endian, normalized audio |
| 2 | `PCM_S16_LE` | 2 | Signed 16-bit integer PCM, little endian |
| 3 | `PCM_S24_LE` | 3 | Signed 24-bit two's-complement PCM, little endian |
| 4 | `PCM_S32_LE` | 4 | Signed 32-bit integer PCM, little endian |
| Other | Unassigned | — | Preserve/skip if possible; never guess an encoding |

The current Python sender emits `FLOAT32_LE`. The Python playback receiver
currently plays only that format. For audio packets, payload length must be a
multiple of `channel_count × bytes_per_sample`. Each sample frame contains one
sample for each channel, in channel order.

## Maximum payload size

The maximum UDP datagram size is **1200 bytes**. Therefore, a v2 packet with
no extensions can carry at most **1160 payload bytes** (`1200 − 40`). Any
extension bytes reduce that maximum by the same number of bytes. Senders must
keep each datagram within the cap and split larger PCM buffers only at complete
sample-frame boundaries. A legacy v1 datagram can carry at most 1172 payload
bytes because its header is 28 bytes.

## Legacy version 1 compatibility

Version 1 is the original AudioStreamer format and is audio-only. Its fixed
28-byte header is:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | Magic `ASAU` |
| 4 | 1 | Version `1` |
| 5 | 1 | Sample format (`1`, `FLOAT32_LE`) |
| 6 | 2 | Reserved flags; must be zero |
| 8 | 4 | Sequence number |
| 12 | 8 | Sender monotonic timestamp in nanoseconds |
| 20 | 4 | Sample rate |
| 24 | 2 | Channel count |
| 26 | 2 | Payload length |
| 28 | variable | Float32 little-endian PCM payload |

V1 has no packet type, stream ID, extensions, or assigned flags. A v2 parser
must accept valid v1 audio packets. A v2 sender may select v1 when sending to
an older receiver, but v1 cannot identify sender restarts and supports only
float32 audio.

## Validation rules

Receivers must:

1. Reject datagrams larger than 1200 bytes or shorter than the relevant base
   header.
2. Verify magic, supported version, fixed-header reserved fields, and that
   `header_length` is at least 40 and no greater than the datagram length.
3. Verify the declared payload length equals the bytes remaining after the
   complete header; do not accept trailing bytes.
4. Validate extension TLV bounds, nonzero types, and zero padding, then skip
   unknown extension types.
5. Validate known audio sample formats and whole-frame payload alignment.
   Unknown formats must never be played as a guessed format.
6. Ignore unknown packet types and unknown noncritical flags.
7. Use sequence numbers to detect duplicates, reordering, and loss within a
   stream. A changed nonzero stream ID starts a new sequence space.

Senders must use a nonzero v2 stream ID, increment sequence per datagram, set
the v2 reserved byte to zero, and avoid IP fragmentation by observing the
1200-byte cap. Sequence comparison uses modulo-2^32 arithmetic; a forward
distance below 2^31 is considered newer.

## Versioning strategy

- The magic identifies the protocol family; the version byte identifies the
  wire layout. A change that alters the meaning or location of existing base
  fields requires a new version. Never reuse a version number for a different
  layout.
- V1 remains frozen. V2 adds the packet type, explicit header length, stream
  ID, sample-format field, and extension mechanism. V2 parsers retain v1
  decoding for audio compatibility; senders can explicitly emit v1 for old
  receivers.
- V2 additions that fit the extension TLV mechanism do not change the base
  layout. Unknown TLVs and unknown flag bits are ignored. Existing assigned
  identifiers must never be redefined.
- A new major wire version is required if a future change cannot be represented
  by optional TLVs or changes existing field semantics. Implementations should
  reject unknown major versions rather than guessing their layout.

## Current implementation scope

The Python protocol modules serialize and parse v1 and v2. The Python sender
uses v2 by default and can emit v1 with `--protocol-version 1`. The Python
receiver accepts both versions, but plays only v2/v1 float32 audio. Android,
ESP32, and STM32 implementations are not included here.
