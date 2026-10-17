using System.Runtime.InteropServices;

namespace MagicKeyboard.Shared;

// C# mirror of driver/MagicKeyboardFilter/Public.h. Keep byte-for-byte in sync.

public enum FKeyAction : byte
{
    None = 0, FKey = 1, BrightnessDown = 2, BrightnessUp = 3, TaskView = 4,
    Search = 5, AppSwitcher = 6, Dictation = 7, Sleep = 8, PrevTrack = 9,
    Rewind = 10, PlayPause = 11, Stop = 12, NextTrack = 13, FastForward = 14,
    Mute = 15, VolumeDown = 16, VolumeUp = 17, PrintScreen = 18, ScrollLock = 19,
    PauseBreak = 20, LaunchExplorer = 21, LaunchCalc = 22, LaunchNotepad = 23,
    LaunchTaskMgr = 24, LaunchCustom = 25, MissionControl = 26, Lock = 27
}

public enum ModTarget : byte
{
    LeftControl = 0xE0, LeftShift = 0xE1, LeftAlt = 0xE2, LeftGui = 0xE3,
    RightControl = 0xE4, RightShift = 0xE5, RightAlt = 0xE6, RightGui = 0xE7,
    Disabled = 0xF0, CapsLock = 0xF1, Fn = 0xF2, Menu = 0xF3
}

public enum ModSlot : byte
{
    LeftControl = 0, LeftShift, LeftAlt, LeftGui, RightControl, RightShift,
    RightAlt, RightGui, CapsLock, Fn, Menu, Count
}

public enum ClearMode : byte { NumLock = 0, Delete = 1, Backspace = 2, Off = 3 }
public enum Preset : byte { Windows = 0, MacOS = 1, Apple = 2, Custom = 3 }
public enum KeyboardTransport : byte { Unknown = 0, Bluetooth = 1, Usb = 2 }

public static class Protocol
{
    public const string UserPath = @"\\.\MagicKeyboardFilter";
    public const uint ProtocolVersion = 3;
    public const int MaxDevices = 8;
    public const int EventData = 32;

    public const uint ConfigMagic = 0x4D4B4333; // 'MKC3'
    public const uint ConfigVersion = 3;
    public const int FKeyCount = 19;
    public const int ModSlotCount = 11;

    public const uint SubscribeActions = 0x1;
    public const uint SubscribeRaw = 0x2;

    public const uint EventAction = 1;
    public const uint EventRaw = 2;

    // CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800 + fn, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private static uint Ctl(uint fn) => (0x22u << 16) | ((0x800u + fn) << 2);
    public static readonly uint IoctlGetStatus = Ctl(0);
    public static readonly uint IoctlSetConfig = Ctl(1);
    public static readonly uint IoctlGetConfig = Ctl(2);
    public static readonly uint IoctlSubscribe = Ctl(3);
    public static readonly uint IoctlReadEvent = Ctl(4);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FKeySlot
{
    public byte Action;      // FKeyAction
    public byte CustomSlot;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MkConfig
{
    public uint Magic;
    public uint Version;
    public uint Size;
    public byte Enabled;
    public byte Preset;
    public byte FnLock;
    public byte SleepShortcut;
    public byte ClearMode;
    public byte EjectMode;      // reserved
    public byte SwapCmdOption;
    public byte ThirdCharMod;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = Protocol.FKeyCount)]
    public FKeySlot[] FKeys;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = Protocol.ModSlotCount)]
    public byte[] Modifiers;
    public byte TouchIdAction;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
    public byte[] Reserved;

    public static MkConfig CreateEmpty()
    {
        var c = new MkConfig
        {
            Magic = Protocol.ConfigMagic,
            Version = Protocol.ConfigVersion,
            FKeys = new FKeySlot[Protocol.FKeyCount],
            Modifiers = new byte[Protocol.ModSlotCount],
            Reserved = new byte[15]
        };
        c.Size = (uint)Marshal.SizeOf<MkConfig>();
        return c;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
public struct MkfDeviceInfo
{
    public uint Index;
    public ushort ProductId;
    public byte Transport;
    public byte LayoutKnown;
    public uint InputReports;
    public uint KeyboardReports;
    public uint LastRawLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] LastRaw;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string InstanceId;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MkfStatus
{
    public uint ProtocolVersion;
    public uint DeviceCount;
    public uint Subscribers;
    public uint ConfigLoaded;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = Protocol.MaxDevices)]
    public MkfDeviceInfo[] Devices;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MkfEvent
{
    public uint Type;
    public uint DeviceIndex;
    public uint Action;
    public uint Down;
    public uint CustomSlot;
    public long Timestamp;
    public uint Length;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = Protocol.EventData)]
    public byte[] Data;
}
