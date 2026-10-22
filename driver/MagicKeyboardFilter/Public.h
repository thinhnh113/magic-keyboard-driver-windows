// Shared definitions between MagicKeyboardFilter.sys and the user-mode app.
// Keep in sync with shared/MagicKeyboard.Shared/Protocol.cs.
#pragma once

#define MKF_DEVICE_NAME     L"\\Device\\MagicKeyboardFilter"
#define MKF_SYMLINK_NAME    L"\\DosDevices\\MagicKeyboardFilter"
#define MKF_USER_PATH       "\\\\.\\MagicKeyboardFilter"

#define MKF_PROTOCOL_VERSION 4
#define MKF_MAX_DEVICES      8
#define MKF_EVENT_DATA       32

#define MKF_IOCTL(fn) CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800 + (fn), METHOD_BUFFERED, FILE_ANY_ACCESS)

// out: MKF_STATUS
#define IOCTL_MKF_GET_STATUS  MKF_IOCTL(0)
// in: MK_CONFIG (applied to every keyboard and persisted in the driver's Parameters key)
#define IOCTL_MKF_SET_CONFIG  MKF_IOCTL(1)
// out: MK_CONFIG
#define IOCTL_MKF_GET_CONFIG  MKF_IOCTL(2)
// in: ULONG flags (MKF_SUB_*). Marks this handle as the action consumer.
#define IOCTL_MKF_SUBSCRIBE   MKF_IOCTL(3)
// out: MKF_EVENT (pends until an event is available)
#define IOCTL_MKF_READ_EVENT  MKF_IOCTL(4)

#define MKF_SUB_ACTIONS 0x1   // receive key actions the app performs (media, brightness, ...)
#define MKF_SUB_RAW     0x2   // also receive every raw keyboard report (diagnostics)

#define MK_CONFIG_MAGIC   0x4D4B4333  // 'MKC3'
#define MK_CONFIG_VERSION 3
#define MK_FKEY_COUNT     19

// What a top-row key does in its "media" state, or what the Touch ID key does.
typedef enum _MK_ACTION {
    MkActionNone = 0,
    MkActionFKey = 1,            // the plain F-key
    MkActionBrightnessDown = 2,  // app
    MkActionBrightnessUp = 3,    // app
    MkActionTaskView = 4,        // Win+Tab
    MkActionSearch = 5,          // Win+S
    MkActionAppSwitcher = 6,     // Alt+Tab
    MkActionDictation = 7,       // Win+H
    MkActionSleep = 8,           // app
    MkActionPrevTrack = 9,       // app
    MkActionRewind = 10,         // app
    MkActionPlayPause = 11,      // app
    MkActionStop = 12,           // app
    MkActionNextTrack = 13,      // app
    MkActionFastForward = 14,    // app
    MkActionMute = 15,           // app
    MkActionVolumeDown = 16,     // app
    MkActionVolumeUp = 17,       // app
    MkActionPrintScreen = 18,
    MkActionScrollLock = 19,
    MkActionPauseBreak = 20,
    MkActionLaunchExplorer = 21, // app
    MkActionLaunchCalc = 22,     // app
    MkActionLaunchNotepad = 23,  // app
    MkActionLaunchTaskMgr = 24,  // app
    MkActionLaunchCustom = 25,   // app
    MkActionMissionControl = 26, // Win+Tab
    MkActionLock = 27,           // Win+L
    MkActionCount
} MK_ACTION;

// Targets for a physical modifier. 0xE0..0xE7 are the HID modifier usages.
typedef enum _MK_MOD_TARGET {
    MkModLeftControl = 0xE0, MkModLeftShift = 0xE1, MkModLeftAlt = 0xE2, MkModLeftGui = 0xE3,
    MkModRightControl = 0xE4, MkModRightShift = 0xE5, MkModRightAlt = 0xE6, MkModRightGui = 0xE7,
    MkModDisabled = 0xF0, MkModCapsLock = 0xF1, MkModFn = 0xF2, MkModMenu = 0xF3
} MK_MOD_TARGET;

// Index into MK_CONFIG.Modifiers. The first eight follow the HID modifier byte order.
typedef enum _MK_MOD_SLOT {
    MkSlotLeftControl = 0, MkSlotLeftShift, MkSlotLeftAlt, MkSlotLeftGui,
    MkSlotRightControl, MkSlotRightShift, MkSlotRightAlt, MkSlotRightGui,
    MkSlotCapsLock, MkSlotFn, MkSlotMenu, MkSlotCount
} MK_MOD_SLOT;

typedef enum _MK_CLEAR_MODE { MkClearNumLock = 0, MkClearDelete = 1, MkClearBackspace = 2, MkClearOff = 3 } MK_CLEAR_MODE;
typedef enum _MK_TRANSPORT { MkTransportUnknown = 0, MkTransportBluetooth = 1, MkTransportUsb = 2 } MK_TRANSPORT;
typedef enum _MK_EVENT_TYPE { MkEventAction = 1, MkEventRaw = 2 } MK_EVENT_TYPE;

#pragma pack(push, 1)
typedef struct _MK_FKEY_SLOT {
    UCHAR Action;       // MK_ACTION
    UCHAR CustomSlot;   // for MkActionLaunchCustom
} MK_FKEY_SLOT;

typedef struct _MK_CONFIG {
    ULONG Magic;
    ULONG Version;
    ULONG Size;
    UCHAR Enabled;
    UCHAR Preset;          // informational (Windows / macOS / Apple / Custom)
    UCHAR FnLock;          // 1: top row is media by default, Fn gives F-keys
    UCHAR SleepShortcut;   // 1: Fn+Space sleeps
    UCHAR ClearMode;       // MK_CLEAR_MODE (numeric keypad models)
    UCHAR EjectMode;       // reserved: no supported keyboard has an Eject key
    UCHAR SwapCmdOption;
    UCHAR ThirdCharMod;    // MK_MOD_TARGET of the physical key that acts as AltGr, or MkModDisabled
    MK_FKEY_SLOT FKeys[MK_FKEY_COUNT];
    UCHAR Modifiers[MkSlotCount];  // MK_MOD_TARGET per MK_MOD_SLOT
    UCHAR TouchIdAction;   // MK_ACTION for the Touch ID / lock key (consumer 0x40 bit)
    UCHAR Reserved[15];
} MK_CONFIG;

typedef struct _MKF_DEVICE_INFO {
    ULONG Index;
    USHORT ProductId;
    UCHAR Transport;        // MK_TRANSPORT
    UCHAR LayoutKnown;      // 1 once a keyboard report in the expected layout was seen
    ULONG InputReports;     // all input reports
    ULONG KeyboardReports;  // report 0x01 in the expected layout
    ULONG LastRawLength;
    UCHAR LastRaw[16];      // last input report (report ID first, BT header stripped)
    WCHAR InstanceId[128];
} MKF_DEVICE_INFO;

typedef struct _MKF_STATUS {
    ULONG ProtocolVersion;
    ULONG DeviceCount;
    ULONG Subscribers;      // handles subscribed to actions
    ULONG ConfigLoaded;     // 1 when a valid config is active (pushed or loaded from the registry)
    MKF_DEVICE_INFO Devices[MKF_MAX_DEVICES];
    // v4: generation of the control device this request was sent to. The control device is re-created
    // when the last keyboard goes away and comes back (e.g. after sleep); a handle whose generation differs
    // from a freshly opened handle's is stale and must be reopened.
    ULONG ControlGeneration;
} MKF_STATUS;

typedef struct _MKF_EVENT {
    ULONG Type;             // MK_EVENT_TYPE
    ULONG DeviceIndex;
    ULONG Action;           // MK_ACTION (MkEventAction)
    ULONG Down;             // 1 press, 0 release
    ULONG CustomSlot;
    LONGLONG Timestamp;     // KeQueryInterruptTime, 100ns
    ULONG Length;           // MkEventRaw: bytes in Data
    UCHAR Data[MKF_EVENT_DATA];
} MKF_EVENT;
#pragma pack(pop)
