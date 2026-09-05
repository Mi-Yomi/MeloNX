namespace Ryujinx.Graphics.Vulkan
{
    internal static class BufferRangeBounds
    {
        /// <summary>
        /// Bounds a nonempty range to its buffer. Vulkan's WholeSize becomes -1
        /// when descriptor ranges enter the renderer's signed byte-count API.
        /// </summary>
        internal static bool TryNormalize(int bufferSize, int offset, ref int size)
        {
            if (bufferSize <= 0 || offset < 0 || offset >= bufferSize || size == 0 || size < -1)
            {
                return false;
            }

            // Subtract only after validating offset; offset + size may overflow.
            int remaining = bufferSize - offset;
            if (size == -1 || size > remaining)
            {
                size = remaining;
            }

            return true;
        }
    }
}
