// Headless regression against the actual freshly built MoltenVK dylib.
// A different process loads the v12 control and the fixed candidate, avoiding
// private API/global configuration collisions between two MoltenVK instances.
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include <dlfcn.h>
#include <unistd.h>
#include <cstdlib>
#include <cstring>
#include <stdexcept>
#include <string>
#include <vector>

#define ENTRY_POINTS(X) \
    X(CreateInstance) X(DestroyInstance) X(EnumeratePhysicalDevices) \
    X(GetPhysicalDeviceQueueFamilyProperties) X(EnumerateDeviceExtensionProperties) \
    X(GetPhysicalDeviceMemoryProperties) X(GetPhysicalDeviceProperties2) \
    X(CreateDevice) X(DestroyDevice) X(GetDeviceQueue) \
    X(CreateBuffer) X(DestroyBuffer) X(GetBufferMemoryRequirements) X(AllocateMemory) \
    X(FreeMemory) X(BindBufferMemory) X(MapMemory) X(UnmapMemory) \
    X(GetMemoryHostPointerPropertiesEXT) X(CreateCommandPool) X(DestroyCommandPool) \
    X(AllocateCommandBuffers) X(BeginCommandBuffer) X(EndCommandBuffer) X(ResetCommandPool) \
    X(CmdPipelineBarrier) X(CmdCopyBuffer) X(CreateFence) X(DestroyFence) \
    X(ResetFences) X(QueueSubmit) X(WaitForFences)

struct Api {
#define FIELD(name) PFN_vk##name name = nullptr;
    ENTRY_POINTS(FIELD)
#undef FIELD
    explicit Api(void* library) {
#define LOAD(name) name = reinterpret_cast<PFN_vk##name>(dlsym(library, "vk" #name)); \
    if (!name) throw std::runtime_error("Missing export vk" #name);
        ENTRY_POINTS(LOAD)
#undef LOAD
    }
};

static void require(bool condition, const char* reason) {
    if (!condition) throw std::runtime_error(reason);
}

static void check(VkResult result, const char* stage) {
    if (result != VK_SUCCESS) throw std::runtime_error(std::string(stage) + ": " + std::to_string(result));
}

static uint32_t memoryType(const VkPhysicalDeviceMemoryProperties& properties, uint32_t bits) {
    // Match MeloNX's required host-visible/coherent/cached import flags.
    const VkMemoryPropertyFlags wanted = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
        VK_MEMORY_PROPERTY_HOST_COHERENT_BIT | VK_MEMORY_PROPERTY_HOST_CACHED_BIT;
    for (uint32_t i = 0; i < properties.memoryTypeCount; ++i) {
        if ((bits & (1u << i)) && (properties.memoryTypes[i].propertyFlags & wanted) == wanted) return i;
    }
    throw std::runtime_error("No compatible host-visible/coherent/cached memory type");
}

static VkDeviceSize alignUp(VkDeviceSize value, VkDeviceSize alignment) {
    return (value + alignment - 1) / alignment * alignment;
}

static void pattern(uint8_t* data, size_t size, uint8_t salt) {
    for (size_t i = 0; i < size; ++i) data[i] = static_cast<uint8_t>((i * 37 + (i >> 4)) ^ salt);
}

static void barrier(Api& api, VkCommandBuffer commands, VkBuffer buffer, VkDeviceSize size,
                    VkAccessFlags source, VkAccessFlags destination,
                    VkPipelineStageFlags sourceStage, VkPipelineStageFlags destinationStage) {
    VkBufferMemoryBarrier info{VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER};
    info.srcAccessMask = source;
    info.dstAccessMask = destination;
    info.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    info.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    info.buffer = buffer;
    info.size = size;
    api.CmdPipelineBarrier(commands, sourceStage, destinationStage, 0, 0, nullptr, 1, &info, 0, nullptr);
}

static void writeReport(NSMutableDictionary* report, const char* path) {
    NSError* error = nil;
    NSData* data = [NSJSONSerialization dataWithJSONObject:report options:NSJSONWritingPrettyPrinted error:&error];
    if (!data || ![data writeToFile:[NSString stringWithUTF8String:path] atomically:YES]) {
        fprintf(stderr, "Cannot write native probe report\n");
        std::exit(2);
    }
}

int main(int argc, const char* argv[]) {
    @autoreleasepool {
        if (argc != 4 || (std::strcmp(argv[2], "expect-rejected") && std::strcmp(argv[2], "expect-roundtrip"))) {
            fprintf(stderr, "usage: host_import_probe dylib expect-rejected|expect-roundtrip report.json\n");
            return 2;
        }
        NSMutableDictionary* report = [@{@"schema": @1, @"status": @"failed",
            @"expectation": [NSString stringWithUTF8String:argv[2]], @"platform": @"macOS arm64"} mutableCopy];
        id<MTLDevice> metal = MTLCreateSystemDefaultDevice();
        if (!metal) {
            report[@"status"] = @"unavailable";
            report[@"reason"] = @"MTLCreateSystemDefaultDevice returned nil; a Metal-capable runner is required";
            writeReport(report, argv[3]);
            return 77; // The release script MUST fail, not silently skip this gate.
        }
        report[@"metal_device"] = metal.name;
        const bool expectRejected = !std::strcmp(argv[2], "expect-rejected");
        try {
            void* library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
            if (!library) throw std::runtime_error(std::string("dlopen failed: ") + dlerror());
            Api api(library);
            VkApplicationInfo application{VK_STRUCTURE_TYPE_APPLICATION_INFO};
            application.pApplicationName = "MeloNX host import regression";
            application.apiVersion = VK_API_VERSION_1_1;
            const char* instanceExtensions[] = {VK_KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME};
            VkInstanceCreateInfo instanceInfo{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
            instanceInfo.flags = VK_INSTANCE_CREATE_ENUMERATE_PORTABILITY_BIT_KHR;
            instanceInfo.pApplicationInfo = &application;
            instanceInfo.enabledExtensionCount = 1;
            instanceInfo.ppEnabledExtensionNames = instanceExtensions;
            VkInstance instance = VK_NULL_HANDLE;
            check(api.CreateInstance(&instanceInfo, nullptr, &instance), "create_instance");
            uint32_t physicalCount = 0;
            check(api.EnumeratePhysicalDevices(instance, &physicalCount, nullptr), "count_physical_devices");
            require(physicalCount != 0, "No MoltenVK physical device");
            std::vector<VkPhysicalDevice> physicalDevices(physicalCount);
            check(api.EnumeratePhysicalDevices(instance, &physicalCount, physicalDevices.data()), "enumerate_physical_devices");
            VkPhysicalDevice physical = physicalDevices[0];

            uint32_t queueCount = 0;
            api.GetPhysicalDeviceQueueFamilyProperties(physical, &queueCount, nullptr);
            std::vector<VkQueueFamilyProperties> queues(queueCount);
            api.GetPhysicalDeviceQueueFamilyProperties(physical, &queueCount, queues.data());
            uint32_t family = queueCount;
            for (uint32_t i = 0; i < queueCount; ++i) {
                if (queues[i].queueCount && (queues[i].queueFlags & VK_QUEUE_TRANSFER_BIT)) { family = i; break; }
            }
            require(family < queueCount, "No transfer queue");
            uint32_t extensionCount = 0;
            check(api.EnumerateDeviceExtensionProperties(physical, nullptr, &extensionCount, nullptr), "count_extensions");
            std::vector<VkExtensionProperties> extensions(extensionCount);
            check(api.EnumerateDeviceExtensionProperties(physical, nullptr, &extensionCount, extensions.data()), "enumerate_extensions");
            bool hasHost = false, hasPortability = false;
            for (const auto& extension : extensions) {
                hasHost |= !std::strcmp(extension.extensionName, VK_EXT_EXTERNAL_MEMORY_HOST_EXTENSION_NAME);
                hasPortability |= !std::strcmp(extension.extensionName, "VK_KHR_portability_subset");
            }
            require(hasHost, "VK_EXT_external_memory_host was not advertised");
            const char* deviceExtensions[] = {VK_EXT_EXTERNAL_MEMORY_HOST_EXTENSION_NAME, "VK_KHR_portability_subset"};
            float priority = 1.0f;
            VkDeviceQueueCreateInfo queueInfo{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
            queueInfo.queueFamilyIndex = family;
            queueInfo.queueCount = 1;
            queueInfo.pQueuePriorities = &priority;
            VkDeviceCreateInfo deviceInfo{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
            deviceInfo.queueCreateInfoCount = 1;
            deviceInfo.pQueueCreateInfos = &queueInfo;
            deviceInfo.enabledExtensionCount = hasPortability ? 2 : 1;
            deviceInfo.ppEnabledExtensionNames = deviceExtensions;
            VkDevice device = VK_NULL_HANDLE;
            check(api.CreateDevice(physical, &deviceInfo, nullptr, &device), "create_device");
            VkQueue queue = VK_NULL_HANDLE;
            api.GetDeviceQueue(device, family, 0, &queue);

            const VkDeviceSize page = static_cast<VkDeviceSize>(sysconf(_SC_PAGESIZE));
            require(page > 0 && page <= 65536, "Unexpected host page size");
            VkExternalMemoryBufferCreateInfo external{VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_BUFFER_CREATE_INFO};
            external.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_HOST_ALLOCATION_BIT_EXT;
            VkBufferCreateInfo bufferInfo{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
            bufferInfo.pNext = &external;
            bufferInfo.size = page;
            bufferInfo.usage = VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VK_BUFFER_USAGE_TRANSFER_DST_BIT;
            bufferInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
            VkBuffer importedBuffer = VK_NULL_HANDLE;
            VkResult created = api.CreateBuffer(device, &bufferInfo, nullptr, &importedBuffer);
            report[@"host_create_result"] = @(created);
            if (expectRejected) {
                if (created == VK_SUCCESS) api.DestroyBuffer(device, importedBuffer, nullptr);
                require(created == VK_ERROR_FEATURE_NOT_PRESENT, "Control must reproduce VK_ERROR_FEATURE_NOT_PRESENT at host buffer creation");
                api.DestroyDevice(device, nullptr);
                api.DestroyInstance(instance, nullptr);
                dlclose(library);
                report[@"status"] = @"expected_rejection";
                writeReport(report, argv[3]);
                return 0;
            }
            check(created, "create_host_buffer");
            VkMemoryRequirements requirements{};
            api.GetBufferMemoryRequirements(device, importedBuffer, &requirements);
            VkPhysicalDeviceExternalMemoryHostPropertiesEXT hostProperties{
                VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_MEMORY_HOST_PROPERTIES_EXT};
            VkPhysicalDeviceProperties2 physicalProperties{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2};
            physicalProperties.pNext = &hostProperties;
            api.GetPhysicalDeviceProperties2(physical, &physicalProperties);
            const VkDeviceSize hostAlignment = hostProperties.minImportedHostPointerAlignment;
            require(hostAlignment >= sizeof(void*) && (hostAlignment & (hostAlignment - 1)) == 0 &&
                hostAlignment <= 1024 * 1024, "Unexpected imported host pointer alignment");
            require(requirements.alignment != 0 && requirements.size >= page &&
                requirements.size <= 1024 * 1024, "Unexpected imported buffer requirements");
            const VkDeviceSize bindingOffset = alignUp(page, requirements.alignment);
            const VkDeviceSize allocationSize = alignUp(bindingOffset + requirements.size, hostAlignment);
            void* host = nullptr;
            require(posix_memalign(&host, hostAlignment, allocationSize) == 0, "Host aligned allocation failed");
            std::memset(host, 0xc7, allocationSize);
            auto* importedBytes = static_cast<uint8_t*>(host) + bindingOffset;
            std::vector<uint8_t> expected(page);
            pattern(expected.data(), page, 0x39);
            std::memcpy(importedBytes, expected.data(), page);
            VkMemoryHostPointerPropertiesEXT pointerProperties{VK_STRUCTURE_TYPE_MEMORY_HOST_POINTER_PROPERTIES_EXT};
            check(api.GetMemoryHostPointerPropertiesEXT(device,
                VK_EXTERNAL_MEMORY_HANDLE_TYPE_HOST_ALLOCATION_BIT_EXT, host, &pointerProperties), "host_pointer_properties");
            VkPhysicalDeviceMemoryProperties memoryProperties{};
            api.GetPhysicalDeviceMemoryProperties(physical, &memoryProperties);
            VkImportMemoryHostPointerInfoEXT importInfo{VK_STRUCTURE_TYPE_IMPORT_MEMORY_HOST_POINTER_INFO_EXT};
            importInfo.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_HOST_ALLOCATION_BIT_EXT;
            importInfo.pHostPointer = host;
            VkMemoryAllocateInfo allocationInfo{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
            allocationInfo.pNext = &importInfo;
            allocationInfo.allocationSize = allocationSize;
            allocationInfo.memoryTypeIndex = memoryType(memoryProperties, pointerProperties.memoryTypeBits & requirements.memoryTypeBits);
            VkDeviceMemory importedMemory = VK_NULL_HANDLE;
            check(api.AllocateMemory(device, &allocationInfo, nullptr, &importedMemory), "allocate_host_memory");
            check(api.BindBufferMemory(device, importedBuffer, importedMemory, bindingOffset), "bind_host_memory_nonzero_offset");

            bufferInfo.pNext = nullptr;
            VkBuffer ordinaryBuffer = VK_NULL_HANDLE;
            check(api.CreateBuffer(device, &bufferInfo, nullptr, &ordinaryBuffer), "create_readback_buffer");
            api.GetBufferMemoryRequirements(device, ordinaryBuffer, &requirements);
            allocationInfo.pNext = nullptr;
            allocationInfo.allocationSize = requirements.size;
            allocationInfo.memoryTypeIndex = memoryType(memoryProperties, requirements.memoryTypeBits);
            VkDeviceMemory ordinaryMemory = VK_NULL_HANDLE;
            check(api.AllocateMemory(device, &allocationInfo, nullptr, &ordinaryMemory), "allocate_readback_memory");
            check(api.BindBufferMemory(device, ordinaryBuffer, ordinaryMemory, 0), "bind_readback_memory");
            void* mapped = nullptr;
            check(api.MapMemory(device, ordinaryMemory, 0, page, 0, &mapped), "map_readback_memory");
            VkCommandPoolCreateInfo poolInfo{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
            poolInfo.queueFamilyIndex = family;
            VkCommandPool pool = VK_NULL_HANDLE;
            check(api.CreateCommandPool(device, &poolInfo, nullptr, &pool), "create_command_pool");
            VkCommandBufferAllocateInfo commandsInfo{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
            commandsInfo.commandPool = pool;
            commandsInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
            commandsInfo.commandBufferCount = 1;
            VkCommandBuffer commands = VK_NULL_HANDLE;
            check(api.AllocateCommandBuffers(device, &commandsInfo, &commands), "allocate_commands");
            VkFenceCreateInfo fenceInfo{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
            VkFence fence = VK_NULL_HANDLE;
            check(api.CreateFence(device, &fenceInfo, nullptr, &fence), "create_fence");
            for (unsigned direction = 0; direction < 2; ++direction) {
                if (direction) {
                    pattern(expected.data(), page, 0x96);
                    std::memcpy(mapped, expected.data(), page);
                    check(api.ResetCommandPool(device, pool, 0), "reset_command_pool");
                    check(api.ResetFences(device, 1, &fence), "reset_fence");
                }
                VkBuffer source = direction ? ordinaryBuffer : importedBuffer;
                VkBuffer destination = direction ? importedBuffer : ordinaryBuffer;
                VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
                begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
                check(api.BeginCommandBuffer(commands, &begin), "begin_commands");
                barrier(api, commands, source, page, VK_ACCESS_HOST_WRITE_BIT, VK_ACCESS_TRANSFER_READ_BIT,
                    VK_PIPELINE_STAGE_HOST_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
                VkBufferCopy copy{0, 0, page};
                api.CmdCopyBuffer(commands, source, destination, 1, &copy);
                barrier(api, commands, destination, page, VK_ACCESS_TRANSFER_WRITE_BIT, VK_ACCESS_HOST_READ_BIT,
                    VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_HOST_BIT);
                check(api.EndCommandBuffer(commands), "end_commands");
                VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
                submit.commandBufferCount = 1;
                submit.pCommandBuffers = &commands;
                check(api.QueueSubmit(queue, 1, &submit, fence), "queue_submit");
                check(api.WaitForFences(device, 1, &fence, VK_TRUE, 10'000'000'000ULL), "wait_fence");
                require(std::memcmp(direction ? importedBytes : mapped, expected.data(), page) == 0,
                    direction ? "GPU-to-imported-host bytes mismatch" : "Imported-host-to-GPU bytes mismatch");
            }
            for (VkDeviceSize i = 0; i < bindingOffset; ++i)
                require(static_cast<uint8_t*>(host)[i] == 0xc7, "Copy changed bytes before imported binding");
            for (VkDeviceSize i = bindingOffset + page; i < allocationSize; ++i)
                require(static_cast<uint8_t*>(host)[i] == 0xc7, "Copy changed bytes after imported binding");
            api.DestroyFence(device, fence, nullptr);
            api.DestroyCommandPool(device, pool, nullptr);
            api.UnmapMemory(device, ordinaryMemory);
            api.DestroyBuffer(device, ordinaryBuffer, nullptr);
            api.DestroyBuffer(device, importedBuffer, nullptr);
            api.FreeMemory(device, ordinaryMemory, nullptr);
            api.FreeMemory(device, importedMemory, nullptr);
            // Importing a pointer must not transfer ownership of the CPU allocation.
            require(std::memcmp(importedBytes, expected.data(), page) == 0, "Host bytes changed during native destruction");
            std::free(host);
            api.DestroyDevice(device, nullptr);
            api.DestroyInstance(instance, nullptr);
            dlclose(library);
            report[@"status"] = @"passed";
            report[@"bytes_each_direction"] = @(page);
            report[@"binding_offset"] = @(bindingOffset);
            report[@"fence_submissions"] = @2;
            report[@"cpu_allocation_survived_native_destruction"] = @YES;
            writeReport(report, argv[3]);
            return 0;
        } catch (const std::exception& error) {
            report[@"reason"] = [NSString stringWithUTF8String:error.what()];
            writeReport(report, argv[3]);
            fprintf(stderr, "%s\n", error.what());
            // Failure is process-isolated. Do not perform an unbounded device-idle
            // cleanup wait after a fence timeout; the supervising runner kills it.
            return 1;
        }
    }
}
