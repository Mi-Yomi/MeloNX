using CommandLine;
using Ryujinx.Common.Configuration;
using Ryujinx.HLE.HOS.SystemState;

namespace Ryujinx.Library
{
    public class Options
    {
        public Options() { }
        // General

        [Option("root-data-dir", Required = false, HelpText = "Set the custom folder path for Ryujinx data.")]
        public string BaseDataDir { get; set; }

        [Option("profile", Required = false, HelpText = "Set the user profile to launch the game with.")]
        public string UserProfile { get; set; }

        [Option("display-id", Required = false, Default = 0, HelpText = "Set the display to use - especially helpful for fullscreen mode. [0-n]")]
        public int DisplayId { get; set; }

        [Option("fullscreen", Required = false, Default = false, HelpText = "Launch the game in fullscreen mode.")]
        public bool IsFullscreen { get; set; }

        [Option("exclusive-fullscreen", Required = false, Default = false, HelpText = "Launch the game in exclusive fullscreen mode.")]
        public bool IsExclusiveFullscreen { get; set; }

        [Option("exclusive-fullscreen-width", Required = false, Default = 1920, HelpText = "Set horizontal resolution for exclusive fullscreen mode.")]
        public int ExclusiveFullscreenWidth { get; set; }

        [Option("exclusive-fullscreen-height", Required = false, Default = 1080, HelpText = "Set vertical resolution for exclusive fullscreen mode.")]
        public int ExclusiveFullscreenHeight { get; set; }

        // Host Information

        [Option("device-model", Required = false, HelpText = "Set the current iDevice Model")]
        public string DeviceModel { get; set; }

        [Option("has-memory-entitlement", Required = false, HelpText = "If the increased memory entitlement exists.")]
        public bool MemoryEnt { get; set; }

        [Option("device-display-name", Required = false, HelpText = "Set the current iDevice display name.")]
        public string DisplayName { get; set; }

        // Input

        [Option("correct-controller", Required = false, Default = false, HelpText = "Makes the on-screen controller (iOS) buttons correspond to what they show.")]
        public bool OnScreenCorrespond { get; set; }

        [Option("input-profile-1", Required = false, HelpText = "Set the input profile in use for Player 1.")]
        public string InputProfile1Name { get; set; }

        [Option("input-profile-2", Required = false, HelpText = "Set the input profile in use for Player 2.")]
        public string InputProfile2Name { get; set; }

        [Option("input-profile-3", Required = false, HelpText = "Set the input profile in use for Player 3.")]
        public string InputProfile3Name { get; set; }

        [Option("input-profile-4", Required = false, HelpText = "Set the input profile in use for Player 4.")]
        public string InputProfile4Name { get; set; }

        [Option("input-profile-5", Required = false, HelpText = "Set the input profile in use for Player 5.")]
        public string InputProfile5Name { get; set; }

        [Option("input-profile-6", Required = false, HelpText = "Set the input profile in use for Player 6.")]
        public string InputProfile6Name { get; set; }

        [Option("input-profile-7", Required = false, HelpText = "Set the input profile in use for Player 7.")]
        public string InputProfile7Name { get; set; }

        [Option("input-profile-8", Required = false, HelpText = "Set the input profile in use for Player 8.")]
        public string InputProfile8Name { get; set; }

        [Option("input-profile-handheld", Required = false, HelpText = "Set the input profile in use for the Handheld Player.")]
        public string InputProfileHandheldName { get; set; }
        

        [Option("controller-type-1", Required = false, HelpText = "Set the controller type in use for Player 1.")]
        public Common.Configuration.Hid.ControllerType controllerType1 { get; set; }

        [Option("controller-type-2", Required = false, HelpText = "Set the controller type in use for Player 2.")]
        public Common.Configuration.Hid.ControllerType controllerType2 { get; set; }

        [Option("controller-type-3", Required = false, HelpText = "Set the controller type in use for Player 3.")]
        public Common.Configuration.Hid.ControllerType controllerType3 { get; set; }

        [Option("controller-type-4", Required = false, HelpText = "Set the controller type in use for Player 4.")]
        public Common.Configuration.Hid.ControllerType controllerType4 { get; set; }

        [Option("controller-type-5", Required = false, HelpText = "Set the controller type in use for Player 5.")]
        public Common.Configuration.Hid.ControllerType controllerType5 { get; set; }

        [Option("controller-type-6", Required = false, HelpText = "Set the controller type in use for Player 6.")]
        public Common.Configuration.Hid.ControllerType controllerType6 { get; set; }

        [Option("controller-type-7", Required = false, HelpText = "Set the controller type in use for Player 7.")]
        public Common.Configuration.Hid.ControllerType controllerType7 { get; set; }

        [Option("controller-type-8", Required = false, HelpText = "Set the controller type in use for Player 8.")]
        public Common.Configuration.Hid.ControllerType controllerType8 { get; set; }

        // ControllerType

        [Option("input-id-1", Required = false, HelpText = "Set the input id in use for Player 1.")]
        public string InputId1 { get; set; }

        [Option("input-id-2", Required = false, HelpText = "Set the input id in use for Player 2.")]
        public string InputId2 { get; set; }

        [Option("input-id-3", Required = false, HelpText = "Set the input id in use for Player 3.")]
        public string InputId3 { get; set; }

        [Option("input-id-4", Required = false, HelpText = "Set the input id in use for Player 4.")]
        public string InputId4 { get; set; }

        [Option("input-id-5", Required = false, HelpText = "Set the input id in use for Player 5.")]
        public string InputId5 { get; set; }

        [Option("input-id-6", Required = false, HelpText = "Set the input id in use for Player 6.")]
        public string InputId6 { get; set; }

        [Option("input-id-7", Required = false, HelpText = "Set the input id in use for Player 7.")]
        public string InputId7 { get; set; }

        [Option("input-id-8", Required = false, HelpText = "Set the input id in use for Player 8.")]
        public string InputId8 { get; set; }

        [Option("input-id-handheld", Required = false, HelpText = "Set the input id in use for the Handheld Player.")]
        public string InputIdHandheld { get; set; }


        [Option("input-dsu-server-1", Required = false, HelpText = "Set the input DSU server:port in use for Player 1.")]
        public string InputDSUServer1 { get; set; }

        [Option("input-dsu-server-2", Required = false, HelpText = "Set the input DSU server:port in use for Player 2.")]
        public string InputDSUServer2 { get; set; }

        [Option("input-dsu-server-3", Required = false, HelpText = "Set the input DSU server:port in use for Player 3.")]
        public string InputDSUServer3 { get; set; }

        [Option("input-dsu-server-4", Required = false, HelpText = "Set the input DSU server:port in use for Player 4.")]
        public string InputDSUServer4 { get; set; }

        [Option("input-dsu-server-5", Required = false, HelpText = "Set the input DSU server:port in use for Player 5.")]
        public string InputDSUServer5 { get; set; }

        [Option("input-dsu-server-6", Required = false, HelpText = "Set the input DSU server:port in use for Player 6.")]
        public string InputDSUServer6 { get; set; }

        [Option("input-dsu-server-7", Required = false, HelpText = "Set the input DSU server:port in use for Player 7.")]
        public string InputDSUServer7 { get; set; }

        [Option("input-dsu-server-8", Required = false, HelpText = "Set the input DSU server:port in use for Player 8.")]
        public string InputDSUServer8 { get; set; }

        [Option("input-dsu-server-handheld", Required = false, HelpText = "Set the input DSU server:port in use for the Handheld Player.")]
        public string InputDSUServerHandheld { get; set; }

        [Option("enable-keyboard", Required = false, Default = false, HelpText = "Enable or disable keyboard support (Independent from controllers binding).")]
        public bool EnableKeyboard { get; set; }

        [Option("enable-mouse", Required = false, Default = false, HelpText = "Enable or disable mouse support.")]
        public bool EnableMouse { get; set; }

        [Option("hide-cursor", Required = false, Default = HideCursorMode.OnIdle, HelpText = "Change when the cursor gets hidden.")]
        public HideCursorMode HideCursorMode { get; set; }

        [Option("list-input-profiles", Required = false, HelpText = "List inputs profiles.")]
        public bool ListInputProfiles { get; set; }

        [Option("list-inputs-ids", Required = false, HelpText = "List inputs ids.")]
        public bool ListInputIds { get; set; }

        // System

        [Option("disable-ptc", Required = false, HelpText = "Disables profiled persistent translation cache.")]
        public bool DisablePTC { get; set; }

        [Option("enable-internet-connection", Required = false, Default = false, HelpText = "Enables guest Internet connection.")]
        public bool EnableInternetAccess { get; set; }

        [Option("disable-fs-integrity-checks", Required = false, HelpText = "Disables integrity checks on Game content files.")]
        public bool DisableFsIntegrityChecks { get; set; }

        [Option("fs-global-access-log-mode", Required = false, Default = 0, HelpText = "Enables FS access log output to the console.")]
        public int FsGlobalAccessLogMode { get; set; }

        [Option("disable-vsync", Required = false, HelpText = "Disables Vertical Sync.")]
        public bool DisableVSync { get; set; }

        [Option("disable-shader-cache", Required = false, HelpText = "Disables Shader cache.")]
        public bool DisableShaderCache { get; set; }

        [Option("enable-texture-recompression", Required = false, Default = false, HelpText = "Enables Texture recompression.")]
        public bool EnableTextureRecompression { get; set; }

        [Option("disable-docked-mode", Required = false, HelpText = "Disables Docked Mode.")]
        public bool DisableDockedMode { get; set; }

        [Option("system-language", Required = false, Default = SystemLanguage.AmericanEnglish, HelpText = "Change System Language.")]
        public SystemLanguage SystemLanguage { get; set; }

        [Option("system-region", Required = false, Default = RegionCode.USA, HelpText = "Change System Region.")]
        public RegionCode SystemRegion { get; set; }

        [Option("system-timezone", Required = false, Default = "UTC", HelpText = "Change System TimeZone.")]
        public string SystemTimeZone { get; set; }

        [Option("system-time-offset", Required = false, Default = 0, HelpText = "Change System Time Offset in seconds.")]
        public long SystemTimeOffset { get; set; }

        [Option("memory-manager-mode", Required = false, Default = MemoryManagerMode.HostMappedUnsafe, HelpText = "The selected memory manager mode.")]
        public MemoryManagerMode MemoryManagerMode { get; set; }

        [Option("audio-volume", Required = false, Default = 1.0f, HelpText = "The audio level (0 to 1).")]
        public float AudioVolume { get; set; }

		[Option("use-hypervisor", Required = false, Default = false, HelpText = "Uses Hypervisor over JIT if available.")]
        public bool UseHypervisor { get; set; }

        [Option("enable-ldn-mitm", Required = false, Default = false, HelpText = "Enables the ldn mitm mode, allowing for local wireless play.")]
        public bool ldnMitm { get; set; }

        [Option("lan-interface-id", Required = false, Default = "0", HelpText = "GUID for the network interface used by LAN.")]
        public string MultiplayerLanInterfaceId { get; set; }

        // Logging

        [Option("disable-file-logging", Required = false, Default = false, HelpText = "Disables logging to a file on disk.")]
        public bool DisableFileLog { get; set; }

        [Option("enable-debug-logs", Required = false, Default = false, HelpText = "Enables printing debug log messages.")]
        public bool LoggingEnableDebug { get; set; }

        [Option("disable-stub-logs", Required = false, HelpText = "Disables printing stub log messages.")]
        public bool LoggingDisableStub { get; set; }

        [Option("disable-info-logs", Required = false, HelpText = "Disables printing info log messages.")]
        public bool LoggingDisableInfo { get; set; }

        [Option("disable-warning-logs", Required = false, HelpText = "Disables printing warning log messages.")]
        public bool LoggingDisableWarning { get; set; }

        [Option("disable-error-logs", Required = false, HelpText = "Disables printing error log messages.")]
        public bool LoggingEnableError { get; set; }

        [Option("enable-trace-logs", Required = false, Default = false, HelpText = "Enables printing trace log messages.")]
        public bool LoggingEnableTrace { get; set; }

        [Option("disable-guest-logs", Required = false, HelpText = "Disables printing guest log messages.")]
        public bool LoggingDisableGuest { get; set; }

        [Option("enable-fs-access-logs", Required = false, Default = false, HelpText = "Enables printing FS access log messages.")]
        public bool LoggingEnableFsAccessLog { get; set; }

        [Option("graphics-debug-level", Required = false, Default = GraphicsDebugLevel.None, HelpText = "Change Graphics API debug log level.")]
        public GraphicsDebugLevel LoggingGraphicsDebugLevel { get; set; }

        // Graphics

        [Option("resolution-scale", Required = false, Default = 1, HelpText = "Resolution Scale. A floating point scale applied to applicable render targets.")]
        public float ResScale { get; set; }

        [Option("max-anisotropy", Required = false, Default = -1, HelpText = "Max Anisotropy. Values range from 0 - 16. Set to -1 to let the game decide.")]
        public float MaxAnisotropy { get; set; }

        [Option("aspect-ratio", Required = false, Default = AspectRatio.Fixed16x9, HelpText = "Aspect Ratio applied to the renderer window.")]
        public AspectRatio AspectRatio { get; set; }

        [Option("backend-threading", Required = false, Default = BackendThreading.On, HelpText = "Whether or not backend threading is enabled. The \"Auto\" setting will determine whether threading should be enabled at runtime.")]
        public BackendThreading BackendThreading { get; set; }

        [Option("disable-macro-hle", Required = false, HelpText = "Disables high-level emulation of Macro code. Leaving this enabled improves performance but may cause graphical glitches in some games.")]
        public bool DisableMacroHLE { get; set; }

        [Option("graphics-shaders-dump-path", Required = false, HelpText = "Dumps shaders in this local directory. (Developer only)")]
        public string GraphicsShadersDumpPath { get; set; }

        [Option("graphics-backend", Required = false, Default = GraphicsBackend.OpenGl, HelpText = "Change Graphics Backend to use.")]
        public GraphicsBackend GraphicsBackend { get; set; }

        [Option("preferred-gpu-vendor", Required = false, Default = "", HelpText = "When using the Vulkan backend, prefer using the GPU from the specified vendor.")]
        public string PreferredGPUVendor { get; set; }

        [Option("anti-aliasing", Required = false, Default = AntiAliasing.None, HelpText = "Set the type of anti aliasing being used. [None|Fxaa|SmaaLow|SmaaMedium|SmaaHigh|SmaaUltra]")]
        public AntiAliasing AntiAliasing { get; set; }

        [Option("scaling-filter", Required = false, Default = ScalingFilter.Bilinear, HelpText = "Set the scaling filter. [Bilinear|Nearest|Fsr]")]
        public ScalingFilter ScalingFilter { get; set; }

        [Option("scaling-filter-level", Required = false, Default = 0, HelpText = "Set the scaling filter intensity (currently only applies to FSR). [0-100]")]
        public int ScalingFilterLevel { get; set; }

        // Hacks

        [Option("expand-ram", Required = false, Default = false, HelpText = "Expands the RAM amount on the emulated system from 4GiB to 8GiB.")]
        public bool ExpandRAM { get; set; }

        [Option("ignore-missing-services", Required = false, Default = false, HelpText = "Enable ignoring missing services.")]
        public bool IgnoreMissingServices { get; set; }

        // Values

        [Value(0, MetaName = "input", HelpText = "Input to load.", Required = true)]
        public string InputPath { get; set; }
    }

      public unsafe struct OptionsNative
    {
        public byte*  BaseDataDir;
        public byte*  UserProfile;
        public int    DisplayId;
        public byte   IsFullscreen;
        public byte   IsExclusiveFullscreen;
        public int    ExclusiveFullscreenWidth;
        public int    ExclusiveFullscreenHeight;

        public byte*  DeviceModel;
        public byte   MemoryEnt;
        public byte*  DisplayName;

        public byte   OnScreenCorrespond;

        public byte*  InputProfile1Name;
        public byte*  InputProfile2Name;
        public byte*  InputProfile3Name;
        public byte*  InputProfile4Name;
        public byte*  InputProfile5Name;
        public byte*  InputProfile6Name;
        public byte*  InputProfile7Name;
        public byte*  InputProfile8Name;
        public byte*  InputProfileHandheldName;

        public Common.Configuration.Hid.ControllerType ControllerType1;
        public Common.Configuration.Hid.ControllerType ControllerType2;
        public Common.Configuration.Hid.ControllerType ControllerType3;
        public Common.Configuration.Hid.ControllerType ControllerType4;
        public Common.Configuration.Hid.ControllerType ControllerType5;
        public Common.Configuration.Hid.ControllerType ControllerType6;
        public Common.Configuration.Hid.ControllerType ControllerType7;
        public Common.Configuration.Hid.ControllerType ControllerType8;

        public byte*  InputId1;
        public byte*  InputId2;
        public byte*  InputId3;
        public byte*  InputId4;
        public byte*  InputId5;
        public byte*  InputId6;
        public byte*  InputId7;
        public byte*  InputId8;
        public byte*  InputIdHandheld;

        public byte*  InputDSUServer1;
        public byte*  InputDSUServer2;
        public byte*  InputDSUServer3;
        public byte*  InputDSUServer4;
        public byte*  InputDSUServer5;
        public byte*  InputDSUServer6;
        public byte*  InputDSUServer7;
        public byte*  InputDSUServer8;
        public byte*  InputDSUServerHandheld;

        public byte   EnableKeyboard;
        public byte   EnableMouse;
        public HideCursorMode HideCursorMode;
        public byte   ListInputProfiles;
        public byte   ListInputIds;

        public byte   DisablePTC;
        public byte   EnableInternetAccess;
        public byte   DisableFsIntegrityChecks;
        public int    FsGlobalAccessLogMode;
        public byte   DisableVSync;
        public byte   DisableShaderCache;
        public byte   EnableTextureRecompression;
        public byte   DisableDockedMode;
        public SystemLanguage SystemLanguage;
        public RegionCode     SystemRegion;
        public byte*          SystemTimeZone;
        public long           SystemTimeOffset;
        public MemoryManagerMode MemoryManagerMode;
        public float  AudioVolume;
        public byte   UseHypervisor;
        public byte   LdnMitm;
        public byte*  MultiplayerLanInterfaceId;

        public byte   DisableFileLog;
        public byte   LoggingEnableDebug;
        public byte   LoggingDisableStub;
        public byte   LoggingDisableInfo;
        public byte   LoggingDisableWarning;
        public byte   LoggingEnableError;
        public byte   LoggingEnableTrace;
        public byte   LoggingDisableGuest;
        public byte   LoggingEnableFsAccessLog;
        public GraphicsDebugLevel LoggingGraphicsDebugLevel;

        public float  ResScale;
        public float  MaxAnisotropy;
        public AspectRatio       AspectRatio;
        public BackendThreading  BackendThreading;
        public byte   DisableMacroHLE;
        public byte*  GraphicsShadersDumpPath;
        public GraphicsBackend   GraphicsBackend;
        public byte*  PreferredGPUVendor;
        public AntiAliasing  AntiAliasing;
        public ScalingFilter ScalingFilter;
        public int    ScalingFilterLevel;

        public byte   ExpandRAM;
        public byte   IgnoreMissingServices;

        public byte*  InputPath;
    }

    public static unsafe class OptionsNativeHelper
    {
        private static byte* AllocUtf8(string s)
        {
            if (s == null) return null;
            int byteCount = System.Text.Encoding.UTF8.GetByteCount(s) + 1;
            byte* ptr = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)byteCount);
            fixed (char* chars = s)
            {
                System.Text.Encoding.UTF8.GetBytes(chars, s.Length, ptr, byteCount);
            }
            ptr[byteCount - 1] = 0;
            return ptr;
        }

        private static byte B(bool b) => b ? (byte)1 : (byte)0;

        public static OptionsNative* ToNative(Options opts)
        {
            OptionsNative* n = (OptionsNative*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(OptionsNative));

            n->BaseDataDir              = AllocUtf8(opts.BaseDataDir);
            n->UserProfile              = AllocUtf8(opts.UserProfile);
            n->DisplayId                = opts.DisplayId;
            n->IsFullscreen             = B(opts.IsFullscreen);
            n->IsExclusiveFullscreen    = B(opts.IsExclusiveFullscreen);
            n->ExclusiveFullscreenWidth = opts.ExclusiveFullscreenWidth;
            n->ExclusiveFullscreenHeight= opts.ExclusiveFullscreenHeight;

            n->DeviceModel  = AllocUtf8(opts.DeviceModel);
            n->MemoryEnt    = B(opts.MemoryEnt);
            n->DisplayName  = AllocUtf8(opts.DisplayName);

            n->OnScreenCorrespond       = B(opts.OnScreenCorrespond);
            n->InputProfile1Name        = AllocUtf8(opts.InputProfile1Name);
            n->InputProfile2Name        = AllocUtf8(opts.InputProfile2Name);
            n->InputProfile3Name        = AllocUtf8(opts.InputProfile3Name);
            n->InputProfile4Name        = AllocUtf8(opts.InputProfile4Name);
            n->InputProfile5Name        = AllocUtf8(opts.InputProfile5Name);
            n->InputProfile6Name        = AllocUtf8(opts.InputProfile6Name);
            n->InputProfile7Name        = AllocUtf8(opts.InputProfile7Name);
            n->InputProfile8Name        = AllocUtf8(opts.InputProfile8Name);
            n->InputProfileHandheldName = AllocUtf8(opts.InputProfileHandheldName);

            n->ControllerType1 = opts.controllerType1;
            n->ControllerType2 = opts.controllerType2;
            n->ControllerType3 = opts.controllerType3;
            n->ControllerType4 = opts.controllerType4;
            n->ControllerType5 = opts.controllerType5;
            n->ControllerType6 = opts.controllerType6;
            n->ControllerType7 = opts.controllerType7;
            n->ControllerType8 = opts.controllerType8;

            n->InputId1        = AllocUtf8(opts.InputId1);
            n->InputId2        = AllocUtf8(opts.InputId2);
            n->InputId3        = AllocUtf8(opts.InputId3);
            n->InputId4        = AllocUtf8(opts.InputId4);
            n->InputId5        = AllocUtf8(opts.InputId5);
            n->InputId6        = AllocUtf8(opts.InputId6);
            n->InputId7        = AllocUtf8(opts.InputId7);
            n->InputId8        = AllocUtf8(opts.InputId8);
            n->InputIdHandheld = AllocUtf8(opts.InputIdHandheld);

            n->InputDSUServer1        = AllocUtf8(opts.InputDSUServer1);
            n->InputDSUServer2        = AllocUtf8(opts.InputDSUServer2);
            n->InputDSUServer3        = AllocUtf8(opts.InputDSUServer3);
            n->InputDSUServer4        = AllocUtf8(opts.InputDSUServer4);
            n->InputDSUServer5        = AllocUtf8(opts.InputDSUServer5);
            n->InputDSUServer6        = AllocUtf8(opts.InputDSUServer6);
            n->InputDSUServer7        = AllocUtf8(opts.InputDSUServer7);
            n->InputDSUServer8        = AllocUtf8(opts.InputDSUServer8);
            n->InputDSUServerHandheld = AllocUtf8(opts.InputDSUServerHandheld);

            n->EnableKeyboard    = B(opts.EnableKeyboard);
            n->EnableMouse       = B(opts.EnableMouse);
            n->HideCursorMode    = opts.HideCursorMode;
            n->ListInputProfiles = B(opts.ListInputProfiles);
            n->ListInputIds      = B(opts.ListInputIds);

            n->DisablePTC               = B(opts.DisablePTC);
            n->EnableInternetAccess     = B(opts.EnableInternetAccess);
            n->DisableFsIntegrityChecks = B(opts.DisableFsIntegrityChecks);
            n->FsGlobalAccessLogMode    = opts.FsGlobalAccessLogMode;
            n->DisableVSync             = B(opts.DisableVSync);
            n->DisableShaderCache       = B(opts.DisableShaderCache);
            n->EnableTextureRecompression = B(opts.EnableTextureRecompression);
            n->DisableDockedMode        = B(opts.DisableDockedMode);
            n->SystemLanguage           = opts.SystemLanguage;
            n->SystemRegion             = opts.SystemRegion;
            n->SystemTimeZone           = AllocUtf8(opts.SystemTimeZone);
            n->SystemTimeOffset         = opts.SystemTimeOffset;
            n->MemoryManagerMode        = opts.MemoryManagerMode;
            n->AudioVolume              = opts.AudioVolume;
            n->UseHypervisor            = B(opts.UseHypervisor);
            n->LdnMitm                  = B(opts.ldnMitm);
            n->MultiplayerLanInterfaceId = AllocUtf8(opts.MultiplayerLanInterfaceId);

            n->DisableFileLog            = B(opts.DisableFileLog);
            n->LoggingEnableDebug        = B(opts.LoggingEnableDebug);
            n->LoggingDisableStub        = B(opts.LoggingDisableStub);
            n->LoggingDisableInfo        = B(opts.LoggingDisableInfo);
            n->LoggingDisableWarning     = B(opts.LoggingDisableWarning);
            n->LoggingEnableError        = B(opts.LoggingEnableError);
            n->LoggingEnableTrace        = B(opts.LoggingEnableTrace);
            n->LoggingDisableGuest       = B(opts.LoggingDisableGuest);
            n->LoggingEnableFsAccessLog  = B(opts.LoggingEnableFsAccessLog);
            n->LoggingGraphicsDebugLevel = opts.LoggingGraphicsDebugLevel;

            n->ResScale               = opts.ResScale;
            n->MaxAnisotropy          = opts.MaxAnisotropy;
            n->AspectRatio            = opts.AspectRatio;
            n->BackendThreading       = opts.BackendThreading;
            n->DisableMacroHLE        = B(opts.DisableMacroHLE);
            n->GraphicsShadersDumpPath= AllocUtf8(opts.GraphicsShadersDumpPath);
            n->GraphicsBackend        = opts.GraphicsBackend;
            n->PreferredGPUVendor     = AllocUtf8(opts.PreferredGPUVendor);
            n->AntiAliasing           = opts.AntiAliasing;
            n->ScalingFilter          = opts.ScalingFilter;
            n->ScalingFilterLevel     = opts.ScalingFilterLevel;

            n->ExpandRAM             = B(opts.ExpandRAM);
            n->IgnoreMissingServices = B(opts.IgnoreMissingServices);

            n->InputPath = AllocUtf8(opts.InputPath);

            return n;
        }

        public static void Free(OptionsNative* n)
        {
            if (n == null) return;

            System.Runtime.InteropServices.NativeMemory.Free(n->BaseDataDir);
            System.Runtime.InteropServices.NativeMemory.Free(n->UserProfile);
            System.Runtime.InteropServices.NativeMemory.Free(n->DeviceModel);
            System.Runtime.InteropServices.NativeMemory.Free(n->DisplayName);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputProfile1Name);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputProfile2Name);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputProfile3Name);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputProfile4Name);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputProfile5Name);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputProfile6Name);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputProfile7Name);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputProfile8Name);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputProfileHandheldName);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputId1);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputId2);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputId3);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputId4);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputId5);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputId6);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputId7);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputId8);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputIdHandheld);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputDSUServer1);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputDSUServer2);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputDSUServer3);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputDSUServer4);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputDSUServer5);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputDSUServer6);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputDSUServer7);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputDSUServer8);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputDSUServerHandheld);
            System.Runtime.InteropServices.NativeMemory.Free(n->SystemTimeZone);
            System.Runtime.InteropServices.NativeMemory.Free(n->MultiplayerLanInterfaceId);
            System.Runtime.InteropServices.NativeMemory.Free(n->GraphicsShadersDumpPath);
            System.Runtime.InteropServices.NativeMemory.Free(n->PreferredGPUVendor);
            System.Runtime.InteropServices.NativeMemory.Free(n->InputPath);

            System.Runtime.InteropServices.NativeMemory.Free(n);
        }
    }
}
