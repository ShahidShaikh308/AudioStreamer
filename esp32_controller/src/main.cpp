#include <Arduino.h>

#include "audiostreamer/app/ControllerApp.h"
#include "audiostreamer/config/ControllerConfig.h"

void setup() {
    Serial.begin(115200);
    // ControllerApp owns several fixed-capacity UI and network buffers. Keep it in
    // static storage so the Arduino setup-task stack is reserved for polling work.
    static audiostreamer::config::ControllerConfig config;
    static audiostreamer::app::ControllerApp app(config);
    if (!app.initialize()) {
        Serial.println("AudioStreamer initialization failed; see the stage reported above.");
        return;
    }
    Serial.println("Hardware initialization complete.");

    for (;;) {
        app.poll();
        delay(5);
    }
}

void loop() {}
