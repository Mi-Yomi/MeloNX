using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Vulkan;
using System;

namespace Ryujinx.Tests.Graphics
{
    public class DescriptorPoolSizingTests
    {
        [Test]
        public void FirstDescriptorRangeIsSizedForEverySetInPool()
        {
            ResourceDescriptorCollection descriptors = CreateDescriptors(
                new ResourceDescriptor(0, 3, ResourceType.TextureAndSampler, ResourceStages.Fragment));
            Span<DescriptorPoolSize> output = stackalloc DescriptorPoolSize[8];

            DescriptorPoolSize[] poolSizes = PipelineLayoutCacheEntry.GetDescriptorPoolSizes(output, descriptors, 8).ToArray();

            Assert.That(poolSizes, Has.Length.EqualTo(1));
            Assert.That(poolSizes[0].Type, Is.EqualTo(DescriptorType.CombinedImageSampler));
            Assert.That(poolSizes[0].DescriptorCount, Is.EqualTo(24));
        }

        [Test]
        public void DescriptorRangesOfSameTypeAreAggregatedForEverySetInPool()
        {
            ResourceDescriptorCollection descriptors = CreateDescriptors(
                new ResourceDescriptor(0, 2, ResourceType.Texture, ResourceStages.Vertex),
                new ResourceDescriptor(1, 5, ResourceType.Texture, ResourceStages.Fragment));
            Span<DescriptorPoolSize> output = stackalloc DescriptorPoolSize[8];

            DescriptorPoolSize[] poolSizes = PipelineLayoutCacheEntry.GetDescriptorPoolSizes(output, descriptors, 8).ToArray();

            Assert.That(poolSizes, Has.Length.EqualTo(1));
            Assert.That(poolSizes[0].Type, Is.EqualTo(DescriptorType.SampledImage));
            Assert.That(poolSizes[0].DescriptorCount, Is.EqualTo(56));
        }

        [Test]
        public void DistinctDescriptorTypesAreEachSizedForEverySetInPool()
        {
            ResourceDescriptorCollection descriptors = CreateDescriptors(
                new ResourceDescriptor(0, 2, ResourceType.UniformBuffer, ResourceStages.Vertex),
                new ResourceDescriptor(1, 4, ResourceType.StorageBuffer, ResourceStages.Compute));
            Span<DescriptorPoolSize> output = stackalloc DescriptorPoolSize[8];

            DescriptorPoolSize[] poolSizes = PipelineLayoutCacheEntry.GetDescriptorPoolSizes(output, descriptors, 8).ToArray();

            Assert.That(poolSizes, Has.Length.EqualTo(2));
            Assert.That(poolSizes[0].Type, Is.EqualTo(DescriptorType.UniformBuffer));
            Assert.That(poolSizes[0].DescriptorCount, Is.EqualTo(16));
            Assert.That(poolSizes[1].Type, Is.EqualTo(DescriptorType.StorageBuffer));
            Assert.That(poolSizes[1].DescriptorCount, Is.EqualTo(32));
        }

        private static ResourceDescriptorCollection CreateDescriptors(params ResourceDescriptor[] descriptors)
        {
            return new ResourceDescriptorCollection(Array.AsReadOnly(descriptors));
        }
    }
}
