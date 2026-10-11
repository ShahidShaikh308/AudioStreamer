"""Human-readable diagnostics for Control Plane startup errors."""


def format_startup_error(port: int, reason: str) -> str:
    return (
        f"AudioStreamer could not start the Control Plane on UDP port {port}.\n\n"
        f"Details: {reason}\n\n"
        "Check that no unrelated application owns the port, Windows Firewall permits "
        "local UDP traffic, and every local AudioStreamer component uses the same port."
    )
