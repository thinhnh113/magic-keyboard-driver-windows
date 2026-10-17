// Remap engine. See Remap.h for the report layout and contract.
#ifdef _KERNEL_MODE
#include <ntddk.h>
#else
#include <windows.h>
#include <winioctl.h>
#endif
#include "Remap.h"

// HID keyboard page usages
#define K_P          0x13
#define K_H          0x0B
#define K_L          0x0F
#define K_S          0x16
#define K_RETURN     0x28
#define K_ESC        0x29
#define K_BACKSPACE  0x2A
#define K_TAB        0x2B
#define K_SPACE      0x2C
#define K_CAPSLOCK   0x39
#define K_F1         0x3A
#define K_F12        0x45
#define K_PRINTSCR   0x46
#define K_SCROLLLOCK 0x47
#define K_PAUSE      0x48
#define K_INSERT     0x49
#define K_HOME       0x4A
#define K_PAGEUP     0x4B
#define K_DELETE     0x4C
#define K_END        0x4D
#define K_PAGEDOWN   0x4E
#define K_RIGHT      0x4F
#define K_LEFT       0x50
#define K_DOWN       0x51
#define K_UP         0x52
#define K_KPCLEAR    0x53
#define K_MENU       0x65
#define K_F13        0x68
#define K_F19        0x6E

#define M_LCTRL 0x01
#define M_LALT  0x04
#define M_LGUI  0x08

static void Zero(void* p, unsigned long n) { unsigned char* b = (unsigned char*)p; while (n--) *b++ = 0; }

static BOOLEAN IsDown(const MK_KEY_STATE* st, UCHAR u) { return (st->Down[u >> 3] >> (u & 7)) & 1; }
static void SetDown(MK_KEY_STATE* st, UCHAR u, BOOLEAN d)
{
    if (d) st->Down[u >> 3] |= (UCHAR)(1u << (u & 7));
    else   st->Down[u >> 3] &= (UCHAR)~(1u << (u & 7));
}

static int FKeyIndex(UCHAR u)
{
    if (u >= K_F1 && u <= K_F12) return u - K_F1;
    if (u >= K_F13 && u <= K_F19) return 12 + (u - K_F13);
    return -1;
}

static BOOLEAN IsModTarget(UCHAR t) { return t >= MkModLeftControl && t <= MkModRightGui; }
static UCHAR ModBit(UCHAR t) { return (UCHAR)(1u << (t - MkModLeftControl)); }

static BOOLEAN IsAppAction(UCHAR a)
{
    switch (a) {
    case MkActionBrightnessDown: case MkActionBrightnessUp: case MkActionSleep:
    case MkActionPrevTrack: case MkActionRewind: case MkActionPlayPause: case MkActionStop:
    case MkActionNextTrack: case MkActionFastForward: case MkActionMute:
    case MkActionVolumeDown: case MkActionVolumeUp:
    case MkActionLaunchExplorer: case MkActionLaunchCalc: case MkActionLaunchNotepad:
    case MkActionLaunchTaskMgr: case MkActionLaunchCustom:
        return TRUE;
    default:
        return FALSE;
    }
}

BOOLEAN MkConfigValid(const MK_CONFIG* in)
{
    return in && in->Magic == MK_CONFIG_MAGIC && in->Version == MK_CONFIG_VERSION &&
           in->Size == sizeof(MK_CONFIG);
}

void MkResetKeyState(MK_KEY_STATE* st) { Zero(st, sizeof(*st)); }

void MkCompileConfig(MK_REMAP_CONFIG* out, const MK_CONFIG* in)
{
    int i;
    Zero(out, sizeof(*out));
    if (!MkConfigValid(in)) return;   // Enabled stays FALSE: pass everything through

    out->Enabled = in->Enabled ? TRUE : FALSE;
    out->FnLock = in->FnLock ? TRUE : FALSE;
    out->SleepShortcut = in->SleepShortcut ? TRUE : FALSE;
    out->ClearMode = in->ClearMode;
    for (i = 0; i < 8; i++) out->ModMap[i] = in->Modifiers[i];
    out->CapsTarget = in->Modifiers[MkSlotCapsLock];
    out->FnTarget = in->Modifiers[MkSlotFn];
    out->MenuTarget = in->Modifiers[MkSlotMenu];

    if (in->SwapCmdOption) {
        UCHAR t;
        t = out->ModMap[MkSlotLeftAlt];  out->ModMap[MkSlotLeftAlt] = out->ModMap[MkSlotLeftGui];   out->ModMap[MkSlotLeftGui] = t;
        t = out->ModMap[MkSlotRightAlt]; out->ModMap[MkSlotRightAlt] = out->ModMap[MkSlotRightGui]; out->ModMap[MkSlotRightGui] = t;
    }
    // The chosen physical right-hand key becomes AltGr (Right Alt).
    if (in->ThirdCharMod == MkModRightGui) out->ModMap[MkSlotRightGui] = MkModRightAlt;
    else if (in->ThirdCharMod == MkModRightAlt) out->ModMap[MkSlotRightAlt] = MkModRightAlt;
    else if (in->ThirdCharMod == MkModRightControl) out->ModMap[MkSlotRightControl] = MkModRightAlt;

    for (i = 0; i < MK_FKEY_COUNT; i++) {
        out->FAction[i] = in->FKeys[i].Action;
        out->FCustom[i] = in->FKeys[i].CustomSlot;
    }
    out->TouchIdAction = in->TouchIdAction;
}

// Decide what a newly pressed action key produces: a keyboard usage (+chord modifiers) or an app action.
static void ResolveAction(UCHAR action, UCHAR plainKey, BOOLEAN appConnected,
                          UCHAR* out, UCHAR* mods, UCHAR* app)
{
    if (IsAppAction(action)) {
        if (appConnected) *app = action;
        else *out = plainKey;   // no app listening: keep the key useful
        return;
    }
    switch (action) {
    case MkActionFKey:           *out = plainKey; break;
    case MkActionPrintScreen:    *out = K_PRINTSCR; break;
    case MkActionScrollLock:     *out = K_SCROLLLOCK; break;
    case MkActionPauseBreak:     *out = K_PAUSE; break;
    case MkActionTaskView:
    case MkActionMissionControl: *out = K_TAB; *mods = M_LGUI; break;
    case MkActionSearch:         *out = K_S;   *mods = M_LGUI; break;
    case MkActionDictation:      *out = K_H;   *mods = M_LGUI; break;
    case MkActionAppSwitcher:    *out = K_TAB; *mods = M_LALT; break;
    case MkActionLock:           *out = K_L;   *mods = M_LGUI; break;
    default: break;              // MkActionNone: swallow
    }
}

static UCHAR FnCombo(UCHAR k)
{
    switch (k) {
    case K_RETURN:    return K_INSERT;
    case K_BACKSPACE: return K_DELETE;
    case K_UP:        return K_PAGEUP;
    case K_DOWN:      return K_PAGEDOWN;
    case K_LEFT:      return K_HOME;
    case K_RIGHT:     return K_END;
    case K_ESC:       return K_PAUSE;
    case K_P:         return K_PRINTSCR;
    default:          return 0;
    }
}

// Apply a modifier-style target (used for real modifiers, Caps Lock, Fn and Menu keys).
static void ApplyTarget(UCHAR target, UCHAR* mods, BOOLEAN* fn, UCHAR* extra, int* extraCount)
{
    if (IsModTarget(target)) *mods |= ModBit(target);
    else if (target == MkModFn) *fn = TRUE;
    else if (target == MkModCapsLock && *extraCount < MK_MAX_KEYS) extra[(*extraCount)++] = K_CAPSLOCK;
    else if (target == MkModMenu && *extraCount < MK_MAX_KEYS) extra[(*extraCount)++] = K_MENU;
}

static void PushEvent(MK_ACTION_EVENT* ev, int* n, UCHAR action, UCHAR down, UCHAR custom)
{
    if (*n >= MK_MAX_EVENTS) return;
    ev[*n].Action = action; ev[*n].Down = down; ev[*n].CustomSlot = custom;
    (*n)++;
}

BOOLEAN MkProcessReport(const MK_REMAP_CONFIG* cfg, MK_KEY_STATE* st, unsigned char* r,
                        unsigned long len, BOOLEAN appConnected,
                        MK_ACTION_EVENT* events, int* eventCount)
{
    UCHAR phys[MK_MAX_KEYS + 1];
    int physCount = 0, i, u;
    UCHAR outMods = 0, extra[MK_MAX_KEYS], outKeys[MK_MAX_KEYS];
    int extraCount = 0, outCount = 0;
    BOOLEAN fn = FALSE, media;
    UCHAR nowDown[256 / 8];

    *eventCount = 0;
    if (len < MK_REPORT_MIN_LEN || r[0] != MK_REPORT_ID) return FALSE;
    for (i = 0; i < MK_MAX_KEYS; i++) if (r[3 + i] == 0x01) return TRUE;  // rollover: leave as is

    if (!cfg->Enabled) { MkResetKeyState(st); return TRUE; }

    // 1) Modifiers and layer keys (all of them, so Fn is known before keys are resolved).
    for (i = 0; i < 8; i++)
        if (r[1] & (1u << i)) ApplyTarget(cfg->ModMap[i], &outMods, &fn, extra, &extraCount);
    if (r[9] & MK_FN_BIT) ApplyTarget(cfg->FnTarget, &outMods, &fn, extra, &extraCount);

    for (i = 0; i < MK_MAX_KEYS; i++) {
        UCHAR k = r[3 + i];
        if (k < 4) continue;
        if (k == K_CAPSLOCK && cfg->CapsTarget != MkModCapsLock) {
            ApplyTarget(cfg->CapsTarget, &outMods, &fn, extra, &extraCount);
            continue;
        }
        if (k == K_MENU && cfg->MenuTarget != MkModMenu) {
            ApplyTarget(cfg->MenuTarget, &outMods, &fn, extra, &extraCount);
            continue;
        }
        phys[physCount++] = k;
    }
    if ((r[9] & MK_TOUCHID_BIT) && cfg->TouchIdAction != MkActionNone)
        phys[physCount++] = MK_USAGE_TOUCHID;

    media = (cfg->FnLock ^ fn) ? TRUE : FALSE;

    // 2) Newly pressed keys get a mapping that sticks until they are released.
    Zero(nowDown, sizeof(nowDown));
    for (i = 0; i < physCount; i++) {
        UCHAR k = phys[i];
        nowDown[k >> 3] |= (UCHAR)(1u << (k & 7));
        if (IsDown(st, k)) continue;

        st->Out[k] = 0; st->OutMods[k] = 0; st->Action[k] = MkActionNone; st->Custom[k] = 0;
        if (k == MK_USAGE_TOUCHID) {
            ResolveAction(cfg->TouchIdAction, 0, appConnected, &st->Out[k], &st->OutMods[k], &st->Action[k]);
        } else {
            int fi = FKeyIndex(k);
            if (fi >= 0) {
                if (media) {
                    ResolveAction(cfg->FAction[fi], k, appConnected, &st->Out[k], &st->OutMods[k], &st->Action[k]);
                    st->Custom[k] = cfg->FCustom[fi];
                } else {
                    st->Out[k] = k;
                }
            } else if (fn && k == K_SPACE && cfg->SleepShortcut && appConnected) {
                st->Action[k] = MkActionSleep;
            } else if (fn && FnCombo(k)) {
                st->Out[k] = FnCombo(k);
            } else if (k == K_KPCLEAR) {
                st->Out[k] = cfg->ClearMode == MkClearDelete ? K_DELETE
                           : cfg->ClearMode == MkClearBackspace ? K_BACKSPACE
                           : cfg->ClearMode == MkClearOff ? 0 : K_KPCLEAR;
            } else {
                st->Out[k] = k;
            }
        }
        if (st->Action[k] != MkActionNone) PushEvent(events, eventCount, st->Action[k], 1, st->Custom[k]);
    }

    // 3) Released keys end their action.
    for (u = 0; u < 256; u++) {
        UCHAR k = (UCHAR)u;
        if (IsDown(st, k) && !((nowDown[k >> 3] >> (k & 7)) & 1)) {
            if (st->Action[k] != MkActionNone) PushEvent(events, eventCount, st->Action[k], 0, st->Custom[k]);
            st->Out[k] = 0; st->OutMods[k] = 0; st->Action[k] = MkActionNone;
        }
    }
    for (i = 0; i < (int)sizeof(nowDown); i++) st->Down[i] = nowDown[i];

    // 4) Rebuild the report from the held keys' mappings.
    for (i = 0; i < physCount; i++) {
        UCHAR k = phys[i];
        outMods |= st->OutMods[k];
        if (st->Out[k] && outCount < MK_MAX_KEYS) outKeys[outCount++] = st->Out[k];
    }
    for (i = 0; i < extraCount && outCount < MK_MAX_KEYS; i++) outKeys[outCount++] = extra[i];

    r[1] = outMods;
    for (i = 0; i < MK_MAX_KEYS; i++) r[3 + i] = (UCHAR)(i < outCount ? outKeys[i] : 0);
    return TRUE;
}
