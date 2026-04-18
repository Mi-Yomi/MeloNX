using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void SwiftDataCallback(ref CallbackData data, nint userData);

[StructLayout(LayoutKind.Sequential)]
public struct CallbackData
{
    public nint Ptr;
    public int    Len;

    public static CallbackData Empty => new() { Ptr = nint.Zero, Len = 0 };
}

public static class CallbackRegistry
{
    private record Entry(SwiftDataCallback Callback, nint UserData);

    private static readonly ConcurrentDictionary<string, Entry> _callbacks = new();

    [UnmanagedCallersOnly(EntryPoint = "RegisterCallback")]
    public static void RegisterCallback(nint namePtr, nint callbackPtr, nint userData)
    {
        var name = Marshal.PtrToStringUTF8(namePtr);
        if (name == null) return;

        var cb = Marshal.GetDelegateForFunctionPointer<SwiftDataCallback>(callbackPtr);
        _callbacks[name] = new Entry(cb, userData);
    }
    
    [UnmanagedCallersOnly(EntryPoint = "UnregisterCallback")]
    public static void UnregisterCallback(nint namePtr)
    {
        var name = Marshal.PtrToStringUTF8(namePtr);
        if (name == null) return;
        _callbacks.TryRemove(name, out _);
    }

    public static bool Invoke(string name, ReadOnlySpan<byte> bytes)
    {
        if (!_callbacks.TryGetValue(name, out var entry)) return false;

        if (bytes.IsEmpty)
        {
            var empty = CallbackData.Empty;
            entry.Callback(ref empty, entry.UserData);
            return true;
        }

        var arr    = bytes.ToArray();
        var handle = GCHandle.Alloc(arr, GCHandleType.Pinned);
        try
        {
            var cd = new CallbackData { Ptr = handle.AddrOfPinnedObject(), Len = arr.Length };
            entry.Callback(ref cd, entry.UserData);
        }
        finally { handle.Free(); }

        return true;
    }

    public static unsafe bool Invoke(string name, byte* ptr, int len)
    {
        if (!_callbacks.TryGetValue(name, out var entry)) return false;

        var cd = new CallbackData { Ptr = (nint)ptr, Len = len };
        entry.Callback(ref cd, entry.UserData);
        return true;
    }


    public static unsafe bool Invoke(string name, nint ptr, int len)
    {
        if (!_callbacks.TryGetValue(name, out var entry)) return false;

        var cd = new CallbackData { Ptr = ptr, Len = len };
        entry.Callback(ref cd, entry.UserData);
        return true;
    }

    public static bool Invoke(string name)
    {
        if (!_callbacks.TryGetValue(name, out var entry)) return false;
        var empty = CallbackData.Empty;
        entry.Callback(ref empty, entry.UserData);
        return true;
    }

    public static unsafe bool Invoke<T>(string name, T value) where T : unmanaged
    {
        var span = new ReadOnlySpan<byte>(&value, sizeof(T));
        return Invoke(name, span);
    }

    [UnmanagedCallersOnly(EntryPoint = "InvokeCallback")]
    public static byte InvokeCallback(nint namePtr, nint dataPtr, int dataLen)
    {
        var name = Marshal.PtrToStringUTF8(namePtr);
        if (name == null) return 0;

        if (dataPtr == nint.Zero || dataLen == 0)
            return Invoke(name) ? (byte)1 : (byte)0;

        unsafe
        {
            return Invoke(name, (byte*)dataPtr, dataLen) ? (byte)1 : (byte)0;
        }
    }
}

