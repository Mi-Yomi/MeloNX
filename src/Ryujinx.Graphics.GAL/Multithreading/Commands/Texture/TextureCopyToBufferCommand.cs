using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;
using System;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands.Texture
{
    struct TextureCopyToBufferCommand : IGALCommand, IGALCommand<TextureCopyToBufferCommand>
    {
        public readonly CommandType CommandType => CommandType.TextureCopyToBuffer;
        private TableRef<ThreadedTexture> _texture;
        private BufferRange _range;
        private int _layer;
        private int _level;
        private int _stride;

        public void Set(TableRef<ThreadedTexture> texture, BufferRange range, int layer, int level, int stride)
        {
            _texture = texture;
            _range = range;
            _layer = layer;
            _level = level;
            _stride = stride;
        }

        public static void Run(ref TextureCopyToBufferCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            ThreadedTexture texture = command._texture.Get(threaded);

            if (!threaded.Buffers.TryMapBufferRange(command._range, out BufferRange mappedRange))
            {
                var buffers = threaded.Buffers.GetDiagnostics();
                string diagnostic =
                    $"Texture-to-buffer copy referenced an unmapped threaded buffer: " +
                    $"threaded_handle=0x{(int)command._range.Handle:X8}, offset={command._range.Offset}, " +
                    $"size={command._range.Size}, write={command._range.Write}, layer={command._layer}, " +
                    $"level={command._level}, stride={command._stride}, " +
                    $"texture_wrapper=0x{(texture == null ? 0 : RuntimeHelpers.GetHashCode(texture)):X8}, " +
                    $"texture_base={texture?.Base?.GetType().Name ?? "null"}, buffers_issued={buffers.Issued}, " +
                    $"buffers_mapped={buffers.Mapped}, buffers_in_flight={buffers.InFlight}, " +
                    $"buffer_map_misses={buffers.Misses}, {threaded.GetDiagnosticSnapshot()}.";
                Logger.Log log = Logger.Error ?? Logger.Notice;
                log.Print(LogClass.Gpu, diagnostic);
                throw new InvalidOperationException(diagnostic);
            }

            if (texture?.Base == null)
            {
                string diagnostic =
                    $"Texture-to-buffer copy has no backend texture: " +
                    $"threaded_handle=0x{(int)command._range.Handle:X8}, mapped_handle=0x{(int)mappedRange.Handle:X8}, " +
                    $"offset={command._range.Offset}, size={command._range.Size}, layer={command._layer}, " +
                    $"level={command._level}, stride={command._stride}, {threaded.GetDiagnosticSnapshot()}.";
                Logger.Log log = Logger.Error ?? Logger.Notice;
                log.Print(LogClass.Gpu, diagnostic);
                throw new InvalidOperationException(diagnostic);
            }

            texture.Base.CopyTo(mappedRange, command._layer, command._level, command._stride);
        }
    }
}
