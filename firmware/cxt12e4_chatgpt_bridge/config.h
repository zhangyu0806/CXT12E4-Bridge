// Copyright 2026
// SPDX-License-Identifier: GPL-2.0-or-later

#pragma once

#define RAW_USAGE_PAGE 0xFF60
#define RAW_USAGE_ID 0x61
#define RAW_EPSIZE 32

// The bridge owns the visible state. If it disappears, stale status lights are
// automatically cleared after this interval.
#define CXT12E4_BRIDGE_TIMEOUT_MS 3000
