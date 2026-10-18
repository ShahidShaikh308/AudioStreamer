"""Cross-language v3 wire checks. Python is the oracle; .NET receives Python bytes and vice versa."""

from __future__ import annotations

import base64
import json
import os
import subprocess
import unittest
from pathlib import Path

from audio_plane.protocol import AudioPacket, SampleFormat, parse_packet, serialize_packet

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = bytes.fromhex((ROOT / "tests" / "ProtocolFixtures" / "audio_v3_canonical.hex").read_text().strip())
PROJECT = ROOT / "tools" / "AudioStreamer.ProtocolCompat" / "AudioStreamer.ProtocolCompat.csproj"


def dotnet_exchange(request: dict[str, object]) -> str:
    if os.environ.get("AUDIOSTREAMER_SKIP_DOTNET_COMPAT") == "1":
        raise unittest.SkipTest(".NET compatibility tool disabled by environment")
    process = subprocess.run(
        ["dotnet", "run", "--project", str(PROJECT), "--no-restore", "--verbosity", "quiet"],
        input=json.dumps(request), text=True, capture_output=True, cwd=ROOT, check=False,
        timeout=60,
    )
    if process.returncode:
        raise AssertionError(f".NET compatibility tool failed: {process.stderr}")
    return process.stdout


class DotNetProtocolCompatibilityTests(unittest.TestCase):
    def test_python_reference_matches_canonical_vector(self) -> None:
        packet = AudioPacket(0x01020304, 0x0102030405060708, 48000, 2, SampleFormat.FLOAT32_LE,
                             bytes.fromhex("0000803f000080bf"))
        self.assertEqual(serialize_packet(packet), FIXTURE)
        self.assertEqual(parse_packet(FIXTURE), packet)

    def test_dotnet_parses_python_canonical_datagram(self) -> None:
        result = json.loads(dotnet_exchange({"operation": "parse", "datagram": base64.b64encode(FIXTURE).decode()}))
        self.assertEqual(result["sequenceNumber"], 0x01020304)
        self.assertEqual(result["streamId"], 0x0102030405060708)
        self.assertEqual(result["sampleRate"], 48000)
        self.assertEqual(base64.b64decode(result["payload"]), bytes.fromhex("0000803f000080bf"))

    def test_python_parses_dotnet_serialized_datagram(self) -> None:
        payload = bytes.fromhex("0000803f000080bf")
        encoded = dotnet_exchange({
            "operation": "serialize", "sequence": str(0x01020304),
            "streamId": str(0x0102030405060708), "sampleRate": 48000,
            "channels": 2, "sampleFormat": 1, "payload": base64.b64encode(payload).decode(),
        })
        datagram = base64.b64decode(encoded)
        self.assertEqual(datagram, FIXTURE)
        packet = parse_packet(datagram)
        self.assertEqual(packet, AudioPacket(0x01020304, 0x0102030405060708, 48000, 2, SampleFormat.FLOAT32_LE, payload))


if __name__ == "__main__":
    unittest.main()
