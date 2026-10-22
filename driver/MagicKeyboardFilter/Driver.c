// MagicKeyboardFilter: KMDF lower filter for Apple Magic Keyboards (BT: below hidbth, USB: below hidusb).
//
// Responsibilities
//  * Rewrite each keyboard input report in place with the remap engine (Remap.c): modifier remapping,
//    Fn layer, missing keys, chords. This works without the app, from boot, using the config persisted
//    in HKLM\SYSTEM\CurrentControlSet\Services\MagicKeyboardFilter\Parameters\Config.
//  * Hand key actions that need user mode (media, volume, brightness, sleep, launchers) to the tray app
//    through a control device (\\.\MagicKeyboardFilter). With no app subscribed those keys fall back to
//    their plain F-key so nothing goes dead.
//
// Structure follows the MagicTouchFilter (Magic Mouse) driver in ..\..\..\magic_mouse.

#include <ntddk.h>
#include <wdf.h>
#include <initguid.h>
#include <devpkey.h>
#include <bthdef.h>
#include <bthioctl.h>
#include <bthddi.h>
#include <usb.h>
#include <usbioctl.h>
#include <ntstrsafe.h>
#include "Public.h"
#include "Remap.h"

#define MKF_TAG 'fKgM'
#define MKF_RING_SIZE 64
#define MKF_STALE_ACTION_100NS (2LL * 10 * 1000 * 1000)   // 2 s
#define HIDP_DATA_INPUT 0xA1   // BT HIDP: DATA | Input

typedef struct _FILTER_CONTEXT {
    WDFDEVICE Device;
    ULONG Index;
    MK_TRANSPORT Transport;
    USHORT ProductId;
    WCHAR InstanceId[128];

    KSPIN_LOCK StateLock;      // guards Keys (completions can run concurrently)
    MK_KEY_STATE Keys;

    volatile LONG InputReports;
    volatile LONG KeyboardReports;
    UCHAR LastRaw[16];
    ULONG LastRawLength;
} FILTER_CONTEXT, *PFILTER_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(FILTER_CONTEXT, FilterGetContext)

typedef struct _FILE_CONTEXT {
    ULONG Flags;               // MKF_SUB_*
} FILE_CONTEXT, *PFILE_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(FILE_CONTEXT, FileGetContext)

typedef struct _CONTROL_CONTEXT {
    ULONG Generation;          // MKF_STATUS.ControlGeneration
} CONTROL_CONTEXT, *PCONTROL_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(CONTROL_CONTEXT, ControlGetContext)

// ---- globals (g_Lock: spin lock for ring, devices, config snapshot; g_ControlLock: PASSIVE lifetime) ----
static KSPIN_LOCK g_Lock;
static WDFWAITLOCK g_ControlLock;
static WDFDEVICE g_ControlDevice;
static WDFQUEUE g_PendingReads;
static PFILTER_CONTEXT g_Devices[MKF_MAX_DEVICES];
static MKF_EVENT g_Ring[MKF_RING_SIZE];
static ULONG g_RingHead, g_RingCount;
static MK_CONFIG g_Config;
static MK_REMAP_CONFIG g_Compiled;
static BOOLEAN g_ConfigLoaded;
static volatile LONG g_Subscribers;
static volatile LONG g_RawSubscribers;
static ULONG g_Generation;          // bumped for every control device (g_ControlLock)

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD EvtDeviceAdd;
EVT_WDF_DEVICE_D0_ENTRY EvtD0Entry;
EVT_WDF_OBJECT_CONTEXT_CLEANUP EvtControlCleanup;
EVT_WDF_OBJECT_CONTEXT_CLEANUP EvtFilterCleanup;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL EvtInternalIoctl;
EVT_WDF_REQUEST_COMPLETION_ROUTINE BrbCompletion;
EVT_WDF_REQUEST_COMPLETION_ROUTINE UrbCompletion;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL EvtControlIoctl;
EVT_WDF_FILE_CLEANUP EvtControlFileCleanup;

// ------------------------------------------------------------------ helpers

static BOOLEAN WcsContainsI(PCWSTR Hay, PCWSTR Needle)
{
    for (; *Hay; Hay++) {
        PCWSTR h = Hay, n = Needle;
        while (*h && *n && RtlUpcaseUnicodeChar(*h) == RtlUpcaseUnicodeChar(*n)) { h++; n++; }
        if (!*n) return TRUE;
    }
    return FALSE;
}

static USHORT HexValue(PCWSTR p, int digits)
{
    USHORT v = 0;
    for (; digits > 0 && *p; digits--, p++) {
        WCHAR c = *p;
        v <<= 4;
        if (c >= L'0' && c <= L'9') v |= (USHORT)(c - L'0');
        else if (c >= L'a' && c <= L'f') v |= (USHORT)(c - L'a' + 10);
        else if (c >= L'A' && c <= L'F') v |= (USHORT)(c - L'A' + 10);
        else return 0;
    }
    return v;
}

static VOID DetectDevice(PFILTER_CONTEXT Ctx)
{
    PCWSTR id = Ctx->InstanceId, p;
    Ctx->Transport = WcsContainsI(id, L"BTHENUM") ? MkTransportBluetooth
                   : WcsContainsI(id, L"USB\\") ? MkTransportUsb : MkTransportUnknown;
    // "PID&0321" (BTHENUM) or "PID_0321" (USB)
    for (p = id; *p; p++) {
        if ((p[0] == L'P' || p[0] == L'p') && (p[1] == L'I' || p[1] == L'i') && (p[2] == L'D' || p[2] == L'd') &&
            (p[3] == L'&' || p[3] == L'_')) {
            Ctx->ProductId = HexValue(p + 4, 4);
            break;
        }
    }
}

static PUCHAR MapBuffer(PVOID Buffer, PMDL Mdl)
{
    if (Buffer) return (PUCHAR)Buffer;
    if (Mdl) return (PUCHAR)MmGetSystemAddressForMdlSafe(Mdl, NormalPagePriority | MdlMappingNoExecute);
    return NULL;
}

// Queue an event and satisfy a pending read if there is one. Any IRQL <= DISPATCH.
static VOID PushEvent(const MKF_EVENT* Ev)
{
    KIRQL irql;
    WDFREQUEST req = NULL;

    KeAcquireSpinLock(&g_Lock, &irql);
    if (g_PendingReads && NT_SUCCESS(WdfIoQueueRetrieveNextRequest(g_PendingReads, &req))) {
        KeReleaseSpinLock(&g_Lock, irql);
        MKF_EVENT* out;
        if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(req, sizeof(MKF_EVENT), (PVOID*)&out, NULL))) {
            *out = *Ev;
            WdfRequestCompleteWithInformation(req, STATUS_SUCCESS, sizeof(MKF_EVENT));
        } else {
            WdfRequestComplete(req, STATUS_BUFFER_TOO_SMALL);
        }
        return;
    }
    {
        MKF_EVENT* slot = &g_Ring[(g_RingHead + g_RingCount) % MKF_RING_SIZE];
        if (g_RingCount == MKF_RING_SIZE) g_RingHead = (g_RingHead + 1) % MKF_RING_SIZE; // drop oldest
        else g_RingCount++;
        *slot = *Ev;
    }
    KeReleaseSpinLock(&g_Lock, irql);
}

// Handle one HID input report in place (Report[0] = report ID). Length is unchanged.
static VOID ProcessInputReport(PFILTER_CONTEXT Ctx, PUCHAR Report, ULONG Length)
{
    MK_REMAP_CONFIG cfg;
    MK_ACTION_EVENT events[MK_MAX_EVENTS];
    int count = 0, i;
    BOOLEAN app, raw, known;
    KIRQL irql;
    MKF_EVENT ev;

    if (Length == 0) return;
    InterlockedIncrement(&Ctx->InputReports);
    Ctx->LastRawLength = Length;
    RtlCopyMemory(Ctx->LastRaw, Report, min(Length, sizeof(Ctx->LastRaw)));

    KeAcquireSpinLock(&g_Lock, &irql);
    cfg = g_Compiled;
    KeReleaseSpinLock(&g_Lock, irql);
    app = g_Subscribers > 0;
    raw = g_RawSubscribers > 0;

    if (raw) {
        RtlZeroMemory(&ev, sizeof(ev));
        ev.Type = MkEventRaw;
        ev.DeviceIndex = Ctx->Index;
        ev.Timestamp = (LONGLONG)KeQueryInterruptTime();
        ev.Length = min(Length, (ULONG)MKF_EVENT_DATA);
        RtlCopyMemory(ev.Data, Report, ev.Length);
        PushEvent(&ev);
    }

    KeAcquireSpinLock(&Ctx->StateLock, &irql);
    known = MkProcessReport(&cfg, &Ctx->Keys, Report, Length, app, events, &count);
    KeReleaseSpinLock(&Ctx->StateLock, irql);
    if (!known) return;
    InterlockedIncrement(&Ctx->KeyboardReports);

    for (i = 0; i < count; i++) {
        RtlZeroMemory(&ev, sizeof(ev));
        ev.Type = MkEventAction;
        ev.DeviceIndex = Ctx->Index;
        ev.Action = events[i].Action;
        ev.Down = events[i].Down;
        ev.CustomSlot = events[i].CustomSlot;
        ev.Timestamp = (LONGLONG)KeQueryInterruptTime();
        PushEvent(&ev);
    }
}

static VOID ResetKeys(PFILTER_CONTEXT Ctx)
{
    KIRQL irql;
    KeAcquireSpinLock(&Ctx->StateLock, &irql);
    MkResetKeyState(&Ctx->Keys);
    KeReleaseSpinLock(&Ctx->StateLock, irql);
}

// ------------------------------------------------------------------ config persistence (PASSIVE)

DECLARE_CONST_UNICODE_STRING(g_ConfigValue, L"Config");

static VOID ApplyConfig(const MK_CONFIG* Cfg)
{
    MK_REMAP_CONFIG compiled;
    KIRQL irql;
    MkCompileConfig(&compiled, Cfg);
    KeAcquireSpinLock(&g_Lock, &irql);
    g_Config = *Cfg;
    g_Compiled = compiled;
    g_ConfigLoaded = TRUE;
    KeReleaseSpinLock(&g_Lock, irql);
}

static VOID LoadConfig(WDFDRIVER Driver)
{
    WDFKEY key;
    MK_CONFIG cfg;
    ULONG len = 0, type = 0;
    if (!NT_SUCCESS(WdfDriverOpenParametersRegistryKey(Driver, KEY_READ, WDF_NO_OBJECT_ATTRIBUTES, &key))) return;
    if (NT_SUCCESS(WdfRegistryQueryValue(key, &g_ConfigValue, sizeof(cfg), &cfg, &len, &type)) &&
        type == REG_BINARY && len == sizeof(cfg) && MkConfigValid(&cfg)) {
        ApplyConfig(&cfg);
    }
    WdfRegistryClose(key);
}

static VOID SaveConfig(const MK_CONFIG* Cfg)
{
    WDFKEY key;
    if (!NT_SUCCESS(WdfDriverOpenParametersRegistryKey(WdfGetDriver(), KEY_WRITE, WDF_NO_OBJECT_ATTRIBUTES, &key)))
        return;
    WdfRegistryAssignValue(key, &g_ConfigValue, REG_BINARY, sizeof(*Cfg), (PVOID)Cfg);
    WdfRegistryClose(key);
}

// ------------------------------------------------------------------ driver / device setup

NTSTATUS DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDFDRIVER driver;
    NTSTATUS status;

    KeInitializeSpinLock(&g_Lock);
    RtlZeroMemory(&g_Compiled, sizeof(g_Compiled));   // disabled until a config is loaded
    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    config.DriverPoolTag = MKF_TAG;
    status = WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, &driver);
    if (NT_SUCCESS(status)) status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &g_ControlLock);
    if (NT_SUCCESS(status)) LoadConfig(driver);
    return status;
}

// Runs whenever the control device object is destroyed, however that happens.
VOID EvtControlCleanup(WDFOBJECT Object)
{
    KIRQL irql;
    KeAcquireSpinLock(&g_Lock, &irql);
    if (g_ControlDevice == (WDFDEVICE)Object) {
        g_ControlDevice = NULL;
        g_PendingReads = NULL;
        g_RingCount = 0;
        g_Subscribers = 0;      // those subscriptions were on this control device
        g_RawSubscribers = 0;
    }
    KeReleaseSpinLock(&g_Lock, irql);
}

// Lifetime of the control device
// ------------------------------
// It is deleted when the last keyboard goes away (so the driver can unload and be updated) and created
// again when one comes back, e.g. after sleep re-enumerates the Bluetooth keyboard. An app may still hold
// a handle to the old one, which keeps that object alive. Three rules keep this safe:
//  * Each control device gets a unique NT name (...FilterN), so a lingering old one never blocks the new one.
//  * The \\.\MagicKeyboardFilter link is ours (IoCreateSymbolicLink), not WdfDeviceCreateSymbolicLink: the
//    framework deletes a device's link by name when the device is finally destroyed, which for an old control
//    device kept alive by a handle would remove the link of its replacement.
//  * DeleteControlDevice completes the app's parked reads, so the app notices and reconnects.
static NTSTATUS CreateControlDevice(WDFDRIVER Driver)
{
    PWDFDEVICE_INIT init;
    WDFDEVICE dev = NULL;
    WDF_OBJECT_ATTRIBUTES ca, fa;
    WDF_IO_QUEUE_CONFIG qc;
    WDF_FILEOBJECT_CONFIG fc;
    WDFQUEUE q;
    NTSTATUS status;
    KIRQL irql;
    WCHAR nameBuf[64];
    UNICODE_STRING name;
    ULONG generation = ++g_Generation;   // caller holds g_ControlLock
    DECLARE_CONST_UNICODE_STRING(link, MKF_SYMLINK_NAME);
    // SYSTEM, admins and the interactive user (the tray app runs unelevated).
    DECLARE_CONST_UNICODE_STRING(sddl, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)");

    RtlInitEmptyUnicodeString(&name, nameBuf, sizeof(nameBuf));
    status = RtlUnicodeStringPrintf(&name, L"%ws%u", MKF_DEVICE_NAME, generation);
    if (!NT_SUCCESS(status)) return status;

    IoDeleteSymbolicLink((PUNICODE_STRING)&link);   // stale link from an earlier control device

    init = WdfControlDeviceInitAllocate(Driver, &sddl);
    if (!init) return STATUS_INSUFFICIENT_RESOURCES;
    WdfDeviceInitSetExclusive(init, FALSE);

    WDF_FILEOBJECT_CONFIG_INIT(&fc, WDF_NO_EVENT_CALLBACK, WDF_NO_EVENT_CALLBACK, EvtControlFileCleanup);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&fa, FILE_CONTEXT);
    WdfDeviceInitSetFileObjectConfig(init, &fc, &fa);

    status = WdfDeviceInitAssignName(init, &name);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&ca, CONTROL_CONTEXT);
    ca.EvtCleanupCallback = EvtControlCleanup;
    ca.ExecutionLevel = WdfExecutionLevelPassive;   // IOCTL handlers touch the registry
    if (NT_SUCCESS(status)) status = WdfDeviceCreate(&init, &ca, &dev);
    if (!NT_SUCCESS(status)) { if (init) WdfDeviceInitFree(init); return status; }
    ControlGetContext(dev)->Generation = generation;

    status = IoCreateSymbolicLink((PUNICODE_STRING)&link, &name);
    if (!NT_SUCCESS(status)) goto fail;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&qc, WdfIoQueueDispatchSequential);
    qc.EvtIoDeviceControl = EvtControlIoctl;
    status = WdfIoQueueCreate(dev, &qc, WDF_NO_OBJECT_ATTRIBUTES, &q);
    if (!NT_SUCCESS(status)) goto fail;

    WDF_IO_QUEUE_CONFIG_INIT(&qc, WdfIoQueueDispatchManual);
    status = WdfIoQueueCreate(dev, &qc, WDF_NO_OBJECT_ATTRIBUTES, &q);
    if (!NT_SUCCESS(status)) goto fail;

    KeAcquireSpinLock(&g_Lock, &irql);
    g_PendingReads = q;
    g_ControlDevice = dev;
    KeReleaseSpinLock(&g_Lock, irql);
    WdfControlFinishInitializing(dev);
    return STATUS_SUCCESS;
fail:
    WdfObjectDelete(dev);
    IoDeleteSymbolicLink((PUNICODE_STRING)&link);
    return status;
}

// Caller holds g_ControlLock.
static VOID EnsureControlDevice(VOID)
{
    if (!g_ControlDevice) CreateControlDevice(WdfGetDriver());
}

NTSTATUS EvtDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT DeviceInit)
{
    WDF_OBJECT_ATTRIBUTES attrs;
    WDF_PNPPOWER_EVENT_CALLBACKS pnp;
    WDF_IO_QUEUE_CONFIG qc;
    WDFDEVICE device;
    PFILTER_CONTEXT ctx;
    NTSTATUS status;
    ULONG i, size = 0;
    DEVPROPTYPE type;
    KIRQL irql;
    UNREFERENCED_PARAMETER(Driver);

    WdfFdoInitSetFilter(DeviceInit);

    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnp);
    pnp.EvtDeviceD0Entry = EvtD0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnp);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attrs, FILTER_CONTEXT);
    attrs.EvtCleanupCallback = EvtFilterCleanup;
    status = WdfDeviceCreate(&DeviceInit, &attrs, &device);
    if (!NT_SUCCESS(status)) return status;

    ctx = FilterGetContext(device);
    RtlZeroMemory(ctx, sizeof(*ctx));
    ctx->Device = device;
    ctx->Index = MAXULONG;
    KeInitializeSpinLock(&ctx->StateLock);
    {
        WDF_DEVICE_PROPERTY_DATA pd;
        WDF_DEVICE_PROPERTY_DATA_INIT(&pd, &DEVPKEY_Device_InstanceId);
        WdfDeviceQueryPropertyEx(device, &pd, sizeof(ctx->InstanceId) - sizeof(WCHAR),
                                 ctx->InstanceId, &size, &type);
    }
    DetectDevice(ctx);

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&qc, WdfIoQueueDispatchParallel);
    qc.EvtIoInternalDeviceControl = EvtInternalIoctl;
    status = WdfIoQueueCreate(device, &qc, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) return status;

    WdfWaitLockAcquire(g_ControlLock, NULL);
    KeAcquireSpinLock(&g_Lock, &irql);
    for (i = 0; i < MKF_MAX_DEVICES; i++) {
        if (!g_Devices[i]) { g_Devices[i] = ctx; ctx->Index = i; break; }
    }
    KeReleaseSpinLock(&g_Lock, irql);
    EnsureControlDevice();   // a missing control device only disables app actions; never fail the keyboard
    WdfWaitLockRelease(g_ControlLock);
    return STATUS_SUCCESS;
}

NTSTATUS EvtD0Entry(WDFDEVICE Device, WDF_POWER_DEVICE_STATE PreviousState)
{
    UNREFERENCED_PARAMETER(PreviousState);
    ResetKeys(FilterGetContext(Device));
    WdfWaitLockAcquire(g_ControlLock, NULL);
    EnsureControlDevice();
    WdfWaitLockRelease(g_ControlLock);
    return STATUS_SUCCESS;
}

VOID EvtFilterCleanup(WDFOBJECT Object)
{
    PFILTER_CONTEXT ctx = FilterGetContext((WDFDEVICE)Object);
    KIRQL irql;
    ULONG i, remaining = 0;
    WDFDEVICE cdo = NULL;
    WDFQUEUE reads = NULL;
    DECLARE_CONST_UNICODE_STRING(link, MKF_SYMLINK_NAME);

    WdfWaitLockAcquire(g_ControlLock, NULL);
    KeAcquireSpinLock(&g_Lock, &irql);
    for (i = 0; i < MKF_MAX_DEVICES; i++) {
        if (g_Devices[i] == ctx) g_Devices[i] = NULL;
        if (g_Devices[i]) remaining++;
    }
    if (remaining == 0) {
        cdo = g_ControlDevice; reads = g_PendingReads;
        g_ControlDevice = NULL; g_PendingReads = NULL; g_RingCount = 0;
        // Subscriptions belonged to this control device; its handles must not keep F-keys swallowed.
        g_Subscribers = 0; g_RawSubscribers = 0;
    }
    KeReleaseSpinLock(&g_Lock, irql);
    if (cdo) {
        // Last keyboard gone: delete the control device so the driver can unload (and be updated).
        IoDeleteSymbolicLink((PUNICODE_STRING)&link);
        // Complete the app's parked reads (as cancelled) so it notices and reconnects to the next one.
        if (reads) WdfIoQueuePurgeSynchronously(reads);
        WdfObjectDelete(cdo);
    }
    WdfWaitLockRelease(g_ControlLock);
}

// ------------------------------------------------------------------ IO from hidbth / hidusb

static VOID ForwardAndForget(WDFREQUEST Request, WDFIOTARGET Target)
{
    WDF_REQUEST_SEND_OPTIONS opts;
    WDF_REQUEST_SEND_OPTIONS_INIT(&opts, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
    if (!WdfRequestSend(Request, Target, &opts)) {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
    }
}

static VOID ForwardWithCompletion(WDFREQUEST Request, WDFIOTARGET Target,
                                  PFN_WDF_REQUEST_COMPLETION_ROUTINE Routine, PFILTER_CONTEXT Ctx)
{
    WdfRequestFormatRequestUsingCurrentType(Request);
    WdfRequestSetCompletionRoutine(Request, Routine, Ctx);
    if (!WdfRequestSend(Request, Target, WDF_NO_SEND_OPTIONS)) {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
    }
}

VOID EvtInternalIoctl(WDFQUEUE Queue, WDFREQUEST Request, size_t OutLen, size_t InLen, ULONG Code)
{
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    PFILTER_CONTEXT ctx = FilterGetContext(device);
    WDFIOTARGET target = WdfDeviceGetIoTarget(device);
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(WdfRequestWdmGetIrp(Request));
    UNREFERENCED_PARAMETER(OutLen);
    UNREFERENCED_PARAMETER(InLen);

    if (Code == IOCTL_INTERNAL_BTH_SUBMIT_BRB) {
        BRB_HEADER* hdr = (BRB_HEADER*)stack->Parameters.Others.Argument1;
        if (hdr && hdr->Type == BRB_L2CA_ACL_TRANSFER) {
            struct _BRB_L2CA_ACL_TRANSFER* t = (struct _BRB_L2CA_ACL_TRANSFER*)hdr;
            if (t->TransferFlags & ACL_TRANSFER_DIRECTION_IN) {
                ForwardWithCompletion(Request, target, BrbCompletion, ctx);
                return;
            }
        } else if (hdr && hdr->Type == BRB_L2CA_CLOSE_CHANNEL) {
            ResetKeys(ctx);   // disconnect: forget held keys so nothing sticks on reconnect
        }
    } else if (Code == IOCTL_INTERNAL_USB_SUBMIT_URB) {
        PURB urb = (PURB)stack->Parameters.Others.Argument1;
        if (urb && urb->UrbHeader.Function == URB_FUNCTION_BULK_OR_INTERRUPT_TRANSFER &&
            (urb->UrbBulkOrInterruptTransfer.TransferFlags & USBD_TRANSFER_DIRECTION_IN)) {
            ForwardWithCompletion(Request, target, UrbCompletion, ctx);
            return;
        }
    }
    ForwardAndForget(Request, target);
}

VOID BrbCompletion(WDFREQUEST Request, WDFIOTARGET Target, PWDF_REQUEST_COMPLETION_PARAMS Params, WDFCONTEXT Context)
{
    PFILTER_CONTEXT ctx = (PFILTER_CONTEXT)Context;
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(WdfRequestWdmGetIrp(Request));
    BRB_HEADER* hdr = (BRB_HEADER*)stack->Parameters.Others.Argument1;
    UNREFERENCED_PARAMETER(Target);

    if (NT_SUCCESS(Params->IoStatus.Status) && hdr && hdr->Type == BRB_L2CA_ACL_TRANSFER) {
        struct _BRB_L2CA_ACL_TRANSFER* t = (struct _BRB_L2CA_ACL_TRANSFER*)hdr;
        PUCHAR b = MapBuffer(t->Buffer, t->BufferMDL);
        if (b && t->BufferSize >= 2 && b[0] == HIDP_DATA_INPUT) {
            ProcessInputReport(ctx, b + 1, t->BufferSize - 1);
        }
    }
    WdfRequestComplete(Request, Params->IoStatus.Status);
}

VOID UrbCompletion(WDFREQUEST Request, WDFIOTARGET Target, PWDF_REQUEST_COMPLETION_PARAMS Params, WDFCONTEXT Context)
{
    PFILTER_CONTEXT ctx = (PFILTER_CONTEXT)Context;
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(WdfRequestWdmGetIrp(Request));
    PURB urb = (PURB)stack->Parameters.Others.Argument1;
    UNREFERENCED_PARAMETER(Target);

    if (urb && NT_SUCCESS(Params->IoStatus.Status) && USBD_SUCCESS(urb->UrbHeader.Status)) {
        struct _URB_BULK_OR_INTERRUPT_TRANSFER* x = &urb->UrbBulkOrInterruptTransfer;
        PUCHAR b = MapBuffer(x->TransferBuffer, x->TransferBufferMDL);
        if (b && x->TransferBufferLength > 0) ProcessInputReport(ctx, b, x->TransferBufferLength);
    }
    WdfRequestComplete(Request, Params->IoStatus.Status);
}

// ------------------------------------------------------------------ control device (app interface)

// Only handles on the current control device count. When a control device is retired its subscriptions are
// dropped wholesale (EvtFilterCleanup), so its handles must not change the counts again later.
static VOID UpdateSubscription(WDFFILEOBJECT File, ULONG NewFlags)
{
    PFILE_CONTEXT fc = FileGetContext(File);
    if (WdfFileObjectGetDevice(File) == g_ControlDevice) {
        if ((fc->Flags & MKF_SUB_ACTIONS) && !(NewFlags & MKF_SUB_ACTIONS)) InterlockedDecrement(&g_Subscribers);
        if (!(fc->Flags & MKF_SUB_ACTIONS) && (NewFlags & MKF_SUB_ACTIONS)) InterlockedIncrement(&g_Subscribers);
        if ((fc->Flags & MKF_SUB_RAW) && !(NewFlags & MKF_SUB_RAW)) InterlockedDecrement(&g_RawSubscribers);
        if (!(fc->Flags & MKF_SUB_RAW) && (NewFlags & MKF_SUB_RAW)) InterlockedIncrement(&g_RawSubscribers);
    }
    fc->Flags = NewFlags;
}

// The app went away (exit or crash): stop routing actions to it so keys fall back to plain F-keys,
// and fail its parked reads.
VOID EvtControlFileCleanup(WDFFILEOBJECT FileObject)
{
    WDFQUEUE q;
    WDFREQUEST req;
    KIRQL irql;
    UpdateSubscription(FileObject, 0);

    KeAcquireSpinLock(&g_Lock, &irql);
    q = g_PendingReads;
    KeReleaseSpinLock(&g_Lock, irql);
    if (!q) return;
    while (NT_SUCCESS(WdfIoQueueRetrieveRequestByFileObject(q, FileObject, &req))) {
        WdfRequestComplete(req, STATUS_CANCELLED);
    }
}

VOID EvtControlIoctl(WDFQUEUE Queue, WDFREQUEST Request, size_t OutLen, size_t InLen, ULONG Code)
{
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR info = 0;
    KIRQL irql;
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    // A handle on a retired control device (still alive because the app holds it) must reconnect rather
    // than subscribe to, or drain, the current one.
    BOOLEAN stale = device != g_ControlDevice;
    UNREFERENCED_PARAMETER(OutLen);
    UNREFERENCED_PARAMETER(InLen);

    if (stale && (Code == IOCTL_MKF_SUBSCRIBE || Code == IOCTL_MKF_READ_EVENT)) {
        WdfRequestComplete(Request, STATUS_DEVICE_REMOVED);
        return;
    }

    switch (Code) {
    case IOCTL_MKF_GET_STATUS: {
        MKF_STATUS* out;
        ULONG i;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(MKF_STATUS), (PVOID*)&out, NULL);
        if (!NT_SUCCESS(status)) break;
        RtlZeroMemory(out, sizeof(*out));
        out->ProtocolVersion = MKF_PROTOCOL_VERSION;
        out->ControlGeneration = ControlGetContext(device)->Generation;
        out->Subscribers = (ULONG)g_Subscribers;
        KeAcquireSpinLock(&g_Lock, &irql);
        out->ConfigLoaded = g_ConfigLoaded;
        for (i = 0; i < MKF_MAX_DEVICES; i++) {
            PFILTER_CONTEXT c = g_Devices[i];
            MKF_DEVICE_INFO* d;
            if (!c) continue;
            d = &out->Devices[out->DeviceCount++];
            d->Index = c->Index;
            d->ProductId = c->ProductId;
            d->Transport = (UCHAR)c->Transport;
            d->LayoutKnown = c->KeyboardReports > 0;
            d->InputReports = (ULONG)c->InputReports;
            d->KeyboardReports = (ULONG)c->KeyboardReports;
            d->LastRawLength = c->LastRawLength;
            RtlCopyMemory(d->LastRaw, c->LastRaw, sizeof(d->LastRaw));
            RtlCopyMemory(d->InstanceId, c->InstanceId, sizeof(d->InstanceId));
        }
        KeReleaseSpinLock(&g_Lock, irql);
        info = sizeof(MKF_STATUS);
        break;
    }
    case IOCTL_MKF_SET_CONFIG: {
        MK_CONFIG* in;
        MK_CONFIG copy;
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(MK_CONFIG), (PVOID*)&in, NULL);
        if (!NT_SUCCESS(status)) break;
        copy = *in;
        if (!MkConfigValid(&copy)) { status = STATUS_REVISION_MISMATCH; break; }
        ApplyConfig(&copy);
        SaveConfig(&copy);
        status = STATUS_SUCCESS;
        break;
    }
    case IOCTL_MKF_GET_CONFIG: {
        MK_CONFIG* out;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(MK_CONFIG), (PVOID*)&out, NULL);
        if (!NT_SUCCESS(status)) break;
        KeAcquireSpinLock(&g_Lock, &irql);
        *out = g_Config;
        KeReleaseSpinLock(&g_Lock, irql);
        info = sizeof(MK_CONFIG);
        break;
    }
    case IOCTL_MKF_SUBSCRIBE: {
        ULONG* in;
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(ULONG), (PVOID*)&in, NULL);
        if (!NT_SUCCESS(status)) break;
        UpdateSubscription(WdfRequestGetFileObject(Request), *in & (MKF_SUB_ACTIONS | MKF_SUB_RAW));
        // A new subscriber starts clean: never replay key presses queued while nobody was reading.
        KeAcquireSpinLock(&g_Lock, &irql);
        g_RingCount = 0;
        KeReleaseSpinLock(&g_Lock, irql);
        break;
    }
    case IOCTL_MKF_READ_EVENT: {
        MKF_EVENT* out;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(MKF_EVENT), (PVOID*)&out, NULL);
        if (!NT_SUCCESS(status)) break;
        KeAcquireSpinLock(&g_Lock, &irql);
        // Drop key actions too old to still mean anything (e.g. the app was stalled).
        while (g_RingCount > 0 && g_Ring[g_RingHead].Type == MkEventAction &&
               (LONGLONG)KeQueryInterruptTime() - g_Ring[g_RingHead].Timestamp > MKF_STALE_ACTION_100NS) {
            g_RingHead = (g_RingHead + 1) % MKF_RING_SIZE;
            g_RingCount--;
        }
        if (g_RingCount > 0) {
            *out = g_Ring[g_RingHead];
            g_RingHead = (g_RingHead + 1) % MKF_RING_SIZE;
            g_RingCount--;
            KeReleaseSpinLock(&g_Lock, irql);
            WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, sizeof(MKF_EVENT));
            return;
        }
        // Park under the lock so PushEvent cannot miss it.
        status = g_PendingReads ? WdfRequestForwardToIoQueue(Request, g_PendingReads) : STATUS_DEVICE_NOT_READY;
        KeReleaseSpinLock(&g_Lock, irql);
        if (NT_SUCCESS(status)) return;
        break;
    }
    default:
        break;
    }
    WdfRequestCompleteWithInformation(Request, status, info);
}
