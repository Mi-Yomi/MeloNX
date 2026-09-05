# MoltenVK 1.4.0 lifetime and host-import backports

This candidate preserves MoltenVK 1.4.0's descriptor implementation and applies
the autorelease-pool part of upstream commit
[5ff9bced](https://github.com/KhronosGroup/MoltenVK/commit/5ff9bcedada97c69686102e00f427355d1096b30).
The baseline source is
[45887054](https://github.com/KhronosGroup/MoltenVK/tree/458870543b9bcf6b0edd6f90aaa776707b310d96),
with all seven upstream dependency revisions and the pre-generated SPIRV-Tools
header archive locked in `source-lock.json`. It also applies the complete host
external-memory validation fix from
[c33ebc7e](https://github.com/KhronosGroup/MoltenVK/commit/c33ebc7e1ea491529477c795479ab414b335ab57).
No driver capability is forced.

`vkUpdateDescriptorSets` and `vkUpdateDescriptorSetWithTemplate` in this baseline
do not establish an autorelease pool. With Metal argument buffers enabled,
`MVKTexelBufferDescriptor::write` can call `MVKBufferView::getMTLTexture` from these
host descriptor updates, outside the per-command pool used by ImmediateEncoding.
The convenience `MTLTextureDescriptor` factory produces an autoreleased object.
The patch gives those temporary objects a local lifetime even when the calling
managed thread does not drain a pool. The actual texture returned by
`newTextureWithDescriptor` remains owned (+1) by the buffer view and is released
by its existing destruction path. No GPU reference/fence lifetime is shortened.

The upstream commit also contains later resource-tracker code absent from 1.4.0;
that unrelated machinery is not backported. In particular this build does not
adopt 1.4.1/1.4.2 descriptor tracking, change heap policy, enable unsupported BC/
ASTC formats, or alter copy behavior. Existing device logs establish memory
pressure but do not quantify this pool's contribution. Device validation remains
required; successful native compilation is not evidence of a GTA V crash fix.

MoltenVK 1.4.0 advertises `VK_EXT_external_memory_host` and already implements
host pointer allocation and Metal buffers without copying. However, its
`VkExternalMemoryBufferCreateInfo` validation rejects `HOST_ALLOCATION_BIT_EXT`
before that backend can be used. MeloNX v12 reached this exact failure in
`CreateHostImported` at 27.110 seconds. The second patch accepts the actual
external-memory properties for buffers and images and preserves Metal heap
support by adding its missing buffer property entry. It does not change public
ABI, host-pointer ownership, the allocation backend, or descriptor tracking.
Only two trailing-whitespace-only lines differ from the complete upstream diff.
The opaque historical bundled binary's custom patch provenance is unknown;
it must not be assumed equivalent to an unmodified upstream 1.4.0 build.

On Apple Silicon macOS with Xcode selected and an available Metal device:

```sh
bash distribution/ios/build-moltenvk.sh
```

The default output is `artifacts/moltenvk`. The tracked legacy dylib is untouched.
To create an unpatched control from the same locked source/toolchain:

```sh
MELO_MVK_VARIANT=baseline MELO_MVK_OUTPUT_DIR="$PWD/artifacts/moltenvk-baseline" \
  bash distribution/ios/build-moltenvk.sh
```

Use a fresh output directory for each build. `MELO_MVK_OUTPUT_DIR` and
`MELO_MVK_VARIANT` are the only build overrides; compiler/Xcode selection follows
`DEVELOPER_DIR`. The release job pins Xcode 26.2. The script records the complete
Xcode, SDK, and clang versions. Source revisions are reproducible; byte-identical
Mach-O UUIDs/timestamps across independent Xcode builds have not been established.

Build gates verify the patch SHA, upstream/dependency HEADs and tracked source,
the 152-byte managed configuration prefix against native arm64 headers, required
exported entry points, thin arm64 iPhoneOS platform and iOS 13.0 minimum, dylib
install name, system-only dependencies, and matching native dSYM UUID. The old
configuration's supported size-prefix protocol is preserved; MeloNX continues
to read back the effective settings at runtime. The ABI probe compiles but does
not execute on the macOS runner, because the output targets a physical iPhone.

Patched release builds additionally compile two macOS arm64 dylibs from the same
locked source: the v12 control (only the lifetime patch) and candidate (both
patches). `host_import_probe.mm` loads each in a separate process and calls the
actual Vulkan entry points. The control must reproduce `VK_ERROR_FEATURE_NOT_PRESENT`
from `vkCreateBuffer` with the host-allocation `pNext`. The candidate must import
an aligned CPU allocation, bind it at a nonzero offset, perform two GPU copies
in opposite directions with barriers and completed fences, compare all bytes,
preserve bytes around the binding and retain CPU ownership after native objects
are destroyed. The supervising process enforces a 60-second bound for each run.

This gate requires real Metal availability. A headless runner returning no
Metal device fails explicitly with exit 77; the build never converts that into
a successful skip. Reports, both tested dylibs, stdout/stderr and exact source
patch lists are in `host-import-regression/`; its `result.json` must report
`passed` and is referenced by `build-manifest.json.host_import_regression`.
The unpatched `baseline` override is marked `not_run_baseline` and is not a
release candidate. A successful macOS import regression proves the shared
native path; physical iPhone testing is still required for device-specific
memory pressure and game compatibility.

Artifacts contain the dylib, original dSYM, manifest and SHA-256 hashes,
unmodified source archives for all eight repositories, applied patch, original
license/notice files, native ABI probe, and build/validation logs. The source
archives plus patch reproduce the source input; the existing opaque bundled
binary may have different historical build settings and is not a controlled
unpatched source-build comparison.

For a device A/B, keep the game build, save point, resolution, memory/JIT setup
and cache state fixed. Record the loaded dylib hash/UUID and configuration
readback. Compare native driver allocation and process footprint growth, view
creation/destruction rate, FPS and time to the same scene. Retain `.ips`, session,
memory samples, and this exact dSYM for any crash. The separate engine fix stops
unchanged texture-buffer views being destroyed by unrelated buffer allocations;
this native backport bounds temporary ObjC objects when a view is actually made.

Licensing: MoltenVK and these upstream source patches are Apache-2.0; copyright of
the original implementation belongs to its upstream authors. The originating
first change is authored by Evan Tang (CodeWeavers); the second by squidbus.
`LICENSE.Apache-2.0.txt` is the unmodified upstream license. The first modified
source contains an explicit adaptation notice; the second is a complete
upstream backport with only trailing whitespace normalized, documented here.
Dependency notices are collected from each exact source revision into
the release artifact. The package also preserves MeloNX's own license notices.
