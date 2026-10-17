using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MagicKeyboard.Shared;

public sealed record KeyboardDevice(int Index, ushort ProductId, KeyboardTransport Transport, bool LayoutKnown,
    uint InputReports, uint KeyboardReports, byte[] LastRaw, string InstanceId);

public sealed record DriverStatus(uint ProtocolVersion, bool ConfigLoaded, uint Subscribers,
    IReadOnlyList<KeyboardDevice> Devices);

public readonly record struct KeyAction(FKeyAction Action, bool Down, int CustomSlot);

public readonly record struct DriverEvent(uint Type, int DeviceIndex, KeyAction Action, long Timestamp, byte[] Data);

// User-mode side of the MagicKeyboardFilter control device (\\.\MagicKeyboardFilter).
// Thread-safe: ReadEvent may block on one thread while other calls run on another (overlapped handle).
public sealed class DriverChannel : IDisposable
{
    private readonly SafeFileHandle _handle;
    private volatile bool _disposed;

    private DriverChannel(SafeFileHandle h) => _handle = h;

    public static DriverChannel? TryOpen()
    {
        var h = CreateFile(Protocol.UserPath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return new DriverChannel(h);
    }

    public DriverStatus GetStatus()
    {
        var s = Read<MkfStatus>(Protocol.IoctlGetStatus);
        var devices = new List<KeyboardDevice>();
        for (int i = 0; i < Math.Min(s.DeviceCount, (uint)Protocol.MaxDevices); i++)
        {
            var d = s.Devices[i];
            devices.Add(new KeyboardDevice((int)d.Index, d.ProductId, (KeyboardTransport)d.Transport,
                d.LayoutKnown != 0, d.InputReports, d.KeyboardReports,
                d.LastRaw.Take((int)Math.Min(d.LastRawLength, 16u)).ToArray(), d.InstanceId ?? ""));
        }
        return new DriverStatus(s.ProtocolVersion, s.ConfigLoaded != 0, s.Subscribers, devices);
    }

    public void SetConfig(MkConfig config) => Write(Protocol.IoctlSetConfig, config);

    public MkConfig GetConfig() => Read<MkConfig>(Protocol.IoctlGetConfig);

    public void Subscribe(bool actions, bool raw) =>
        Write(Protocol.IoctlSubscribe, (actions ? Protocol.SubscribeActions : 0) | (raw ? Protocol.SubscribeRaw : 0));

    // Blocks until the driver has an event. Returns null when cancelled or disposed.
    public DriverEvent? ReadEvent()
    {
        MkfEvent e;
        try { e = Read<MkfEvent>(Protocol.IoctlReadEvent); }
        catch (Win32Exception ex) when (_disposed || ex.NativeErrorCode == ERROR_OPERATION_ABORTED) { return null; }
        if (_disposed) return null;
        var data = e.Data.Take((int)Math.Min(e.Length, (uint)Protocol.EventData)).ToArray();
        return new DriverEvent(e.Type, (int)e.DeviceIndex,
            new KeyAction((FKeyAction)e.Action, e.Down != 0, (int)e.CustomSlot), e.Timestamp, data);
    }

    public void CancelPendingReads()
    {
        if (!_handle.IsClosed) CancelIoEx(_handle, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPendingReads();
        _handle.Dispose();
    }

    // --- interop -------------------------------------------------------------

    private void Write<T>(uint code, T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, buf, false);
            if (!IoctlWait(code, buf, size, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private T Read<T>(uint code) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!IoctlWait(code, IntPtr.Zero, 0, buf, size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStructure<T>(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private unsafe bool IoctlWait(uint code, IntPtr inBuf, int inLen, IntPtr outBuf, int outLen)
    {
        using var evt = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
        if (DeviceIoControl(_handle, code, inBuf, inLen, outBuf, outLen, out _, &ov)) return true;
        if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
        return GetOverlappedResult(_handle, &ov, out _, true);
    }

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int ERROR_IO_PENDING = 997, ERROR_OPERATION_ABORTED = 995;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32", SetLastError = true)]
    private static extern unsafe bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize,
        IntPtr outBuf, int outSize, out int returned, NativeOverlapped* overlapped);

    [DllImport("kernel32", SetLastError = true)]
    private static extern unsafe bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* ov,
        out int transferred, bool wait);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
}
