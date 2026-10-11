#include <Arduino.h>

#include "audiostreamer/app/ControllerApp.h"
#include "audiostreamer/config/ControllerConfig.h"

void setup() {
    Serial.begin(115200);
    audiostreamer::config::ControllerConfig config;
    audiostreamer::app::ControllerApp app(config);
    if (!app.initialize()) {
        Serial.println("Hardware initialization failed. Check OLED wiring and Wi-Fi settings.");
        return;
    }
    Serial.println("Hardware initialization complete.");

    for (;;) {
        app.poll();
        delay(5);
    }
}

void loop() {}
