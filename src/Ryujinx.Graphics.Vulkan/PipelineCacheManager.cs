using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.IO;

namespace Ryujinx.Graphics.Vulkan
{
    internal sealed class PipelineCacheManager : IDisposable
    {
        private const long MaximumCacheSize = 128L * 1024 * 1024;

        private readonly Vk _api;
        private readonly Device _device;
        private readonly PipelineCache[] _workerCaches;
        private readonly PipelineCacheCheckpointPolicy _checkpointPolicy;
        private readonly string _cacheDirectory;
        private readonly string _mainCachePath;
        private readonly string[] _workerCachePaths;

        private bool _disposed;

        public PipelineCache MainCache { get; }
        public int WorkerCount => _workerCaches.Length;

        public PipelineCacheManager(Vk api, VulkanPhysicalDevice physicalDevice, Device device, int workerCount)
        {
            _api = api;
            _device = device;
            _workerCaches = new PipelineCache[workerCount];
            _checkpointPolicy = new(workerCount);
            _workerCachePaths = new string[workerCount];

            PhysicalDeviceProperties properties = physicalDevice.PhysicalDeviceProperties;
            string cacheKey = $"{properties.VendorID:X8}-{properties.DeviceID:X8}-{properties.DriverVersion:X8}";

            if (!string.IsNullOrEmpty(AppDataManager.BaseDirPath))
            {
                _cacheDirectory = Path.Combine(AppDataManager.BaseDirPath, "cache", "vulkan");
                _mainCachePath = Path.Combine(_cacheDirectory, $"pipeline-{cacheKey}.vkpc");

                for (int index = 0; index < workerCount; index++)
                {
                    _workerCachePaths[index] = Path.Combine(_cacheDirectory, $"pipeline-{cacheKey}.worker{index}.vkpc");
                }
            }

            byte[] mainData = LoadCacheData(_mainCachePath);
            MainCache = CreateCache(mainData, "main");

            int loadedWorkerCaches = 0;

            for (int index = 0; index < workerCount; index++)
            {
                byte[] workerData = LoadCacheData(_workerCachePaths[index]);

                if (workerData != null)
                {
                    loadedWorkerCaches++;
                }

                _workerCaches[index] = CreateCache(workerData ?? mainData, $"worker {index}");
            }

            // Recover anything captured by a periodic worker save before the previous clean shutdown.
            MergeInto(MainCache, _workerCaches);

            // Every worker starts with the full union but owns its destination cache exclusively.
            for (int index = 0; index < workerCount; index++)
            {
                PipelineCache source = MainCache;
                MergeInto(_workerCaches[index], new ReadOnlySpan<PipelineCache>(in source));
            }

            long loadedSize = mainData?.LongLength ?? 0;

            Logger.Notice.PrintMsg(
                LogClass.Gpu,
                $"Vulkan pipeline cache initialized: {FormatSize(loadedSize)} main data, " +
                $"{loadedWorkerCaches}/{workerCount} worker snapshots recovered.");
        }

        public PipelineCache GetWorkerCache(int workerIndex)
        {
            return _workerCaches[workerIndex];
        }

        public bool IsWorkerCache(PipelineCache cache)
        {
            ulong handle = cache.Handle;

            for (int index = 0; index < _workerCaches.Length; index++)
            {
                if (_workerCaches[index].Handle == handle)
                {
                    return true;
                }
            }

            return false;
        }

        public void NotifyWorkerPipelineCreated(int workerIndex)
        {
            // Independently implemented checkpoint pacing inspired by Eden PR #4294:
            // https://git.eden-emu.dev/eden-emu/eden/pulls/4294
            // Keep snapshots on the owning worker, between its pipeline creation operations.
            if (_checkpointPolicy.TryBegin(workerIndex))
            {
                bool saved = false;

                try
                {
                    saved = SaveCache(_workerCaches[workerIndex], _workerCachePaths[workerIndex], final: false);
                }
                finally
                {
                    _checkpointPolicy.Complete(workerIndex, saved);
                }
            }
        }

        public void MergeAndSave()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (MergeInto(MainCache, _workerCaches))
            {
                SaveCache(MainCache, _mainCachePath, final: true);
            }
        }

        private unsafe PipelineCache CreateCache(byte[] initialData, string description)
        {
            PipelineCache pipelineCache;
            PipelineCacheCreateInfo createInfo = new()
            {
                SType = StructureType.PipelineCacheCreateInfo,
                // Use core synchronization; pipelineCreationCacheControl is not enabled on the device.
                Flags = 0,
            };

            Result result;

            fixed (byte* data = initialData)
            {
                createInfo.InitialDataSize = (nuint)(initialData?.Length ?? 0);
                createInfo.PInitialData = data;

                result = _api.CreatePipelineCache(_device, in createInfo, null, out pipelineCache);
            }

            if (!result.IsError)
            {
                return pipelineCache;
            }

            if (initialData != null)
            {
                Logger.Warning?.PrintMsg(
                    LogClass.Gpu,
                    $"Vulkan {description} pipeline cache data was rejected ({result}); starting it empty.");

                createInfo.InitialDataSize = 0;
                createInfo.PInitialData = null;

                _api.CreatePipelineCache(_device, in createInfo, null, out pipelineCache).ThrowOnError();

                return pipelineCache;
            }

            result.ThrowOnError();
            return default;
        }

        private unsafe bool MergeInto(PipelineCache destination, ReadOnlySpan<PipelineCache> sources)
        {
            if (sources.IsEmpty)
            {
                return true;
            }

            Result result;

            fixed (PipelineCache* sourceCaches = sources)
            {
                result = _api.MergePipelineCaches(_device, destination, (uint)sources.Length, sourceCaches);
            }

            if (result.IsError)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Vulkan pipeline cache merge failed: {result}.");
                return false;
            }

            return true;
        }

        private byte[] LoadCacheData(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                FileInfo info = new(path);

                if (info.Length == 0 || info.Length > MaximumCacheSize)
                {
                    Logger.Warning?.PrintMsg(
                        LogClass.Gpu,
                        $"Ignoring Vulkan pipeline cache with invalid size {FormatSize(info.Length)}: {path}");
                    return null;
                }

                return File.ReadAllBytes(path);
            }
            catch (IOException exception)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Failed to read Vulkan pipeline cache: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Failed to read Vulkan pipeline cache: {exception.Message}");
            }

            return null;
        }

        private unsafe bool SaveCache(PipelineCache cache, string path, bool final)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            nuint dataSize = 0;
            Result result = _api.GetPipelineCacheData(_device, cache, &dataSize, null);

            if (result.IsError || dataSize == 0 || dataSize > (nuint)MaximumCacheSize || dataSize > int.MaxValue)
            {
                Logger.Warning?.PrintMsg(
                    LogClass.Gpu,
                    $"Unable to size Vulkan pipeline cache for persistence: {result}, {dataSize} bytes.");
                return false;
            }

            byte[] data = new byte[(int)dataSize];

            fixed (byte* dataPointer = data)
            {
                result = _api.GetPipelineCacheData(_device, cache, &dataSize, dataPointer);
            }

            if (result != Result.Success || dataSize == 0 || dataSize > (nuint)data.Length)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Unable to read Vulkan pipeline cache for persistence: {result}.");
                return false;
            }

            try
            {
                Directory.CreateDirectory(_cacheDirectory);

                string temporaryPath = path + ".tmp";

                using (FileStream file = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(data.AsSpan(0, checked((int)dataSize)));
                }

                File.Move(temporaryPath, path, overwrite: true);

                string message = $"Vulkan pipeline cache {(final ? "saved" : "checkpointed")}: {FormatSize((long)dataSize)}.";

                if (final)
                {
                    Logger.Notice.PrintMsg(LogClass.Gpu, message);
                }
                else
                {
                    Logger.Info?.PrintMsg(LogClass.Gpu, message);
                }

                return true;
            }
            catch (IOException exception)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Failed to persist Vulkan pipeline cache: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Failed to persist Vulkan pipeline cache: {exception.Message}");
            }

            return false;
        }

        private static string FormatSize(long size)
        {
            return size >= 1024 * 1024
                ? $"{size / (1024.0 * 1024.0):F2} MiB"
                : $"{size / 1024.0:F2} KiB";
        }

        public unsafe void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            for (int index = 0; index < _workerCaches.Length; index++)
            {
                _api.DestroyPipelineCache(_device, _workerCaches[index], null);
            }

            _api.DestroyPipelineCache(_device, MainCache, null);
        }
    }
}
