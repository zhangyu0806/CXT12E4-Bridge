// Copyright 2026
// SPDX-License-Identifier: GPL-2.0-or-later

#include QMK_KEYBOARD_H
#include "bootloader.h"
#include "raw_hid.h"
#include <string.h>

#define CXT_MAGIC_0 0x43  // C
#define CXT_MAGIC_1 0x31  // 1
#define CXT_MAGIC_2 0x32  // 2
#define CXT_MAGIC_3 0x45  // E
#define CXT_PROTOCOL_VERSION 0x01
#define CXT_COMMAND_PING 0x01
#define CXT_COMMAND_SET_STATE 0x02
#define CXT_COMMAND_SET_TEST_RGB 0x03
#define CXT_COMMAND_BOOTLOADER 0x7F
#define CXT_RESPONSE_BIT 0x80

enum cxt_task_state {
    CXT_TASK_OFF = 0,
    CXT_TASK_IDLE = 1,
    CXT_TASK_THINKING = 2,
    CXT_TASK_COMPLETED = 3,
    CXT_TASK_WAITING = 4,
    CXT_TASK_ERROR = 5,
};

// QMK's LED order for the six physical task keys 1,2,3,5,6,7.
static const uint8_t task_leds[6] = {3, 2, 1, 4, 5, 6};

// Global terminal session 1..6 start on the bottom row: keys 9,10,11,12,8,4.
static const uint8_t terminal_leds[6] = {11, 10, 9, 8, 7, 0};

// QMK LED index for physical keys 1..12.
static const uint8_t key_leds[12] = {3, 2, 1, 0, 4, 5, 6, 7, 11, 10, 9, 8};

// Logical layer indicators follow the user's visible order: L1=key1 through
// L7=key7, with the fallback L0 on key8. The marker only overlays the live
// status during its two pulses.
static const uint8_t layer_leds[8] = {7, 3, 2, 1, 0, 4, 5, 6};

static uint8_t logical_layer;
static uint8_t task_states[6];
static uint8_t terminal_states[6];
static uint32_t last_bridge_packet;
static uint32_t layer_changed_at;
static bool bridge_state_valid;
static bool danger_flash_active;
static bool test_rgb_active;
static uint8_t test_key;
static uint8_t test_red;
static uint8_t test_green;
static uint8_t test_blue;
static bool bootloader_requested;
static uint32_t bootloader_requested_at;

const uint16_t PROGMEM keymaps[][MATRIX_ROWS][MATRIX_COLS] = {
    [0] = LAYOUT(
        KC_F13, KC_F14, KC_F15, KC_F16,
        KC_F17, KC_F18, KC_F19, KC_F20,
        KC_F21, KC_F22, KC_F23, KC_F24,
        RCTL(KC_F23), RCTL(KC_F20), RCTL(KC_F14), RCTL(KC_F17)
    )
};

static void tap_encoder_trigger(uint16_t keycode) {
    tap_code16(RCTL(keycode));
}

bool encoder_update_user(uint8_t index, bool clockwise) {
    switch (index) {
        case 0: // K1: RCtrl+F13 / RCtrl+F15
            tap_encoder_trigger(clockwise ? KC_F15 : KC_F13);
            break;
        case 1: // K2: RCtrl+F16 / RCtrl+F18
            tap_encoder_trigger(clockwise ? KC_F18 : KC_F16);
            break;
        case 2: // K3: RCtrl+F19 / RCtrl+F21
            tap_encoder_trigger(clockwise ? KC_F21 : KC_F19);
            break;
        case 3: // K4: RCtrl+F22 / RCtrl+F24
            tap_encoder_trigger(clockwise ? KC_F24 : KC_F22);
            break;
    }

    // Stop the keyboard-level default encoder actions. Every encoder gesture is
    // translated by the Windows bridge according to the current logical layer.
    return false;
}

void keyboard_post_init_user(void) {
    // Ignore any stale factory EEPROM lighting state: the bridge owns every LED.
    rgb_matrix_enable_noeeprom();
    rgb_matrix_mode_noeeprom(RGB_MATRIX_SOLID_COLOR);
    rgb_matrix_sethsv_noeeprom(0, 0, 255);
    logical_layer = 0;
    memset(task_states, CXT_TASK_OFF, sizeof(task_states));
    memset(terminal_states, CXT_TASK_OFF, sizeof(terminal_states));
    layer_changed_at = timer_read32();
    bridge_state_valid = false;
    danger_flash_active = false;
    test_rgb_active = false;
    bootloader_requested = false;
}

void housekeeping_task_user(void) {
    if (bootloader_requested && timer_elapsed32(bootloader_requested_at) > 80u) {
        bootloader_jump();
    }
    if (bridge_state_valid && timer_elapsed32(last_bridge_packet) > CXT12E4_BRIDGE_TIMEOUT_MS) {
        bridge_state_valid = false;
        danger_flash_active = false;
        test_rgb_active = false;
        logical_layer = 0;
        memset(task_states, CXT_TASK_OFF, sizeof(task_states));
        memset(terminal_states, CXT_TASK_OFF, sizeof(terminal_states));
    }
}

static bool valid_packet(const uint8_t *data, uint8_t length) {
    return length == RAW_EPSIZE &&
           data[0] == CXT_MAGIC_0 &&
           data[1] == CXT_MAGIC_1 &&
           data[2] == CXT_MAGIC_2 &&
           data[3] == CXT_MAGIC_3 &&
           data[4] == CXT_PROTOCOL_VERSION;
}

void raw_hid_receive(uint8_t *data, uint8_t length) {
    if (!valid_packet(data, length)) {
        return;
    }

    uint8_t command = data[5];
    if (command == CXT_COMMAND_SET_STATE) {
        uint8_t next_layer = data[6] <= 7 ? data[6] : 0;
        bool restart_layer_marker = !bridge_state_valid || test_rgb_active || next_layer != logical_layer;
        test_rgb_active = false;
        logical_layer = next_layer;
        if (restart_layer_marker) {
            layer_changed_at = timer_read32();
        }
        danger_flash_active = data[14] == 1;
        for (uint8_t i = 0; i < 6; i++) {
            task_states[i] = data[7 + i] <= CXT_TASK_ERROR ? data[7 + i] : CXT_TASK_OFF;
            terminal_states[i] = data[15 + i] <= CXT_TASK_ERROR ? data[15 + i] : CXT_TASK_OFF;
        }
        last_bridge_packet = timer_read32();
        bridge_state_valid = true;
    } else if (command == CXT_COMMAND_SET_TEST_RGB) {
        if (data[6] < 1 || data[6] > 12) {
            return;
        }
        test_key = data[6] - 1;
        test_red = data[7];
        test_green = data[8];
        test_blue = data[9];
        test_rgb_active = true;
        danger_flash_active = false;
        logical_layer = 0;
        last_bridge_packet = timer_read32();
        bridge_state_valid = true;
    } else if (command == CXT_COMMAND_BOOTLOADER) {
        if (data[7] != 0x42 || data[8] != 0x4F || data[9] != 0x4F || data[10] != 0x54) {
            return;
        }
        bootloader_requested = true;
        bootloader_requested_at = timer_read32();
    } else if (command != CXT_COMMAND_PING) {
        return;
    }

    data[5] = command | CXT_RESPONSE_BIT;
    data[6] = logical_layer;
    raw_hid_send(data, length);
}

static uint8_t layer_indicator_brightness(void) {
    // Start a visible two-breath marker immediately after a layer change and
    // repeat it every 1.5 seconds. Each pulse rises to full brightness and
    // falls back to a visible floor before the live status color is restored.
    uint16_t phase = (uint16_t)(timer_elapsed32(layer_changed_at) % 1500u);
    uint16_t pulse_phase;
    if (phase < 220u) {
        pulse_phase = phase;
    } else if (phase >= 340u && phase < 560u) {
        pulse_phase = (uint16_t)(phase - 340u);
    } else {
        return 0;
    }

    uint16_t rise = pulse_phase <= 110u ? pulse_phase : (uint16_t)(220u - pulse_phase);
    return (uint8_t)(112u + ((rise * 143u) / 110u));
}

static uint8_t cxt_triangle_wave(uint8_t phase) {
    return phase < 128u ? (uint8_t)(phase * 2u) : (uint8_t)((255u - phase) * 2u);
}

static void set_task_color(uint8_t led, uint8_t state, uint8_t task_slot) {
    switch (state) {
        case CXT_TASK_IDLE:
            rgb_matrix_set_color(led, 0xFF, 0xFF, 0xFF);
            break;
        case CXT_TASK_THINKING:
            // One LED cannot show spatial movement by itself, so each thinking
            // key runs a saturated deep-blue brightness wave. Adjacent task slots are
            // phase shifted; multiple concurrent tasks therefore flow left-to-right.
            {
                uint8_t phase = (uint8_t)((timer_read32() / 10u) + ((uint16_t)task_slot * 43u));
                uint8_t wave = cxt_triangle_wave(phase);
                uint8_t brightness = (uint8_t)(56u + (((uint16_t)wave * 199u) / 255u));
                rgb_matrix_set_color(led, 0x00, 0x00, brightness);
            }
            break;
        case CXT_TASK_COMPLETED:
            rgb_matrix_set_color(led, 0x00, 0xB0, 0x00);
            break;
        case CXT_TASK_WAITING:
            rgb_matrix_set_color(led, 0xFF, 0xB0, 0x00);
            break;
        case CXT_TASK_ERROR:
            rgb_matrix_set_color(led, 0xFF, 0x00, 0x00);
            break;
        default:
            rgb_matrix_set_color(led, 0x00, 0x00, 0x00);
            break;
    }
}

static uint8_t scale_layer_channel(uint8_t channel, uint8_t brightness) {
    return (uint8_t)(((uint16_t)channel * brightness) / 255u);
}

static void set_layer_color(uint8_t layer, uint8_t brightness) {
    uint8_t led = layer_leds[layer];
    uint8_t red = 0;
    uint8_t green = 0;
    uint8_t blue = 0;
    switch (layer) {
        case 0:
            red = 0x70;
            green = 0x88;
            blue = 0xB8;
            break;
        case 1:
            red = 0xC0;
            blue = 0xC8;
            break;
        case 2:
            red = 0xFF;
            green = 0x14;
            blue = 0x64;
            break;
        case 3:
            red = 0xFF;
            green = 0x60;
            break;
        case 4:
            red = 0x80;
            blue = 0xFF;
            break;
        case 5:
            red = 0xFF;
            green = 0xDC;
            break;
        case 6:
            green = 0xC8;
            blue = 0xFF;
            break;
        case 7:
            green = 0xFF;
            break;
    }
    rgb_matrix_set_color(led,
                         scale_layer_channel(red, brightness),
                         scale_layer_channel(green, brightness),
                         scale_layer_channel(blue, brightness));
}

bool rgb_matrix_indicators_advanced_user(uint8_t led_min, uint8_t led_max) {
    for (uint8_t i = led_min; i < led_max; i++) {
        rgb_matrix_set_color(i, 0x00, 0x00, 0x00);
    }

    if (!bridge_state_valid) {
        return false;
    }

    if (danger_flash_active) {
        uint8_t danger_led = key_leds[8];
        if (danger_led >= led_min && danger_led < led_max) {
            rgb_matrix_set_color(danger_led, 0xFF, 0x00, 0x00);
        }
    } else if (test_rgb_active) {
        rgb_matrix_set_color(key_leds[test_key], test_red, test_green, test_blue);
    } else {
        // Both six-key banks are global: ChatGPT tasks on the left bank and tmux
        // sessions from the bottom row upward remain visible on every logical layer.
        for (uint8_t i = 0; i < 6; i++) {
            set_task_color(task_leds[i], task_states[i], i);
            set_task_color(terminal_leds[i], terminal_states[i], i);
        }
        uint8_t layer_brightness = layer_indicator_brightness();
        if (logical_layer <= 7 && layer_brightness > 0) {
            set_layer_color(logical_layer, layer_brightness);
        }
    }

    // L1-L7 time-multiplex a breathing double-flash on physical keys 1-7;
    // fallback L0 uses key8. No global status light is hidden permanently.
    return false;
}
