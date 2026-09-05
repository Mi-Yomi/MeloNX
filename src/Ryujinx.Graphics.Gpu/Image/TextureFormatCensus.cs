using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Image
{
    internal enum TextureFallbackReason
    {
        Native,
        AstcFamilyCapability,
        AstcFamilyCapabilityToBc7,
        Etc2FamilyCapability,
        BcFamilyCapability,
        Bc3DTargetCapability,
        BcFamilyAnd3DTargetCapability,
        PackedFormatCapability,
        OtherHostFormat,
        Count,
    }

    internal enum TextureAllocationRole
    {
        Storage,
        View,
        ReadbackAuxiliary,
        UploadAuxiliary,
    }

    [Flags]
    internal enum TextureObservedUsage
    {
        Unknown = 0,
        Upload = 1,
        Readback = 2,
        ShaderAccess = 4,
        GpuWrite = 8,
        ScaledCopy = 16,
    }

    internal readonly record struct TextureFormatKey(
        Format GuestFormat, Format HostGalFormat, TextureFallbackReason Fallback, Target Target,
        int GuestWidth, int GuestHeight, int HostWidth, int HostHeight, int Depth, int Layers, int Levels,
        int Samples, TextureAllocationRole Role);

    internal struct TextureCensusRegistration
    {
        internal int Slot;
        internal ulong LogicalBytes;
        internal TextureAllocationRole Role;
    }

    /// <summary>
    /// Bounded GAL-issued texture lifetime census. Contains no texture references or guest addresses.
    /// Storage bytes are logical, not physical residency: a Release can still be queued/in flight.
    /// A private short lock covers lifetime bookkeeping: background readback can create auxiliary
    /// storage while the GPU thread creates another texture. No renderer calls, callbacks or waits
    /// execute under that lock. Usage/converter counters do not take it. Formatting works on a bounded
    /// copy outside the lock. Vulkan's final VkFormat/usage flags are not inferred here.
    /// </summary>
    internal sealed class TextureFormatCensus
    {
        internal const int MaxKeys = 64;
        private const int OverflowSlot = MaxKeys;

        private struct Entry
        {
            internal TextureFormatKey Key;
            internal long Created;
            internal long Released;
            internal long LiveStorage;
            internal long LiveViews;
            internal ulong LiveLogicalBytes;
            internal ulong PeakLogicalBytes;
            internal int Usage;
        }

        private struct Conversion
        {
            internal long Calls;
            internal long Failed;
            internal long SourceBytes;
            internal long OutputBytes;
            internal long CpuTicks;
        }

        private readonly Dictionary<TextureFormatKey, int> _indices = new(MaxKeys);
        private readonly object _gate = new();
        private readonly Entry[] _entries = new Entry[MaxKeys + 1];
        private readonly Conversion[] _conversions = new Conversion[(int)TextureFallbackReason.Count];
        private long _invalidAstc;

        internal int KeyCount
        {
            get
            {
                lock (_gate)
                {
                    return _indices.Count;
                }
            }
        }

        internal static TextureFallbackReason GetFallbackReason(TextureInfo info, Format hostFormat, Capabilities caps)
        {
            Format guest = info.FormatInfo.Format;
            if (guest == hostFormat)
            {
                return TextureFallbackReason.Native;
            }

            if (guest.IsAstc && !caps.SupportsAstcCompression)
            {
                return hostFormat is Format.Bc7Unorm or Format.Bc7Srgb
                    ? TextureFallbackReason.AstcFamilyCapabilityToBc7
                    : TextureFallbackReason.AstcFamilyCapability;
            }

            if (guest.IsEtc2 && !caps.SupportsEtc2Compression)
            {
                return TextureFallbackReason.Etc2FamilyCapability;
            }

            if (!TextureCompatibility.HostSupportsBcFormat(guest, info.Target, caps))
            {
                bool family = !TextureCompatibility.HostSupportsBcFormat(guest, Target.Texture2D, caps);
                bool target = info.Target == Target.Texture3D && !caps.Supports3DTextureCompression;
                return family && target ? TextureFallbackReason.BcFamilyAnd3DTargetCapability :
                    target ? TextureFallbackReason.Bc3DTargetCapability : TextureFallbackReason.BcFamilyCapability;
            }

            return guest.Is16BitPacked || guest == Format.R4G4Unorm
                ? TextureFallbackReason.PackedFormatCapability : TextureFallbackReason.OtherHostFormat;
        }

        internal TextureCensusRegistration Add(TextureInfo guest, TextureCreateInfo host, Capabilities caps, TextureAllocationRole role)
        {
            TextureFormatKey key = new(guest.FormatInfo.Format, host.Format, GetFallbackReason(guest, host.Format, caps),
                host.Target, guest.Width, guest.Height, host.Width, host.Height,
                host.Target == Target.Texture3D ? host.Depth : 1, host.GetLayers(), host.Levels, host.Samples, role);
            return Add(key, role == TextureAllocationRole.View ? 0 : host.GetTotalSize());
        }

        internal TextureCensusRegistration Add(TextureFormatKey key, ulong logicalBytes)
        {
            lock (_gate)
            {
                return AddLocked(key, logicalBytes);
            }
        }

        private TextureCensusRegistration AddLocked(TextureFormatKey key, ulong logicalBytes)
        {
            if (!_indices.TryGetValue(key, out int index))
            {
                index = _indices.Count;
                if (index < MaxKeys)
                {
                    _indices.Add(key, index);
                    _entries[index].Key = key;
                }
                else
                {
                    index = OverflowSlot;
                }
            }

            ref Entry entry = ref _entries[index];
            entry.Created++;
            if (key.Role == TextureAllocationRole.View)
            {
                entry.LiveViews++;
                logicalBytes = 0; // A view never adds the storage payload again.
            }
            else
            {
                entry.LiveStorage++;
                entry.LiveLogicalBytes += logicalBytes;
                entry.PeakLogicalBytes = Math.Max(entry.PeakLogicalBytes, entry.LiveLogicalBytes);
            }

            return new TextureCensusRegistration { Slot = index + 1, LogicalBytes = logicalBytes, Role = key.Role };
        }

        internal void Release(ref TextureCensusRegistration registration)
        {
            lock (_gate)
            {
                if (registration.Slot == 0)
                {
                    return;
                }

                ref Entry entry = ref _entries[registration.Slot - 1];
                entry.Released++;
                if (registration.Role == TextureAllocationRole.View)
                {
                    entry.LiveViews--;
                }
                else
                {
                    entry.LiveStorage--;
                    entry.LiveLogicalBytes -= registration.LogicalBytes;
                }
                registration = default;
            }
        }

        internal void MarkUsage(TextureCensusRegistration registration, TextureObservedUsage usage)
        {
            if (registration.Slot != 0)
            {
                ref int observed = ref _entries[registration.Slot - 1].Usage;
                if ((Volatile.Read(ref observed) & (int)usage) != (int)usage)
                {
                    Interlocked.Or(ref observed, (int)usage);
                }
            }
        }

        internal void RecordConversion(TextureFallbackReason reason, int sourceBytes, int outputBytes, long cpuTicks, bool failed)
        {
            ref Conversion conversion = ref _conversions[(int)reason];
            Interlocked.Increment(ref conversion.Calls);
            if (failed)
            {
                Interlocked.Increment(ref conversion.Failed);
            }
            Interlocked.Add(ref conversion.SourceBytes, sourceBytes);
            Interlocked.Add(ref conversion.OutputBytes, outputBytes);
            Interlocked.Add(ref conversion.CpuTicks, cpuTicks);
        }

        internal void RecordInvalidAstc() => Interlocked.Increment(ref _invalidAstc);

        internal (long Calls, long Failed, long SourceBytes, long OutputBytes, long CpuTicks) GetConversionStatistics(TextureFallbackReason reason)
        {
            ref Conversion conversion = ref _conversions[(int)reason];
            return (Interlocked.Read(ref conversion.Calls), Interlocked.Read(ref conversion.Failed),
                Interlocked.Read(ref conversion.SourceBytes), Interlocked.Read(ref conversion.OutputBytes), Interlocked.Read(ref conversion.CpuTicks));
        }

        internal (long Created, long Released, long LiveStorage, long LiveViews, ulong LogicalBytes) GetTotals()
        {
            lock (_gate)
            {
                return GetTotals(_entries);
            }
        }

        private static (long Created, long Released, long LiveStorage, long LiveViews, ulong LogicalBytes) GetTotals(Entry[] entries)
        {
            long created = 0, released = 0, storage = 0, views = 0;
            ulong bytes = 0;
            foreach (Entry entry in entries)
            {
                created += entry.Created;
                released += entry.Released;
                storage += entry.LiveStorage;
                views += entry.LiveViews;
                bytes += entry.LiveLogicalBytes;
            }
            return (created, released, storage, views, bytes);
        }

        internal string GetDiagnosticSnapshot()
        {
            long start = Stopwatch.GetTimestamp();
            Entry[] entries;
            int keyCount;
            lock (_gate)
            {
                entries = (Entry[])_entries.Clone();
                keyCount = _indices.Count;
            }
            var totals = GetTotals(entries);
            StringBuilder builder = new(2048);
            builder.Append(CultureInfo.InvariantCulture,
                $"scope=gal_issued_lifetimes, bytes_quality=logical_not_resident, usage_quality=observed_ops_not_vk_flags, " +
                $"keys={keyCount}, max_keys={MaxKeys}, created={totals.Created}, released={totals.Released}, " +
                $"live_storage={totals.LiveStorage}, live_views={totals.LiveViews}, logical_bytes={totals.LogicalBytes}, " +
                $"invalid_astc={Interlocked.Read(ref _invalidAstc)}");
            for (int index = 0; index <= MaxKeys; index++)
            {
                ref Entry entry = ref entries[index];
                if (entry.Created == 0)
                {
                    continue;
                }
                TextureFormatKey key = entry.Key;
                builder.Append("; bin=[");
                if (index == OverflowSlot)
                {
                    builder.Append("key=overflow_unknown");
                }
                else
                {
                    builder.Append(CultureInfo.InvariantCulture,
                        $"guest={key.GuestFormat}, host_gal={key.HostGalFormat}, fallback={key.Fallback}, target={key.Target}, " +
                        $"guest_width={key.GuestWidth}, guest_height={key.GuestHeight}, host_width={key.HostWidth}, host_height={key.HostHeight}, " +
                        $"depth={key.Depth}, layers={key.Layers}, levels={key.Levels}, samples={key.Samples}, role={key.Role}");
                }
                builder.Append(CultureInfo.InvariantCulture,
                    $", created={entry.Created}, released={entry.Released}, live_storage={entry.LiveStorage}, live_views={entry.LiveViews}, " +
                    $"logical_bytes={entry.LiveLogicalBytes}, peak_logical_bytes={entry.PeakLogicalBytes}, observed_ops_mask={Volatile.Read(ref entry.Usage)}]");
            }
            for (int index = 0; index < _conversions.Length; index++)
            {
                ref Conversion conversion = ref _conversions[index];
                long calls = Interlocked.Read(ref conversion.Calls);
                if (calls != 0)
                {
                    double cpuMs = Interlocked.Read(ref conversion.CpuTicks) * 1000.0 / Stopwatch.Frequency;
                    builder.Append(CultureInfo.InvariantCulture,
                        $"; conversion=[fallback={(TextureFallbackReason)index}, calls={calls}, failed={Interlocked.Read(ref conversion.Failed)}, " +
                        $"source_bytes={Interlocked.Read(ref conversion.SourceBytes)}, output_bytes={Interlocked.Read(ref conversion.OutputBytes)}, cpu_ms={cpuMs:F3}]");
                }
            }
            builder.Append(CultureInfo.InvariantCulture, $"; census_cpu_ms={(Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency:F3}");
            return builder.ToString();
        }
    }
}
