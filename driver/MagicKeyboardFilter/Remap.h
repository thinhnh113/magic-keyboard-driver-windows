// Remap engine: rewrites one Magic Keyboard input report in place. Pure C, no WDF, no allocation,
// so it runs at DISPATCH_LEVEL in the driver and builds unchanged in tests/remap_test.c.
//
// Report layout (Magic Keyboard family, from the SDP HID descriptor of a 2024 Touch ID model):
//   [0] = 0x01 report ID      [1] = modifier bits (HID 0xE0..0xE7)   [2] = reserved
//   [3..8] = up to 6 key usages (page 7)
//   [9] bit1 = Fn (vendor 0xFF01:0x03), bit2 = Touch ID / lock key (consumer 0x40)
// Anything else (other report IDs, shorter reports, rollover) passes through untouched.
#pragma once
#include "Public.h"

#define MK_REPORT_ID        0x01
#define MK_REPORT_MIN_LEN   10
#define MK_MAX_KEYS         6
#define MK_MAX_EVENTS       8
#define MK_FN_BIT           0x02
#define MK_TOUCHID_BIT      0x04
#define MK_USAGE_TOUCHID    0xF0   // pseudo usage for the Touch ID key in the key state tables

// Compiled config: immutable while a report is processed.
typedef struct _MK_REMAP_CONFIG {
    BOOLEAN Enabled;
    BOOLEAN FnLock;
    BOOLEAN SleepShortcut;
    UCHAR ClearMode;
    UCHAR ModMap[8];       // physical modifier bit i -> MK_MOD_TARGET
    UCHAR CapsTarget;      // MK_MOD_TARGET for the Caps Lock key
    UCHAR FnTarget;        // MK_MOD_TARGET for the Fn key (MkModFn = normal Fn layer)
    UCHAR MenuTarget;      // MK_MOD_TARGET for an Application/Menu key, if the board has one
    UCHAR FAction[MK_FKEY_COUNT];
    UCHAR FCustom[MK_FKEY_COUNT];
    UCHAR TouchIdAction;
} MK_REMAP_CONFIG;

// Per-keyboard runtime state: what each held physical key was mapped to when it went down.
typedef struct _MK_KEY_STATE {
    UCHAR Down[256 / 8];   // physical keys held in the previous report (bitmap)
    UCHAR Out[256];        // output usage for a held key (0 = none)
    UCHAR OutMods[256];    // modifier bits held with it (chords such as Win+Tab)
    UCHAR Action[256];     // app action for a held key (MkActionNone = none)
    UCHAR Custom[256];
} MK_KEY_STATE;

typedef struct _MK_ACTION_EVENT {
    UCHAR Action;
    UCHAR Down;
    UCHAR CustomSlot;
} MK_ACTION_EVENT;

void MkCompileConfig(MK_REMAP_CONFIG* out, const MK_CONFIG* in);
BOOLEAN MkConfigValid(const MK_CONFIG* in);
void MkResetKeyState(MK_KEY_STATE* st);

// Returns TRUE if the report was recognized as a keyboard report (and possibly rewritten).
// appConnected: actions that need the app fall back to the plain key when no app is listening.
// events/eventCount: app actions to deliver (press and release).
BOOLEAN MkProcessReport(const MK_REMAP_CONFIG* cfg, MK_KEY_STATE* st, unsigned char* report,
                        unsigned long length, BOOLEAN appConnected,
                        MK_ACTION_EVENT* events, int* eventCount);
