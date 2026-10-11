# Windows Device Manager

The Device Manager is a standalone Control Plane client. It has no capture, audio playback, or PCM transport. Launch it with `AudioStreamerDeviceManager.bat` after the Windows Python environment has been prepared with `AudioStreamer.bat`.

The peer table displays announced node identities, addresses, capabilities, and status. Select a sender and receiver and issue `START_STREAM` or `STOP_STREAM`. These commands are best-effort UDP messages. The UI reports that a command was sent; it cannot guarantee the receiving node accepted it or that audio is audible. A sender still has one configured audio destination.

The client persists its local name and ports under the user's application data directory. It shares the Windows Control Plane broker with the Windows audio node; all local applications must use the same UDP Control Plane port. The broker owns the single UDP socket.
