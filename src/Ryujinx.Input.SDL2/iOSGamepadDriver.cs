using Ryujinx.Common.Configuration.Hid;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Input.SDL2
{
    public class iOSGamepadDriver : IGamepadDriver
    {
        private readonly Dictionary<string, iOSGamepad> _gamepads;
        private readonly Dictionary<IntPtr, string> _nativeHandleToId;
        private readonly List<string> _gamepadsIds;
        private readonly object _lock = new object();

        public ReadOnlySpan<string> GamepadsIds
        {
            get
            {
                lock (_lock)
                {
                    return _gamepadsIds.ToArray();
                }
            }
        }

        public string DriverName => "iOS GameController";

        public event Action<string> OnGamepadConnected;
        public event Action<string> OnGamepadDisconnected;

        // Static instance for callbacks
        private static iOSGamepadDriver _instance;
        private static readonly object _instanceLock = new object();

        public iOSGamepadDriver()
        {
            _gamepads = new Dictionary<string, iOSGamepad>();
            _nativeHandleToId = new Dictionary<IntPtr, string>();
            _gamepadsIds = new List<string>();

            lock (_instanceLock)
            {
                _instance = this;
            }

            // Initialize native iOS GameController framework
            InitializeNativeGameControllers();
        }

        // P/Invoke declarations for native iOS functions
        [DllImport("__Internal")]
        private static extern void InitializeGameControllers(
            IntPtr onConnectedCallback,
            IntPtr onDisconnectedCallback
        );

        [DllImport("__Internal")]
        private static extern IntPtr GetConnectedGameControllers(out int count);

        [DllImport("__Internal")]
        private static extern IntPtr GetGameControllerName(IntPtr handle);

        [DllImport("__Internal")]
        private static extern IntPtr GetGameControllerIdentifier(IntPtr handle);

        [DllImport("__Internal")]
        private static extern int GetGameControllerFeatures(IntPtr handle);

        [DllImport("__Internal")]
        private static extern void SetupGameControllerCallbacks(
            IntPtr handle,
            IntPtr buttonCallback,
            IntPtr stickCallback,
            IntPtr motionCallback,
            IntPtr connectionCallback
        );

        [DllImport("__Internal")]
        private static extern void ReleaseGameController(IntPtr handle);

        [DllImport("__Internal")]
        private static extern void CleanupGameControllers();

        // Static callback methods - these will be called from native code
        [UnmanagedCallersOnly(EntryPoint = "onGameControllerConnected")]
        public static void OnGameControllerConnected(IntPtr handle)
        {
            lock (_instanceLock)
            {
                _instance?.HandleGameControllerConnected(handle);
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "onGameControllerDisconnected")]
        public static void OnGameControllerDisconnected(IntPtr handle)
        {
            lock (_instanceLock)
            {
                _instance?.HandleGameControllerDisconnected(handle);
            }
        }

        private void InitializeNativeGameControllers()
        {
            // Get function pointers for callbacks
            unsafe
            {
                delegate* unmanaged<IntPtr, void> onConnectedPtr = &OnGameControllerConnected;
                delegate* unmanaged<IntPtr, void> onDisconnectedPtr = &OnGameControllerDisconnected;

                InitializeGameControllers(
                    (IntPtr)onConnectedPtr,
                    (IntPtr)onDisconnectedPtr
                );
            }

            // Check for already connected controllers
            IntPtr controllersArray = GetConnectedGameControllers(out int count);
            if (controllersArray != IntPtr.Zero && count > 0)
            {
                unsafe
                {
                    IntPtr* controllers = (IntPtr*)controllersArray;
                    for (int i = 0; i < count; i++)
                    {
                        HandleGameControllerConnected(controllers[i]);
                    }
                }
            }
        }

        private void HandleGameControllerConnected(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return;

            try
            {
                // Get controller information
                IntPtr namePtr = GetGameControllerName(handle);
                IntPtr identifierPtr = GetGameControllerIdentifier(handle);
                int featuresFlags = GetGameControllerFeatures(handle);

                string name = Marshal.PtrToStringUTF8(namePtr) ?? "Unknown iOS Controller";
                string identifier = Marshal.PtrToStringUTF8(identifierPtr) ?? Guid.NewGuid().ToString();

                // Create unique ID combining identifier and handle
                string gamepadId = $"iOS-{identifier}-{handle.ToInt64():X}";

                GamepadFeaturesFlag features = (GamepadFeaturesFlag)featuresFlags;

                lock (_lock)
                {
                    // Check if we already have this gamepad
                    if (_gamepads.ContainsKey(gamepadId) || _nativeHandleToId.ContainsKey(handle))
                    {
                        return;
                    }

                    // Create iOS gamepad instance
                    var gamepad = new iOSGamepad(gamepadId, name, features);
                    
                    // Register the gamepad for callbacks
                    gamepad.RegisterForCallbacks(handle);

                    // Setup native callbacks for this controller
                    unsafe
                    {
                        delegate* unmanaged<IntPtr, int, byte, void> buttonPtr = &iOSGamepad.OnButtonPressed;
                        delegate* unmanaged<IntPtr, int, float, float, void> stickPtr = &iOSGamepad.OnStickMoved;
                        delegate* unmanaged<IntPtr, int, float, float, float, void> motionPtr = &iOSGamepad.OnMotionData;
                        delegate* unmanaged<IntPtr, byte, void> connectionPtr = &iOSGamepad.OnConnectionChanged;

                        SetupGameControllerCallbacks(
                            handle,
                            (IntPtr)buttonPtr,
                            (IntPtr)stickPtr,
                            (IntPtr)motionPtr,
                            (IntPtr)connectionPtr
                        );
                    }

                    _gamepads[gamepadId] = gamepad;
                    _nativeHandleToId[handle] = gamepadId;
                    _gamepadsIds.Add(gamepadId);
                }

                OnGamepadConnected?.Invoke(gamepadId);
            }
            catch (Exception ex)
            {
                // Log the error (you might want to use your logging system here)
                Console.WriteLine($"Error handling gamepad connection: {ex.Message}");
            }
        }

        private void HandleGameControllerDisconnected(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return;

            lock (_lock)
            {
                if (_nativeHandleToId.TryGetValue(handle, out string gamepadId))
                {
                    if (_gamepads.TryGetValue(gamepadId, out var gamepad))
                    {
                        // Unregister from callbacks
                        gamepad.UnregisterFromCallbacks(handle);
                        gamepad.Dispose();

                        _gamepads.Remove(gamepadId);
                    }

                    _nativeHandleToId.Remove(handle);
                    _gamepadsIds.Remove(gamepadId);

                    // Release native resources
                    ReleaseGameController(handle);

                    OnGamepadDisconnected?.Invoke(gamepadId);
                }
            }
        }

        public IGamepad GetGamepad(string id)
        {
            lock (_lock)
            {
                _gamepads.TryGetValue(id, out var gamepad);
                return gamepad;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_lock)
                {
                    // Dispose all gamepads and clean up native handles
                    foreach (var kvp in _gamepads)
                    {
                        kvp.Value.Dispose();
                        OnGamepadDisconnected?.Invoke(kvp.Key);
                    }

                    // Find and release all native handles
                    foreach (var handle in _nativeHandleToId.Keys)
                    {
                        ReleaseGameController(handle);
                    }

                    _gamepads.Clear();
                    _nativeHandleToId.Clear();
                    _gamepadsIds.Clear();
                }

                // Cleanup native GameController framework
                CleanupGameControllers();

                lock (_instanceLock)
                {
                    if (_instance == this)
                    {
                        _instance = null;
                    }
                }
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        // Helper method to get gamepad by native handle (useful for debugging)
        internal iOSGamepad GetGamepadByHandle(IntPtr handle)
        {
            lock (_lock)
            {
                if (_nativeHandleToId.TryGetValue(handle, out string id))
                {
                    return _gamepads.TryGetValue(id, out var gamepad) ? gamepad : null;
                }
                return null;
            }
        }

        // Method to refresh connected controllers (useful if native notifications fail)
        public void RefreshControllers()
        {
            IntPtr controllersArray = GetConnectedGameControllers(out int count);
            if (controllersArray != IntPtr.Zero && count > 0)
            {
                unsafe
                {
                    IntPtr* controllers = (IntPtr*)controllersArray;
                    for (int i = 0; i < count; i++)
                    {
                        if (!_nativeHandleToId.ContainsKey(controllers[i]))
                        {
                            HandleGameControllerConnected(controllers[i]);
                        }
                    }
                }
            }
        }
    }

    // Extension of GamepadFeaturesFlag to match native iOS features
    [Flags]
    public enum iOSGamepadFeatures
    {
        None = 0,
        BasicGamepad = 1,
        ExtendedGamepad = 2,
        Motion = 4,
        Haptics = 8,
        AdaptiveTriggers = 16,
        PhysicalInputProfile = 32
    }
}