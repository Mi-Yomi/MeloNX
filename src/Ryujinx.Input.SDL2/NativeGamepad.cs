using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using System.Collections.Generic;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Ryujinx.Input.SDL2
{
    public class NativeGamepad : IGamepad
    {
        private readonly object _stateLock = new object();
        private readonly bool[] _buttonStates;
        private readonly float[] _stickStates; 
        private readonly Vector3[] _motionStates; 

        private readonly Dictionary<int, GamepadButtonInputId> intToInputId = new()
        {
            [0] = GamepadButtonInputId.A,
            [1] = GamepadButtonInputId.B,
            [2] = GamepadButtonInputId.X,
            [3] = GamepadButtonInputId.Y,
            [4] = GamepadButtonInputId.Back,
            [5] = GamepadButtonInputId.Guide,
            [6] = GamepadButtonInputId.Start,
            [7] = GamepadButtonInputId.LeftStick,
            [8] = GamepadButtonInputId.RightStick,
            [9] = GamepadButtonInputId.LeftShoulder,
            [10] = GamepadButtonInputId.RightShoulder,
            [11] = GamepadButtonInputId.DpadUp,
            [12] = GamepadButtonInputId.DpadDown,
            [13] = GamepadButtonInputId.DpadLeft,
            [14] = GamepadButtonInputId.DpadRight,
            [15] = GamepadButtonInputId.LeftTrigger,
            [16] = GamepadButtonInputId.RightTrigger
        };


        private StandardControllerInputConfig _configuration;
        private float _triggerThreshold;

        public string Id { get; }
        public string Name { get; }
        public bool IsConnected { get; private set; }
        public GamepadFeaturesFlag Features { get; }

        public NativeGamepad(string name, string id)
        {
            Name = name;
            Id = id;
            IsConnected = true;
            Features = GamepadFeaturesFlag.Rumble | GamepadFeaturesFlag.Motion;

            _buttonStates = new bool[(int)GamepadButtonInputId.Count];
            _stickStates = new float[4];
            _motionStates = new Vector3[2];
            _triggerThreshold = 0.0f;
        }

        internal void SetButtonStateInternal(int buttonId2, bool pressed)
        {
            GamepadButtonInputId buttonEnum = intToInputId[buttonId2];
            
            int buttonId = (int)buttonEnum;

            if (buttonId >= 0 && buttonId < (int)GamepadButtonInputId.Count)
            {
                lock (_stateLock)
                {
                    _buttonStates[buttonId] = pressed;
                }
            }
        }

        internal void SetStickAxisInternal(int stickId, float x, float y)
        {
            lock (_stateLock)
            {
                if (stickId == (int)StickInputId.Left)
                {
                    _stickStates[0] = Math.Clamp(x, -1.0f, 1.0f);
                    _stickStates[1] = Math.Clamp(y, -1.0f, 1.0f);
                }
                else if (stickId == (int)StickInputId.Right)
                {
                    _stickStates[2] = Math.Clamp(x, -1.0f, 1.0f);
                    _stickStates[3] = Math.Clamp(y, -1.0f, 1.0f);
                }
            }
        }

        internal void SetMotionDataInternal(int motionType, float x, float y, float z)
        {
            lock (_stateLock)
            {
                // Console.WriteLine($"{motionType}, {x}, {y}, {z}");
                if (motionType == (int)MotionInputId.Accelerometer)
                {
                    _motionStates[0] = new Vector3(x, y, z);
                }
                else if (motionType == (int)MotionInputId.Gyroscope)
                {
                    _motionStates[1] = new Vector3(x, y, z);
                }
            }
        }

        internal void ResetStateInternal()
        {
            lock (_stateLock)
            {
                Array.Clear(_buttonStates, 0, _buttonStates.Length);
                Array.Clear(_stickStates, 0, _stickStates.Length);
                Array.Clear(_motionStates, 0, _motionStates.Length);
            }
        }

        public bool IsPressed(GamepadButtonInputId inputId)
        {
            lock (_stateLock)
            {
                if ((int)inputId >= 0 && (int)inputId < _buttonStates.Length)
                {
                    return _buttonStates[(int)inputId];
                }
            }

            return false;
        }

        public (float, float) GetStick(StickInputId inputId)
        {
            lock (_stateLock)
            {
                if (inputId == StickInputId.Left)
                {
                    return (_stickStates[0], _stickStates[1]);
                }
                else if (inputId == StickInputId.Right)
                {
                    return (_stickStates[2], _stickStates[3]);
                }
            }

            return (0.0f, 0.0f);
        }

        public Vector3 GetMotionData(MotionInputId inputId)
        {
            lock (_stateLock)
            {
                if (inputId == MotionInputId.Accelerometer)
                {
                    return _motionStates[0];
                }
                else if (inputId == MotionInputId.Gyroscope)
                {
                    return _motionStates[1];
                }
            }

            return Vector3.Zero;
        }

        public void SetConfiguration(InputConfig configuration)
        {
            _configuration = (StandardControllerInputConfig)configuration;
            SetTriggerThreshold(_configuration.TriggerThreshold);
        }

        public void SetTriggerThreshold(float triggerThreshold)
        {
            _triggerThreshold = triggerThreshold;
        }

        public GamepadStateSnapshot GetStateSnapshot()
        {
            return IGamepad.GetStateSnapshot(this);
        }

        public GamepadStateSnapshot GetMappedStateSnapshot()
        {
            return GetStateSnapshot();
        }

        [DllImport("RyujinxHelper.framework/RyujinxHelper", CallingConvention = CallingConvention.Cdecl)]
        public static extern void TriggerCallbackWithData(string cIdentifier, IntPtr data,  UIntPtr dataLength);

        public void Rumble(float lowFrequency, float highFrequency, uint durationMs)
        {
            var rumbleData = new RumbleData
            {
                LowFrequency = lowFrequency,
                HighFrequency = highFrequency,
                DurationMs = durationMs
            };

            int size = Marshal.SizeOf(typeof(RumbleData));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(rumbleData, ptr, false);

            try
            {
                TriggerCallbackWithData($"rumble-{Id}", ptr, (UIntPtr)size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void Dispose()
        {
            IsConnected = false;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RumbleData
    {
        public float LowFrequency;
        public float HighFrequency;
        public uint DurationMs;
    }

}