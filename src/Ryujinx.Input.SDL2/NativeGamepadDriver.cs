using Ryujinx.Common.Configuration.Hid;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Input.SDL2
{
    public class NativeGamepadDriver : IGamepadDriver
    {
        private static NativeGamepadDriver _instance;
        private readonly Dictionary<string, NativeGamepad> _gamepads;
        private readonly List<string> _gamepadIds;
        private static readonly object _lock = new object();

        public ReadOnlySpan<string> GamepadsIds
        {
            get
            {
                lock (_lock)
                {
                    return _gamepadIds.ToArray();
                }
            }
        }

        public string DriverName => "Native";

        public event Action<string> OnGamepadConnected;
        public event Action<string> OnGamepadDisconnected;

        public NativeGamepadDriver()
        {
            _gamepads = new Dictionary<string, NativeGamepad>();
            _gamepadIds = new List<string>();
            _instance = this;
        }

        public static IntPtr AttachGamepad(IntPtr namePtr, IntPtr idPtr)
        {
            try
            {
                string name = Marshal.PtrToStringAnsi(namePtr);
                string id = idPtr.ToInt64().ToString("X");
                
                if (_instance != null && !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id))
                {
                    lock (_lock)
                    {
                        if (!_instance._gamepads.ContainsKey(id))
                        {
                            NativeGamepad gamepad = new(name, id);
                            _instance._gamepads.Add(id, gamepad);
                            _instance._gamepadIds.Add(id);
                            _instance.OnGamepadConnected?.Invoke(id);
                        }
                    }
                }

                return idPtr;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
        
        public static void DetachGamepad(IntPtr idPtr)
        {
            try
            {
                string id = idPtr.ToInt64().ToString("X");

                if (_instance != null && !string.IsNullOrEmpty(id))
                {
                    lock (_lock)
                    {
                        if (_instance._gamepads.TryGetValue(id, out NativeGamepad gamepad))
                        {
                            gamepad.Dispose();
                            _instance._gamepads.Remove(id);
                            _instance._gamepadIds.Remove(id);
                            _instance.OnGamepadDisconnected?.Invoke(id);
                        }
                    }
                }
            }
            catch { }
        }

        private static NativeGamepad GetGamepadById(string id)
        {
            if (_instance != null && !string.IsNullOrEmpty(id))
            {
                lock (_lock)
                {
                    if (_instance._gamepads.TryGetValue(id, out NativeGamepad gamepad))
                    {
                        return gamepad;
                    }
                }
            }
            return null;
        }

        public static void SetButtonState(IntPtr idPtr, int buttonId, byte pressed)
        {
            try
            {
                string id = idPtr.ToInt64().ToString("X");
                NativeGamepad gamepad = GetGamepadById(id);
                gamepad?.SetButtonStateInternal(buttonId, pressed != 0);
            }
            catch { }
        }

        public static void SetStickAxis(IntPtr idPtr, int stickId, float x, float y)
        {
            try
            {
                string id = idPtr.ToInt64().ToString("X");
                NativeGamepad gamepad = GetGamepadById(id);
                gamepad?.SetStickAxisInternal(stickId, x, y);
            }
            catch { }
        }

        public static void SetMotionData(IntPtr idPtr, int motionType, float x, float y, float z)
        {
            try
            {
                string id = idPtr.ToInt64().ToString("X");
                NativeGamepad gamepad = GetGamepadById(id);
                gamepad?.SetMotionDataInternal(motionType, x, y, z);
            }
            catch { }
        }

        public static void ResetState(IntPtr idPtr)
        {
            try
            {
                string id = idPtr.ToInt64().ToString("X");
                NativeGamepad gamepad = GetGamepadById(id);
                gamepad?.ResetStateInternal();
            }
            catch { }
        }

        public IGamepad GetGamepad(string id)
        {
            return GetGamepadById(id);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_lock)
                {
                    foreach (var gamepad in _gamepads.Values)
                    {
                        gamepad.Dispose();
                    }

                    foreach (string id in _gamepadIds)
                    {
                        OnGamepadDisconnected?.Invoke(id);
                    }

                    _gamepads.Clear();
                    _gamepadIds.Clear();
                }

                if (_instance == this)
                {
                    _instance = null;
                }
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }
    }
}