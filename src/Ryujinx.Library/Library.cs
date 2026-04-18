using CommandLine;
using LibHac.Tools.FsSystem;
using Ryujinx.Audio.Backends.SDL3;
using Ryujinx.Audio.Backends.Apple;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Configuration.Hid.Controller.Motion;
using Ryujinx.Common.Configuration.Hid.Keyboard;
using Ryujinx.Common.GraphicsDriver;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using Ryujinx.Common.SystemInterop;
using Ryujinx.Common.Utilities;
using Ryujinx.Cpu;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.OpenGL;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.Graphics.Vulkan.MoltenVK;
using Ryujinx.HLE;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using Ryujinx.Input.SDL3;
using SDL;
using static SDL.SDL3;
using SDL3Type = SDL.SDL3;
using Ryujinx.SDL3.Common;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using ConfigGamepadInputId = Ryujinx.Common.Configuration.Hid.Controller.GamepadInputId;
using ConfigStickInputId = Ryujinx.Common.Configuration.Hid.Controller.StickInputId;
using Key = Ryujinx.Common.Configuration.Hid.Key;
using Ryujinx.HLE.HOS.SystemState;
using LibHac.Common.Keys;
using LibHac.Common;
using LibHac.Ns;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Fs;
using Path = System.IO.Path;
using Ryujinx.Common.Configuration.Multiplayer;
using Ryujinx.HLE.Loaders.Npdm;
using System.Globalization;
using System.Text;
using LibHac.Ncm;
using Microsoft.Win32.SafeHandles;
using System.Text.RegularExpressions;
using System.Runtime;
using System.Linq;
using System.Threading.Tasks;
// using Ryujinx.Input.Native;


namespace Ryujinx.Library 
{
    class Library
    {
        private static VirtualFileSystem _virtualFileSystem;
        private static ContentManager _contentManager;
        private static AccountManager _accountManager;
        private static LibHacHorizonManager _libHacHorizonManager;
        private static UserChannelPersistence _userChannelPersistence;
        private static InputManager _inputManager;
        private static Switch _emulationContext;
        private static WindowBase _window;
        private static WindowsMultimediaTimerResolution _windowsMultimediaTimerResolution;
        private static List<InputConfig> _inputConfiguration;
        private static bool _enableKeyboard;
        private static bool _enableMouse;
        private static nint nativeMetalLayer = nint.Zero;
        private static readonly Lock metalLayerLock = new();

        void wow() {
            // :3
        }

        [UnmanagedCallersOnly(EntryPoint = "main_ryujinx_sdl")]
        public static unsafe int MainExternal(int argCount, IntPtr* pArgs)
        {
            string[] args = new string[argCount];

            try
            {
                for (int i = 0; i < argCount; i++)
                {
                    args[i] = Marshal.PtrToStringAnsi(pArgs[i]);

                    Console.WriteLine(args[i]);
                }

                Main(args);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                return -1;
            }

            return 0;
        }

        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
            .WithParsed(Load)
            .WithNotParsed(errors => errors.Output());
        }

        [UnmanagedCallersOnly(EntryPoint = "initialize")]
        public static unsafe void Initialize()
        {
            AppDataManager.Initialize(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

            Silk.NET.Core.Loader.SearchPathContainer.Platform = Silk.NET.Core.Loader.UnderlyingPlatform.MacOS;

            if (_virtualFileSystem == null)
            {
                _virtualFileSystem = VirtualFileSystem.CreateInstance();
            }

            if (_libHacHorizonManager == null)
            {
                _libHacHorizonManager = new LibHacHorizonManager();
                _libHacHorizonManager.InitializeFsServer(_virtualFileSystem);
                _libHacHorizonManager.InitializeArpServer();
                _libHacHorizonManager.InitializeBcatServer();
                _libHacHorizonManager.InitializeSystemClients();
            }

            if (_contentManager == null)
            {
                _contentManager = new ContentManager(_virtualFileSystem);
            }
            
            if (_accountManager == null)
            {
                _accountManager = new AccountManager(_libHacHorizonManager.RyujinxClient);
            }

            // :3
            NativeLibrary.SetDllImportResolver(typeof(SDL3Type).Assembly, (_, assembly, path) => NativeLibrary.Load("@rpath/SDL3.framework/SDL3", assembly, path));

            _inputManager = new InputManager(new SDL3KeyboardDriver(), new SDL3GamepadDriver());
            _inputConfiguration = new List<InputConfig>();
            _enableKeyboard = true;
            _enableMouse = false;

            var config = new StandardKeyboardInputConfig
            {
                Version = InputConfig.CurrentVersion,
                Backend = InputBackendType.WindowKeyboard,
                Id = "0",
                ControllerType = ControllerType.JoyconPair,
                LeftJoycon = new LeftJoyconCommonConfig<Key>
                {
                    DpadUp = Key.Up, DpadDown = Key.Down, DpadLeft = Key.Left, DpadRight = Key.Right,
                    ButtonMinus = Key.BracketLeft, ButtonL = Key.E, ButtonZl = Key.Q,
                    ButtonSl = Key.Unbound, ButtonSr = Key.Unbound,
                },
                LeftJoyconStick = new JoyconConfigKeyboardStick<Key>
                {
                    StickUp = Key.W, StickDown = Key.S, StickLeft = Key.A, StickRight = Key.D, StickButton = Key.F,
                },
                RightJoycon = new RightJoyconCommonConfig<Key>
                {
                    ButtonA = Key.Z, ButtonB = Key.X, ButtonX = Key.C, ButtonY = Key.V,
                    ButtonPlus = Key.BracketRight, ButtonR = Key.U, ButtonZr = Key.O,
                    ButtonSl = Key.Unbound, ButtonSr = Key.Unbound,
                },
                RightJoyconStick = new JoyconConfigKeyboardStick<Key>
                {
                    StickUp = Key.I, StickDown = Key.K, StickLeft = Key.J, StickRight = Key.L, StickButton = Key.H,
                },
            };

            config.PlayerIndex = PlayerIndex.Player1;
            _inputConfiguration.Add(config);

            AutoResetEvent invoked = new(false);

            SDL3Driver.MainThreadDispatcher = action =>
            {
                invoked.Reset();

                WindowBase.QueueMainThreadAction(() =>
                {
                    action();

                    invoked.Set();
                });

                invoked.WaitOne();
            };
        }

        [UnmanagedCallersOnly(EntryPoint = "set_native_window")]
        public static unsafe void SetNativeWindow(nint layer) {
            lock (metalLayerLock) {
                nativeMetalLayer = layer;
            }
        }

        public static nint GetNativeMetalLayer()
        {
            lock (metalLayerLock)
            {
                return nativeMetalLayer;
            }
        }

        static void Load(Options option)
        {
            _libHacHorizonManager = new LibHacHorizonManager();
            _libHacHorizonManager.InitializeFsServer(_virtualFileSystem);
            _libHacHorizonManager.InitializeArpServer();
            _libHacHorizonManager.InitializeBcatServer();
            _libHacHorizonManager.InitializeSystemClients();

            _accountManager = new AccountManager(_libHacHorizonManager.RyujinxClient, option.UserProfile);
            _userChannelPersistence = new UserChannelPersistence();

            GraphicsConfig.EnableShaderCache = !option.DisableShaderCache;
            GraphicsConfig.EnableMacroJit = false;
            GraphicsConfig.EnableMacroHLE = option.DisableMacroHLE;

            Logger.SetEnable(LogLevel.Debug, option.LoggingEnableDebug);
            Logger.SetEnable(LogLevel.Stub, !option.LoggingDisableStub);
            Logger.SetEnable(LogLevel.Info, !option.LoggingDisableInfo);
            Logger.SetEnable(LogLevel.Warning, !option.LoggingDisableWarning);
            Logger.SetEnable(LogLevel.Error, option.LoggingEnableError);
            Logger.SetEnable(LogLevel.Trace, option.LoggingEnableTrace);
            Logger.SetEnable(LogLevel.Guest, !option.LoggingDisableGuest);
            Logger.SetEnable(LogLevel.AccessLog, option.LoggingEnableFsAccessLog);

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            {
                if (option.GraphicsBackend == GraphicsBackend.OpenGl)
                {
                    option.GraphicsBackend = GraphicsBackend.Vulkan;
                    Logger.Warning?.Print(LogClass.Application, "OpenGL is not supported on Apple platforms, switching to Vulkan!");
                }
            }

            DriverUtilities.InitDriverConfig(option.BackendThreading == BackendThreading.Off);
            _virtualFileSystem.ReloadKeySet();

            while (true)
            {
                LoadApplication(option);

                if (_userChannelPersistence.PreviousIndex == -1 || !_userChannelPersistence.ShouldRestart)
                    break;

                _userChannelPersistence.ShouldRestart = false;
            }

            _inputManager.Dispose();
        }

        private static WindowBase CreateWindow(Options options)
        {
            return new MoltenVKWindow(_inputManager, options.LoggingGraphicsDebugLevel, options.AspectRatio, options.EnableMouse, options.HideCursorMode);
        }

        private static IRenderer CreateRenderer(Options options, WindowBase window)
        {
            if (options.GraphicsBackend == GraphicsBackend.Vulkan)
            {
                string preferredGpuId = string.Empty;
                Vk api = Vk.GetApi();

                if (!string.IsNullOrEmpty(options.PreferredGPUVendor))
                {
                    string preferredGpuVendor = options.PreferredGPUVendor.ToLowerInvariant();
                    var devices = VulkanRenderer.GetPhysicalDevices(api);
                    foreach (var device in devices)
                    {
                        if (device.Vendor.ToLowerInvariant() == preferredGpuVendor)
                        {
                            preferredGpuId = device.Id;
                            break;
                        }
                    }
                }

                if (window is MoltenVKWindow mvulkanWindow)
                    return new VulkanRenderer(api,
                        (instance, vk) => new SurfaceKHR((ulong)(mvulkanWindow.CreateWindowSurface(instance.Handle))),
                        mvulkanWindow.GetRequiredInstanceExtensions, preferredGpuId);
            }

            return new OpenGLRenderer();
        }

        private static Switch InitializeEmulationContext(WindowBase window, IRenderer renderer, Options options)
        {
            renderer = renderer.TryMakeThreaded(options.BackendThreading);

            bool appleHV;
            if (!OperatingSystem.IsIOSVersionAtLeast(16, 4) && options.UseHypervisor)
                appleHV = true;
            else if (OperatingSystem.IsIOS())
                appleHV = false;
            else
                appleHV = options.UseHypervisor;

            var configuration = new HleConfiguration(
                MemoryConfiguration.MemoryConfiguration4GiB,
                options.SystemLanguage,
                options.SystemRegion,
                Ryujinx.Common.Configuration.VSyncMode.Switch,
                !options.DisableDockedMode,
                !options.DisablePTC,
                ITickSource.RealityTickScalar,
                options.EnableInternetAccess,
                !options.DisableFsIntegrityChecks ? IntegrityCheckLevel.ErrorOnInvalid : IntegrityCheckLevel.None,
                options.FsGlobalAccessLogMode,
                options.SystemTimeOffset,
                options.SystemTimeZone,
                options.MemoryManagerMode,
                options.IgnoreMissingServices,
                options.AspectRatio,
                options.AudioVolume,
                appleHV,
                options.MultiplayerLanInterfaceId,
                options.ldnMitm ? MultiplayerMode.LdnMitm : MultiplayerMode.Disabled,
                false, // MultiplayerDisableP2p
                string.Empty, // Passphrase
                string.Empty, // Server
                false,
                0,
                false,
                0
            ).Configure(
                _virtualFileSystem,
                _libHacHorizonManager,
                _contentManager,
                _accountManager,
                _userChannelPersistence,
                renderer,
                new SDL3HardwareDeviceDriver(),
                window
            );

            return new Switch(configuration);
        }

        private static void SetupProgressHandler()
        {
            if (_emulationContext.Processes.ActiveApplication.DiskCacheLoadState != null)
            {
                _emulationContext.Processes.ActiveApplication.DiskCacheLoadState.StateChanged -= ProgressHandler;
                _emulationContext.Processes.ActiveApplication.DiskCacheLoadState.StateChanged += ProgressHandler;
            }

            _emulationContext.Gpu.ShaderCacheStateChanged -= ProgressHandler;
            _emulationContext.Gpu.ShaderCacheStateChanged += ProgressHandler;
        }

        private static void ProgressHandler<T>(T state, int current, int total) where T : Enum
        {
            string jsonData = state switch
            {
                LoadState => $"[\"PTC\",{current},{total}]",
                ShaderCacheState => $"[\"Shaders\",{current},{total}]",
                _ => throw new ArgumentException($"Unknown Progress Handler type {typeof(T)}"),
            };

            byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonData);
            nint unmanagedPointer = Marshal.AllocHGlobal(jsonBytes.Length);
            try
            {
                Marshal.Copy(jsonBytes, 0, unmanagedPointer, jsonBytes.Length);

                CallbackRegistry.Invoke("ProgressWithPTCorShaderCache", unmanagedPointer, (int)jsonBytes.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(unmanagedPointer);
            }
        }

        private static void ExecutionEntrypoint()
        {
            if (OperatingSystem.IsWindows())
                _windowsMultimediaTimerResolution = new WindowsMultimediaTimerResolution(1);

            DisplaySleep.Prevent();

            _window.Initialize(_emulationContext, _inputConfiguration, _enableKeyboard, _enableMouse);
            _window.Execute();

            _emulationContext.Dispose();
            _window.Dispose();

            if (OperatingSystem.IsWindows())
            {
                _windowsMultimediaTimerResolution?.Dispose();
                _windowsMultimediaTimerResolution = null;
            }
        }

        private static bool LoadApplication(Options options)
        {
            string path = options.InputPath;

            Logger.RestartTime();

            WindowBase window = CreateWindow(options);

            if (window is MoltenVKWindow mvulkanWindow)
                mvulkanWindow.SetNativeWindow(nativeMetalLayer);

            IRenderer renderer = CreateRenderer(options, window);

            _window = window;
            _window.IsFullscreen = options.IsFullscreen;
            _window.DisplayId = options.DisplayId;
            _window.IsExclusiveFullscreen = options.IsExclusiveFullscreen;
            _window.ExclusiveFullscreenWidth = options.ExclusiveFullscreenWidth;
            _window.ExclusiveFullscreenHeight = options.ExclusiveFullscreenHeight;
            _window.AntiAliasing = options.AntiAliasing;
            _window.ScalingFilter = options.ScalingFilter;
            _window.ScalingFilterLevel = options.ScalingFilterLevel;
            renderer.Window?.SetColorSpacePassthrough(true);

            _emulationContext = InitializeEmulationContext(window, renderer, options);

            SystemVersion firmwareVersion = _contentManager.GetCurrentFirmwareVersion();
            Logger.Notice.Print(LogClass.Application, $"Using Firmware Version: {firmwareVersion?.VersionString}");

            bool isFirmwareTitle = false;
            if (path.StartsWith("@SystemContent"))
            {
                path = VirtualFileSystem.SwitchPathToSystemPath(path);
                isFirmwareTitle = true;
            }

            if (Directory.Exists(path))
            {
                string[] romFsFiles = Directory.GetFiles(path, "*.istorage");
                if (romFsFiles.Length == 0)
                    romFsFiles = Directory.GetFiles(path, "*.romfs");

                if (romFsFiles.Length > 0)
                {
                    Logger.Info?.Print(LogClass.Application, "Loading as cart with RomFS.");
                    if (!_emulationContext.LoadCart(path, romFsFiles[0])) { _emulationContext.Dispose(); return false; }
                }
                else
                {
                    Logger.Info?.Print(LogClass.Application, "Loading as cart WITHOUT RomFS.");
                    if (!_emulationContext.LoadCart(path)) { _emulationContext.Dispose(); return false; }
                }
            }
            else if (File.Exists(path))
            {
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".xci":
                        Logger.Info?.Print(LogClass.Application, "Loading as XCI.");
                        if (!_emulationContext.LoadXci(path)) { _emulationContext.Dispose(); return false; }
                        break;
                    case ".nca":
                        Logger.Info?.Print(LogClass.Application, "Loading as NCA.");
                        if (!_emulationContext.LoadNca(path)) { _emulationContext.Dispose(); return false; }
                        break;
                    case ".nsp":
                    case ".pfs0":
                        Logger.Info?.Print(LogClass.Application, "Loading as NSP.");
                        if (!_emulationContext.LoadNsp(path)) { _emulationContext.Dispose(); return false; }
                        break;
                    default:
                        if (isFirmwareTitle)
                        {
                            Logger.Info?.Print(LogClass.Application, "Loading as Firmware Title (NCA).");
                            if (!_emulationContext.LoadNca(path)) { _emulationContext.Dispose(); return false; }
                        }
                        else
                        {
                            Logger.Info?.Print(LogClass.Application, "Loading as Homebrew.");
                            try
                            {
                                if (!_emulationContext.LoadProgram(path)) { _emulationContext.Dispose(); return false; }
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                Logger.Error?.Print(LogClass.Application, "The specified file is not supported by Ryujinx.");
                                _emulationContext.Dispose();
                                return false;
                            }
                        }
                        break;
                }
            }
            else
            {
                Logger.Warning?.Print(LogClass.Application, $"Couldn't load '{options.InputPath}'. Please specify a valid XCI/NCA/NSP/PFS0/NRO file.");
                _emulationContext.Dispose();
                return false;
            }

            SetupProgressHandler();
            ExecutionEntrypoint();
            return true;
        }
    }


}