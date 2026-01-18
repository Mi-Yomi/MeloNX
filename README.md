<p align="center">
    <a href="https://melonx.org">
        <img src="https://melonx.org/static/imgs/MeloNX.svg" alt="MeloNX Logo" width="120">
    </a>
</p>

<h1 align="center">MeloNX</h1>

<p align="center">
    MeloNX enables Nintendo Switch game emulation on iOS using the Ryujinx iOS code base.
</p>

<p align="center">
       MeloNX is an iOS Nintendo Switch emulator based on Ryujinx, written primarily in C#. Designed to bring accurate performance and a user-friendly interface to iOS, MeloNX makes Switch games accessible on Apple devices.
       Developed from the ground up, MeloNX is open-source and available on Github under the <a href="https://github.com/MeloNX-Emu/MeloNX/blob/master/LICENSE.txt" target="_blank">MeloNX license</a>. <br 
</p>

# Compatibility

MeloNX works on iPhone 11 (XS/XR may work but can have issues) and later and iPad 8th Gen and later. Check out the Compatibility on the <a href="https://melonx.org/compatibility/" target="_blank">website</a>.

# Usage

## Paid Certificates are **NOT** supported and we will not give any help when using them.

## FAQ
- MeloNX cannot be Sideloaded normally and requires the use of the following Installation Guide(s).
- [SideStore](https://sidestore.io/) is recommended for Sideloading MeloNX
- Apple ID with free / paid developer account
- MeloNX requires JIT
- Recommended Device: iPhone 15 Pro or newer.
- Low-End Recommended Device: iPhone 13 Pro. 


## Discord Server

We have a discord server!
  - https://discord.gg/HjCDPTpC3W

## How to install

### Paid Developer Account  

#### 1. Sideload MeloNX
Download and install MeloNX using your preferred Apple ID sideloader:
  - [Download MeloNX from Releases](https://git.ryujinx.app/melonx/emu/-/releases)

#### 2. Enable Memory Entitlement
   - Visit [Apple Developer Identifiers](https://developer.apple.com/account/resources/identifiers).  
   - Locate **MeloNX** and enable the following entitlements:  
     - `Increased Memory Limit`  
     - `Increased Debugging Memory Limit`  

#### 3. Reinstall MeloNX
  - Delete existing MeloNX installation
  - Sideload MeloNX again
  - Verify **Increased Memory Limit** is enabled in app
  
#### 4. Setup Files
  - Add Encryption Keys and Firmware using the file picker inside MeloNX
    - If having Issues installing firmware:
      - You can Install firmware and keys from **Ryujinx Desktop** (or forks).  
      - Copy the **bis** and **system** folders  

#### 5. Enable JIT
  - Enable JIT using your preferred method. We recommend [StikDebug](https://apps.apple.com/us/app/stikdebug/id6744045754).

### Free Developer Account (PlumeImpactor, Recommended)

>  [SideStore](https://sidestore.io/) is recommended for an on-device Sideloader, and should be installed before performing this install.

#### 1. Sideload Application
Download and install MeloNX using [PlumeImpactor](https://github.com/khcrysalis/PlumeImpactor/releases) on a computer.
  - [Download **MeloNX** From Releases](https://git.ryujinx.app/melonx/emu/-/releases)
  - Open PlumeImpactor > Click Settings > Click Login
  - Login with the same Apple ID you are using for SideStore (or AltStore). 
  - Import the MeloNX .ipa you downloaded earlier.
  - Plug in your iDevice.
  - Select your iDevice from the dropdown at the top of the window.
  - Click Install.

#### 2. Load Into SideStore
To have MeloNX show inside SideStore (or AltStore), You must re-install it:
  - Open **SideStore** on your iDevice
  - Select the **My Apps** tab > Tap the **+** button.
  - Select the **MeloNX** .ipa (You may need to download it again.)
  - Wait for it to Sideload, then it should show up Inside **SideStore**.
  - Now You can Refresh **MeloNX** and Update it without needing a computer.

#### 4. Setup Files
  - Add Encryption Keys and Firmware using the file picker inside MeloNX
    - If having Issues installing firmware:
      - You can Install firmware and keys from **Ryujinx Desktop** (or forks).  
      - Copy the **bis** and **system** folders  

#### 5. Enable JIT
  - Enable JIT using your preferred method. We recommend [StikDebug](https://apps.apple.com/us/app/stikdebug/id6744045754).

   
### Free Developer Account (On-Device, Legacy)

>  [SideStore](https://sidestore.io/) is recommended to Sideload MeloNX.

***The Entitlement App is **NOT** needed for AltStore Classic***
  - You may skip Step 2 and Step 3

#### 1. Sideload Applications
Download and install both apps using your preferred **APPLE ID** sideloader:
  - **MeloNX**: [Download from Releases](https://git.ryujinx.app/melonx/emu/-/releases)
  - **Entitlement App**: [Download IPA](https://github.com/hugeBlack/GetMoreRam/releases/download/nightly/Entitlement.ipa)

#### 2. Enable Memory Entitlement
> If the Entitlement / GetMoreRam app isn't working correctly, the new PlumeImpactor Method is for you. 
  - Open the **Entitlement app** > **Settings**
  - Sign in with the same Apple ID you used to Sideload MeloNX.
  - Go to **App IDs** > tap **Refresh**
  - Select **MeloNX** (e.g., "com.stossy11.MeloNX.XXXXXX")
  - Tap **Add Increased Memory Limit**

#### 3. Reinstall MeloNX
  - Delete existing MeloNX installation
  - Sideload MeloNX again
  - Verify **Increased Memory Limit** is enabled in app

#### 4. Setup Files
  - Add Encryption Keys and Firmware using the file picker inside MeloNX
    - If having Issues installing firmware:
      - You can Install firmware and keys from **Ryujinx Desktop** (or forks).  
      - Copy the **bis** and **system** folders  

#### 5. Enable JIT
  - Enable JIT using your preferred method. We recommend [StikDebug](https://apps.apple.com/us/app/stikdebug/id6744045754).


### Compile from Source (Xcode)

**NOTE: These Xcode builds are nightly and may have unfinished features.**

1. **Compile Guide**
   - Visit the [guide here](https://git.ryujinx.app/melonx/emu/-/blob/XC-ios-ht/Compile.md?ref_type=heads).

2. **Add Necessary Files**  

If having Issues installing firmware (Make sure your keys are installed first)
   - If needed, install firmware and keys from **Ryujinx Desktop** (or forks).  
   - Copy the **bis** and **system** folders  
   
## Features

- **Audio**

  Audio output is entirely supported, audio input (microphone) isn't supported.
  We use C# wrappers for [OpenAL](https://openal-soft.org/), and [SDL2](https://www.libsdl.org/) & [libsoundio](http://libsound.io/) as fallbacks.

- **CPU**

  The CPU emulator, ARMeilleure, emulates an ARMv8 CPU and currently has support for most 64-bit ARMv8 and some of the ARMv7 (and older) instructions, including partial 32-bit support.
  It translates the ARM code to a custom IR, performs a few optimizations, and turns that into x86 code.
  There are three memory manager options available depending on the user's preference, leveraging both software-based (slower) and host-mapped modes (much faster).
  The fastest option (host, unchecked) is set by default.
  Ryujinx also features an optional Profiled Persistent Translation Cache, which essentially caches translated functions so that they do not need to be translated every time the game loads.
  The net result is a significant reduction in load times (the amount of time between launching a game and arriving at the title screen) for nearly every game.
  NOTE: This feature is enabled by default, You must launch the game at least twice to the title screen or beyond before performance improvements are unlocked on the third launch!
  These improvements are permanent and do not require any extra launches going forward.

- **GPU**

  The GPU emulator emulates the Switch's Maxwell GPU using Metal (via MoltenVK) APIs through a custom build of Silk.NET.

- **Input**

  We currently have support for keyboard, touch input, JoyCon input support, and nearly all MFI controllers.
  Motion controls are natively supported in most cases, however JoyCons do not have motion support doe to an iOS limitation.
  
- **DLC & Modifications**

  MeloNX supports DLC + Game Update Add-ons.
  Mods (romfs, exefs, and runtime mods such as cheats) are supported;

- **Configuration**

  The emulator has settings for enabling or disabling some logging, remapping controllers, and more.

# Notice

This project does not distribute, include, or endorse any copyrighted or pirated content. The software is provided for legitimate and lawful use.

Users are solely responsible for ensuring that any copyrighted material they use with this software (including, but not limited to, ROMs, cryptographic keys, and firmware) is obtained legally and in accordance with applicable law. The developers disclaim all responsibility for any misuse of the software or for any legal consequences resulting from the use of illegally obtained content.

Any third-party distribution of this emulator bundled with copyrighted content (including, but not limited to, ROMs, cryptographic keys, and firmware) is done without permission and is not associated with orendorsed by this project.

Nintendo Switch is a trademark of Nintendo Co., Ltd.
Nintendo is a trademark of Nintendo Co., Ltd.
Pokémon is a trademark of Nintendo/Creatures Inc./Game Freak Inc.
Animal Crossing is a trademark of Nintendo Co., Ltd.

This project is an independent, third-party work and is not affiliated with, endorsed by, or associated with Nintendo Co., Ltd., any of its subsidiaries, or any other console manufacturer or game and/or game publisher.

# License

This software is licensed under the terms of the [MeloNX license](LICENSE.txt).
This project makes use of code authored by the libvpx project, licensed under BSD and the ffmpeg project, licensed under LGPLv3.
See [LICENSE.txt](LICENSE.txt) and [THIRDPARTY.md](distribution/legal/THIRDPARTY.md) for more details.

# Credits
- [Ryujinx](https://github.com/ryujinx-mirror/ryujinx) is used for the base of this emulator. (link is to ryujinx-mirror since they were supportive)
- [LibHac](https://github.com/Thealexbarney/LibHac) is used for our file-system.
- [AmiiboAPI](https://www.amiiboapi.com) is used in our Amiibo emulation.
- [ldn_mitm](https://github.com/spacemeowx2/ldn_mitm) is used for one of our available multiplayer modes.
- [ShellLink](https://github.com/securifybv/ShellLink) is used for Windows shortcut generation.
