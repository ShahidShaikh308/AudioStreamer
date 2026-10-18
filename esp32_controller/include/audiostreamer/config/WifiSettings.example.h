#pragma once

#define AUDIOSTREAMER_WIFI_SSID "YOUR_WIFI_SSID"
#define AUDIOSTREAMER_WIFI_PASSWORD "YOUR_WIFI_PASSWORD"

// Set the same 64 hexadecimal characters as ControlPlane:ControllerSecret on all nodes.
// Generate a random 32-byte key and never commit the private WifiSettings.h file.
#define AUDIOSTREAMER_CONTROLLER_SECRET_HEX ""
