# MeloNX v10 — buffer working set / bounded scratch (experimental)

Baseline: v9 `29b5f5ab19983011fce574fa535e695dcd06cf1c`. Branch: `codex/gta-v10-cache-stability`. Master and previous releases remain unchanged. This release is not a certification of 720p/1080p GTA V stability on iPhone.

## Evidence from the supplied v9 session

The session metadata identifies iPhone 16 Pro Max / A18 Pro, iOS 27 beta 24A5430a, effective guest memory 4 GiB, threaded renderer Auto→On, 8 command buffers, configured caches 128/128 MiB, warm shader cache, Bilinear, AA off, JIT Auto 512 MiB. Resolution scale is 0.45; the first guest presentation is 576×324, not exactly 360p. The swapchain is later 2346×1320. The unbounded presentation target field is 120, but unbounded=False and normal production is about 30 FPS: this does not mean the game renders at 120 FPS.

At elapsed 396.144 s a pressure sample with about 969 MiB available permanently reduces the v9 buffer budget to 64 MiB. Issued buffer handles rise from 11,773 at 392.468 s to 91,415 at 413.578 s. Median presentation throughput over the following low-FPS interval is about 8.5 FPS, including windows around 6.4 FPS. Only hundreds of handles remain mapped. This is creation/reupload churn, not 91,415 simultaneously resident buffers. Texture normal evictions remain zero and texture-cache accounting stays around 85 MiB in this run; texture storage around the collapse is about 226 MiB. No sampled BufferMap miss or background buffer-copy activity is recorded. Heavy pipeline/descriptor trimming is not active at the onset.

The last memory sample at 654 s records footprint 6,425,618,912 bytes and available 16,832,032 bytes, close to the observed 6-GiB process ceiling. The supplied set has no matching system .ips; memory-limit termination is strongly suggested but Jetsam is not proven. Thermal state is already elevated much earlier while the game still produces about 30 FPS. There is no independent timestamp tying a particular log line to the exact train-collision frame.

## Changes

1. Preserve the configured buffer cache while measured process headroom exceeds 512 MiB. At <=512 MiB retain half the configured budget; at <=256 MiB retain a quarter. The user budget is never increased above its configured value. Clean/in-flight/dirty/alias eligibility checks are unchanged.
2. Replace a session-permanent pressure latch with sampled recovery. Require 20 continuous seconds above 512 MiB to recover quarter→half, or above 768 MiB to recover half→full. Gaps over 5 seconds, low samples and clock discontinuities restart qualification. Swift sends observation-only samples through the existing ABI. They cannot overwrite pending real pressure and do not schedule renderer trims, GC or barriers; ordinary sequence maintenance still runs.
3. The below-budget buffer-maintenance path returns before constructing eviction delegates or alias scans. A regression checks 20,000 under-budget calls for managed allocations.
4. Replace MemoryOwner's unbounded size-diverse idle pool. The old count trigger could increase without enforcing any byte ceiling. The new pool retains at most 64 MiB for byte arrays, 16 MiB per other closed generic type, 64 arrays per type, and does not retain a single array over 32 MiB. These are idle payload limits, not limits on all live conversion memory. Best-fit reuse and bounded LRU admission keep useful returned arrays. No active owner is reclaimed; Dispose stays idempotent. Reference-containing arrays are cleared on return. Under iOS headroom <=512 MiB the idle byte pool is trimmed to 16 MiB before a scheduled GC, without introducing an additional forced collection.
5. New scratch-pool telemetry reports idle, leased and peak leased payload, rentals, reuse and discarded payload every approximately 10 seconds. Logical payload is not resident memory and must not be added to overlapping GPU/managed/OS counters.

## Deliberately unchanged

No blind ASTC/BC capability forcing, lossy texture encoding, VMA/MoltenVK replacement, guest-page discard, JIT reduction, Auto→Off, or lower render-resolution default. Existing v9 readback/teardown fixes and native compression paths are preserved. MoltenVK remains the bundled 1.4.0 for this isolated experiment. The game resolution and texture quality are not silently reduced.

## Validation

CI runs graphics/threaded-lifetime/private-memory regressions, new actual BufferCache + tracked guest-memory + CPU fake-renderer fixtures, pressure/recovery tests, bounded scratch concurrent tests, the memory test project, Swift ownership tests, and NativeAOT/Xcode packaging. The CPU fake backend does not emulate Metal timing, real driver allocation pressure or full-game correctness. Actual results and source commit are attached by the workflow after completion; planned tests are not evidence of passing tests.

New fitting-working-set cases simulate the reported 969/1229-MiB headroom, repeatedly traverse unchanged guest buffers, verify handles are reused and sentinel bytes remain correct. Emergency cases still reclaim clean buffers and reload correct data. Pool tests cover many distinct sizes, byte/count bounds, active data across trimming, concurrent rental/return, reference clearing and double Dispose.

## Device gate

The primary first test is native 1.0x (typically 1280×720 for this guest), with the same shader cache, Bilinear, AA off, Auto threading and Switch VSync, after a complete app restart. Do not change settings mid-run. Native 1.5x from a 720p source is 1920×1080, but its stability/performance is not established by CI. Increasing output resolution is not itself a memory fix.

Compare the same route: prologue/train, end of prologue, Michael therapy, Franklin control and subsequent streaming. Check whether buffer creations stay bounded, scratch idle is <=64 MiB, headroom survives the transition and there is no stale-resource exception. A separate Stop + 60 seconds foreground verifies teardown. Export the complete session, all memory segments, core log and matching system .ips when available. If memory growth persists, v10's leased/idle scratch counters help distinguish retained pool memory from live resources. No fixed FPS improvement or total RAM saving is claimed without a new device run.

IPA is unprovisioned and needs re-signing for sideload.
