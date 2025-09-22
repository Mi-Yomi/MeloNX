using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Ryujinx.Input.SDL2
{
    class iOSGamepad : IGamepad
    {
        private bool HasConfiguration => _configuration != null;

        private record struct ButtonMappingEntry(GamepadButtonInputId To, GamepadButtonInputId From);

        private StandardControllerInputConfig _configuration;

        // Current state storage
        private readonly Dictionary<GamepadButtonInputId, bool> _buttonStates = new();
        private readonly Dictionary<StickInputId, (float x, float y)> _stickStates = new();
        private Vector3 _accelerometerData = Vector3.Zero;
        private Vector3 _gyroscopeData = Vector3.Zero;

        private readonly object _userMappingLock = new();
        private readonly object _stateLock = new();

        private readonly List<ButtonMappingEntry> _buttonsUserMapping;

        private readonly StickInputId[] _stickUserMapping = new StickInputId[(int)StickInputId.Count]
        {
            StickInputId.Unbound,
            StickInputId.Left,
            StickInputId.Right,
        };

        public GamepadFeaturesFlag Features { get; private set; }

        private readonly string _gamepadId;
        private float _triggerThreshold;
        private bool _isConnected = true;

        public iOSGamepad(string gamepadId, string name, GamepadFeaturesFlag features)
        {
            _gamepadId = gamepadId;
            Name = name;
            Id = gamepadId;
            Features = features;
            _buttonsUserMapping = new List<ButtonMappingEntry>(20);
            _triggerThreshold = 0.0f;

            // Initialize button states
            for (int i = 0; i < (int)GamepadButtonInputId.Count; i++)
            {
                _buttonStates[(GamepadButtonInputId)i] = false;
            }

            // Initialize stick states
            _stickStates[StickInputId.Left] = (0.0f, 0.0f);
            _stickStates[StickInputId.Right] = (0.0f, 0.0f);
        }

        public string Id { get; }
        public string Name { get; }

        public bool IsConnected 
        { 
            get 
            { 
                lock (_stateLock) 
                { 
                    return _isConnected; 
                }
            }
        }

        // Static callback handlers - these will be called from native code
        [UnmanagedCallersOnly(EntryPoint = "onButtonPressed")]
        public static void OnButtonPressed(IntPtr gamepadPtr, int buttonId, byte isPressed)
        {
            if (TryGetGamepadFromPointer(gamepadPtr, out var gamepad))
            {
                gamepad.HandleButtonInput(buttonId, isPressed != 0);
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "onStickMoved")]
        public static void OnStickMoved(IntPtr gamepadPtr, int stickId, float x, float y)
        {
            if (TryGetGamepadFromPointer(gamepadPtr, out var gamepad))
            {
                gamepad.HandleStickInput(stickId, x, y);
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "onMotionData")]
        public static void OnMotionData(IntPtr gamepadPtr, int motionType, float x, float y, float z)
        {
            if (TryGetGamepadFromPointer(gamepadPtr, out var gamepad))
            {
                gamepad.HandleMotionInput(motionType, new Vector3(x, y, z));
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "onConnectionChanged")]
        public static void OnConnectionChanged(IntPtr gamepadPtr, byte isConnected)
        {
            if (TryGetGamepadFromPointer(gamepadPtr, out var gamepad))
            {
                gamepad.HandleConnectionChange(isConnected != 0);
            }
        }

        // Static registry to map native pointers to managed objects
        private static readonly Dictionary<IntPtr, WeakReference<iOSGamepad>> _gamepadRegistry = new();
        private static readonly object _registryLock = new();

        public void RegisterForCallbacks(IntPtr nativeHandle)
        {
            lock (_registryLock)
            {
                _gamepadRegistry[nativeHandle] = new WeakReference<iOSGamepad>(this);
            }
        }

        public void UnregisterFromCallbacks(IntPtr nativeHandle)
        {
            lock (_registryLock)
            {
                _gamepadRegistry.Remove(nativeHandle);
            }
        }

        private static bool TryGetGamepadFromPointer(IntPtr ptr, out iOSGamepad gamepad)
        {
            gamepad = null;
            lock (_registryLock)
            {
                if (_gamepadRegistry.TryGetValue(ptr, out var weakRef))
                {
                    return weakRef.TryGetTarget(out gamepad);
                }
            }
            return false;
        }

        // Instance methods to handle the input
        private void HandleButtonInput(int buttonId, bool isPressed)
        {
            if (buttonId >= 0 && buttonId < (int)GamepadButtonInputId.Count)
            {
                lock (_stateLock)
                {
                    _buttonStates[(GamepadButtonInputId)buttonId] = isPressed;
                }
            }
        }

        private void HandleStickInput(int stickId, float x, float y)
        {
            if (stickId >= 0 && stickId < (int)StickInputId.Count)
            {
                lock (_stateLock)
                {
                    _stickStates[(StickInputId)stickId] = (x, y);
                }
            }
        }

        private void HandleMotionInput(int motionType, Vector3 data)
        {
            lock (_stateLock)
            {
                switch (motionType)
                {
                    case 0: // Accelerometer
                        _accelerometerData = GsToMs2(data);
                        break;
                    case 1: // Gyroscope
                        _gyroscopeData = RadToDegree(data);
                        break;
                }
            }
        }

        private void HandleConnectionChange(bool connected)
        {
            lock (_stateLock)
            {
                _isConnected = connected;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Cleanup any native handles here if needed
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public void SetTriggerThreshold(float triggerThreshold)
        {
            _triggerThreshold = triggerThreshold;
        }

        public void Rumble(float lowFrequency, float highFrequency, uint durationMs)
        {
            if (Features.HasFlag(GamepadFeaturesFlag.Rumble))
            {
                // Call native rumble function
                // NativeRumble(_gamepadId, lowFrequency, highFrequency, durationMs);
            }
        }

        // P/Invoke for native rumble function (implement in your native layer)
        // [DllImport("__Internal")]
        // private static extern void NativeRumble(string gamepadId, float lowFreq, float highFreq, uint durationMs);

        public Vector3 GetMotionData(MotionInputId inputId)
        {
            if (!Features.HasFlag(GamepadFeaturesFlag.Motion))
            {
                return Vector3.Zero;
            }

            lock (_stateLock)
            {
                return inputId switch
                {
                    MotionInputId.Accelerometer => _accelerometerData,
                    MotionInputId.Gyroscope => _gyroscopeData,
                    _ => Vector3.Zero
                };
            }
        }

        private static Vector3 RadToDegree(Vector3 rad)
        {
            return rad * (180 / MathF.PI);
        }

        private static Vector3 GsToMs2(Vector3 gs)
        {
            const float SDL_STANDARD_GRAVITY = 9.80665f;
            return gs / SDL_STANDARD_GRAVITY;
        }

        public void SetConfiguration(InputConfig configuration)
        {
            lock (_userMappingLock)
            {
                _configuration = (StandardControllerInputConfig)configuration;

                _buttonsUserMapping.Clear();

                // First update sticks
                _stickUserMapping[(int)StickInputId.Left] = (StickInputId)_configuration.LeftJoyconStick.Joystick;
                _stickUserMapping[(int)StickInputId.Right] = (StickInputId)_configuration.RightJoyconStick.Joystick;

                // Then left joycon
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.LeftStick, (GamepadButtonInputId)_configuration.LeftJoyconStick.StickButton));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadUp, (GamepadButtonInputId)_configuration.LeftJoycon.DpadUp));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadDown, (GamepadButtonInputId)_configuration.LeftJoycon.DpadDown));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadLeft, (GamepadButtonInputId)_configuration.LeftJoycon.DpadLeft));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadRight, (GamepadButtonInputId)_configuration.LeftJoycon.DpadRight));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.Minus, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonMinus));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.LeftShoulder, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonL));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.LeftTrigger, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonZl));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleRightTrigger0, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonSr));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleLeftTrigger0, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonSl));

                // Finally right joycon
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.RightStick, (GamepadButtonInputId)_configuration.RightJoyconStick.StickButton));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.A, (GamepadButtonInputId)_configuration.RightJoycon.ButtonA));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.B, (GamepadButtonInputId)_configuration.RightJoycon.ButtonB));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.X, (GamepadButtonInputId)_configuration.RightJoycon.ButtonX));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.Y, (GamepadButtonInputId)_configuration.RightJoycon.ButtonY));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.Plus, (GamepadButtonInputId)_configuration.RightJoycon.ButtonPlus));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.RightShoulder, (GamepadButtonInputId)_configuration.RightJoycon.ButtonR));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.RightTrigger, (GamepadButtonInputId)_configuration.RightJoycon.ButtonZr));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleRightTrigger1, (GamepadButtonInputId)_configuration.RightJoycon.ButtonSr));
                _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleLeftTrigger1, (GamepadButtonInputId)_configuration.RightJoycon.ButtonSl));

                SetTriggerThreshold(_configuration.TriggerThreshold);
            }
        }

        public GamepadStateSnapshot GetStateSnapshot()
        {
            return IGamepad.GetStateSnapshot(this);
        }

        public GamepadStateSnapshot GetMappedStateSnapshot()
        {
            GamepadStateSnapshot rawState = GetStateSnapshot();
            GamepadStateSnapshot result = default;

            lock (_userMappingLock)
            {
                if (_buttonsUserMapping.Count == 0)
                {
                    return rawState;
                }

                foreach (ButtonMappingEntry entry in _buttonsUserMapping)
                {
                    if (entry.From == GamepadButtonInputId.Unbound || entry.To == GamepadButtonInputId.Unbound)
                    {
                        continue;
                    }

                    // Do not touch state of button already pressed
                    if (!result.IsPressed(entry.To))
                    {
                        result.SetPressed(entry.To, rawState.IsPressed(entry.From));
                    }
                }

                (float leftStickX, float leftStickY) = rawState.GetStick(_stickUserMapping[(int)StickInputId.Left]);
                (float rightStickX, float rightStickY) = rawState.GetStick(_stickUserMapping[(int)StickInputId.Right]);

                result.SetStick(StickInputId.Left, leftStickX, leftStickY);
                result.SetStick(StickInputId.Right, rightStickX, rightStickY);
            }

            return result;
        }

        public (float, float) GetStick(StickInputId inputId)
        {
            if (inputId == StickInputId.Unbound)
            {
                return (0.0f, 0.0f);
            }

            float resultX, resultY;

            lock (_stateLock)
            {
                if (!_stickStates.TryGetValue(inputId, out var stickState))
                {
                    return (0.0f, 0.0f);
                }

                resultX = stickState.x;
                resultY = stickState.y;
            }

            if (HasConfiguration)
            {
                if ((inputId == StickInputId.Left && _configuration.LeftJoyconStick.InvertStickX) ||
                    (inputId == StickInputId.Right && _configuration.RightJoyconStick.InvertStickX))
                {
                    resultX = -resultX;
                }

                if ((inputId == StickInputId.Left && _configuration.LeftJoyconStick.InvertStickY) ||
                    (inputId == StickInputId.Right && _configuration.RightJoyconStick.InvertStickY))
                {
                    resultY = -resultY;
                }

                if ((inputId == StickInputId.Left && _configuration.LeftJoyconStick.Rotate90CW) ||
                    (inputId == StickInputId.Right && _configuration.RightJoyconStick.Rotate90CW))
                {
                    float temp = resultX;
                    resultX = resultY;
                    resultY = -temp;
                }
            }

            return (resultX, resultY);
        }

        public bool IsPressed(GamepadButtonInputId inputId)
        {
            lock (_stateLock)
            {
                // Handle trigger threshold for analog triggers
                if (inputId == GamepadButtonInputId.LeftTrigger || inputId == GamepadButtonInputId.RightTrigger)
                {
                    // For triggers, you might want to handle them as analog values
                    // This assumes your native code sends trigger values as button states based on threshold
                    return _buttonStates.TryGetValue(inputId, out bool pressed) && pressed;
                }

                return _buttonStates.TryGetValue(inputId, out bool isPressed) && isPressed;
            }
        }
    }

    // Enum definitions to match your callback system
    public enum NativeButtonId
    {
        A = 1,
        B = 2,
        X = 3,
        Y = 4,
        LeftStick = 5,
        RightStick = 6,
        LeftShoulder = 7,
        RightShoulder = 8,
        LeftTrigger = 9,
        RightTrigger = 10,
        DpadUp = 11,
        DpadDown = 12,
        DpadLeft = 13,
        DpadRight = 14,
        Minus = 15,
        Plus = 16,
        Guide = 17,
        // Add more as needed
    }

    public enum NativeStickId
    {
        Left = 1,
        Right = 2
    }

    public enum NativeMotionType
    {
        Accelerometer = 0,
        Gyroscope = 1
    }
}