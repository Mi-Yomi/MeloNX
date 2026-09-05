// Compile against the pinned native headers for arm64 iOS. These offsets are
// consumed by Ryujinx.Graphics.Vulkan/MoltenVK/MVKConfiguration.cs (152 bytes).
#include <stddef.h>
#include <MoltenVK/mvk_private_api.h>
#include <MoltenVK/mvk_deprecated_api.h>

_Static_assert(sizeof(void*) == 8, "MeloNX requires the arm64 configuration ABI");
_Static_assert(MVK_VERSION_MAJOR == 1 && MVK_VERSION_MINOR == 4 && MVK_VERSION_PATCH == 0,
               "Unexpected native MoltenVK baseline");
#define CHECK_OFFSET(field, offset) _Static_assert(offsetof(MVKConfiguration, field) == offset, #field " ABI changed")
CHECK_OFFSET(debugMode, 0);
CHECK_OFFSET(synchronousQueueSubmits, 8);
CHECK_OFFSET(prefillMetalCommandBuffers, 12);
CHECK_OFFSET(maxActiveMetalCommandBuffersPerQueue, 16);
CHECK_OFFSET(metalCompileTimeout, 32);
CHECK_OFFSET(autoGPUCaptureOutputFilepath, 104);
CHECK_OFFSET(texture1DAs2D, 112);
CHECK_OFFSET(useCommandPooling, 120);
CHECK_OFFSET(useMTLHeap, 124);
CHECK_OFFSET(useMetalArgumentBuffers, 144);
CHECK_OFFSET(shaderSourceCompressionAlgorithm, 148);
_Static_assert(offsetof(MVKConfiguration, shaderSourceCompressionAlgorithm) +
               sizeof(((MVKConfiguration*)0)->shaderSourceCompressionAlgorithm) == 152,
               "Managed configuration prefix changed");
_Static_assert(MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS_STYLE_IMMEDIATE_ENCODING == 2,
               "ImmediateEncoding enum changed");
_Static_assert(MVK_CONFIG_COMPRESSION_ALGORITHM_LZFSE == 1, "Compression enum changed");

// Taking addresses also checks declarations/types of the private entry points.
PFN_vkGetMoltenVKConfigurationMVK melonx_get_configuration = vkGetMoltenVKConfigurationMVK;
PFN_vkSetMoltenVKConfigurationMVK melonx_set_configuration = vkSetMoltenVKConfigurationMVK;
