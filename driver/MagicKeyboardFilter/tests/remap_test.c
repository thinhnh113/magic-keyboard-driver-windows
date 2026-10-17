// Host unit tests for Remap.c. Build/run: driver\MagicKeyboardFilter\tests\run-tests.cmd
#include <windows.h>
#include <winioctl.h>
#include <stdio.h>
#include <string.h>
#include "../Remap.h"

static int g_fail, g_pass;
#define CHECK(c, msg) do { if (c) g_pass++; else { g_fail++; printf("FAIL %s:%d %s\n", __FUNCTION__, __LINE__, msg); } } while (0)

static MK_CONFIG DefaultConfig(void)
{
    static const UCHAR fk[MK_FKEY_COUNT] = {
        MkActionBrightnessDown, MkActionBrightnessUp, MkActionTaskView, MkActionSearch, MkActionDictation,
        MkActionSleep, MkActionPrevTrack, MkActionPlayPause, MkActionNextTrack, MkActionMute,
        MkActionVolumeDown, MkActionVolumeUp, MkActionPrintScreen, MkActionScrollLock, MkActionPauseBreak,
        MkActionLaunchExplorer, MkActionLaunchCalc, MkActionLaunchNotepad, MkActionLaunchTaskMgr };
    MK_CONFIG c; int i;
    memset(&c, 0, sizeof(c));
    c.Magic = MK_CONFIG_MAGIC; c.Version = MK_CONFIG_VERSION; c.Size = sizeof(c);
    c.Enabled = 1; c.FnLock = 1; c.SleepShortcut = 1; c.ThirdCharMod = MkModDisabled;
    for (i = 0; i < MK_FKEY_COUNT; i++) c.FKeys[i].Action = fk[i];
    for (i = 0; i < 8; i++) c.Modifiers[i] = (UCHAR)(MkModLeftControl + i);
    c.Modifiers[MkSlotCapsLock] = MkModCapsLock;
    c.Modifiers[MkSlotFn] = MkModFn;
    c.Modifiers[MkSlotMenu] = MkModMenu;
    return c;
}

typedef struct { MK_REMAP_CONFIG cfg; MK_KEY_STATE st; MK_ACTION_EVENT ev[MK_MAX_EVENTS]; int n; UCHAR r[10]; } T;

static void Setup(T* t, const MK_CONFIG* c) { memset(t, 0, sizeof(*t)); MkCompileConfig(&t->cfg, c); }

// Send one physical report: modifiers, fn, up to 2 keys.
static void Send(T* t, UCHAR mods, int fn, UCHAR k1, UCHAR k2, BOOLEAN app)
{
    memset(t->r, 0, sizeof(t->r));
    t->r[0] = 0x01; t->r[1] = mods; t->r[3] = k1; t->r[4] = k2;
    if (fn) t->r[9] |= MK_FN_BIT;
    MkProcessReport(&t->cfg, &t->st, t->r, sizeof(t->r), app, t->ev, &t->n);
}

static void FnArrowIsHome(void)
{
    MK_CONFIG c = DefaultConfig(); T t; Setup(&t, &c);
    Send(&t, 0, 1, 0x50, 0, TRUE);
    CHECK(t.r[3] == 0x4A, "fn+Left -> Home");
    Send(&t, 0, 0, 0x50, 0, TRUE);        // fn released first, Left still held
    CHECK(t.r[3] == 0x4A, "mapping sticks after fn release");
    Send(&t, 0, 0, 0, 0, TRUE);
    CHECK(t.r[3] == 0, "released");
    Send(&t, 0, 0, 0x50, 0, TRUE);
    CHECK(t.r[3] == 0x50, "plain Left without fn");
}

static void MediaKeyWithApp(void)
{
    MK_CONFIG c = DefaultConfig(); T t; Setup(&t, &c);
    Send(&t, 0, 0, 0x40 /*F7*/, 0, TRUE);
    CHECK(t.r[3] == 0, "F7 swallowed in media mode");
    CHECK(t.n == 1 && t.ev[0].Action == MkActionPrevTrack && t.ev[0].Down == 1, "PrevTrack down event");
    Send(&t, 0, 0, 0, 0, TRUE);
    CHECK(t.n == 1 && t.ev[0].Action == MkActionPrevTrack && t.ev[0].Down == 0, "PrevTrack up event");
}

static void MediaKeyWithoutAppFallsBack(void)
{
    MK_CONFIG c = DefaultConfig(); T t; Setup(&t, &c);
    Send(&t, 0, 0, 0x40, 0, FALSE);
    CHECK(t.r[3] == 0x40 && t.n == 0, "no app: F7 passes through");
}

static void FnGivesFKeyUnderFnLock(void)
{
    MK_CONFIG c = DefaultConfig(); T t; Setup(&t, &c);
    Send(&t, 0, 1, 0x40, 0, TRUE);
    CHECK(t.r[3] == 0x40 && t.n == 0, "fn+F7 -> F7 with fn lock");
}

static void FnLockOff(void)
{
    MK_CONFIG c = DefaultConfig(); T t; c.FnLock = 0; Setup(&t, &c);
    Send(&t, 0, 0, 0x40, 0, TRUE);
    CHECK(t.r[3] == 0x40 && t.n == 0, "F7 plain when fn lock off");
    Send(&t, 0, 0, 0, 0, TRUE);
    Send(&t, 0, 1, 0x40, 0, TRUE);
    CHECK(t.n == 1 && t.ev[0].Action == MkActionPrevTrack, "fn+F7 -> media when fn lock off");
}

static void TaskViewChord(void)
{
    MK_CONFIG c = DefaultConfig(); T t; Setup(&t, &c);
    Send(&t, 0, 0, 0x3C /*F3*/, 0, TRUE);
    CHECK(t.r[3] == 0x2B && (t.r[1] & 0x08), "F3 -> Win+Tab");
    Send(&t, 0, 0, 0, 0, TRUE);
    CHECK(t.r[1] == 0 && t.r[3] == 0, "chord released");
}

static void MacPreset(void)
{
    MK_CONFIG c = DefaultConfig(); T t;
    c.Modifiers[MkSlotLeftGui] = MkModLeftControl;   // Command -> Ctrl
    c.Modifiers[MkSlotLeftControl] = MkModLeftGui;   // Control -> Win
    Setup(&t, &c);
    Send(&t, 0x08 /*LGui*/, 0, 0x06 /*C*/, 0, TRUE);
    CHECK(t.r[1] == 0x01 && t.r[3] == 0x06, "Cmd+C -> Ctrl+C");
}

static void SwapCmdOption(void)
{
    MK_CONFIG c = DefaultConfig(); T t; c.SwapCmdOption = 1; Setup(&t, &c);
    Send(&t, 0x08 /*LGui*/, 0, 0, 0, TRUE);
    CHECK(t.r[1] == 0x04, "Cmd -> Alt when swapped");
}

static void CapsAsControl(void)
{
    MK_CONFIG c = DefaultConfig(); T t; c.Modifiers[MkSlotCapsLock] = MkModLeftControl; Setup(&t, &c);
    Send(&t, 0, 0, 0x39, 0x04 /*A*/, TRUE);
    CHECK(t.r[1] == 0x01 && t.r[3] == 0x04 && t.r[4] == 0, "Caps+A -> Ctrl+A");
}

static void SleepShortcut(void)
{
    MK_CONFIG c = DefaultConfig(); T t; Setup(&t, &c);
    Send(&t, 0, 1, 0x2C, 0, TRUE);
    CHECK(t.r[3] == 0 && t.n == 1 && t.ev[0].Action == MkActionSleep, "fn+Space -> sleep action");
}

static void DisabledPassesThrough(void)
{
    MK_CONFIG c = DefaultConfig(); T t; c.Enabled = 0; Setup(&t, &c);
    Send(&t, 0x08, 1, 0x40, 0x50, TRUE);
    CHECK(t.r[1] == 0x08 && t.r[3] == 0x40 && t.r[4] == 0x50 && t.n == 0, "disabled: untouched");
}

static void OtherReportsUntouched(void)
{
    MK_CONFIG c = DefaultConfig(); T t; UCHAR bat[3] = { 0x90, 0x02, 0x55 }; BOOLEAN ok;
    Setup(&t, &c);
    ok = MkProcessReport(&t.cfg, &t.st, bat, sizeof(bat), TRUE, t.ev, &t.n);
    CHECK(!ok && bat[0] == 0x90 && bat[2] == 0x55, "battery report ignored");
}

static void InvalidConfigDisables(void)
{
    MK_CONFIG c = DefaultConfig(); T t; c.Version = 99; Setup(&t, &c);
    Send(&t, 0, 1, 0x50, 0, TRUE);
    CHECK(t.r[3] == 0x50, "bad config version passes through");
}

int main(void)
{
    FnArrowIsHome(); MediaKeyWithApp(); MediaKeyWithoutAppFallsBack(); FnGivesFKeyUnderFnLock();
    FnLockOff(); TaskViewChord(); MacPreset(); SwapCmdOption(); CapsAsControl(); SleepShortcut();
    DisabledPassesThrough(); OtherReportsUntouched(); InvalidConfigDisables();
    printf("%d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
