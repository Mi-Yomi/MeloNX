using LibHac;
using LibHac.Common;
using LibHac.Fs;
using System;
using VfsFile = LibHac.Fs.Fsa.IFile;
using VfsFileSystem = LibHac.Fs.Fsa.IFileSystem;

namespace Ryujinx.HLE.HOS.Services.Fs.FileSystemProxy
{
    class LazyFile : VfsFile
    {
        private readonly object _lock = new();
        private SharedRef<VfsFileSystem> _fileSystem;
        private readonly string _filePath;
        private bool _disposed;

        public LazyFile(string filePath, in SharedRef<VfsFileSystem> fileSystem)
        {
            _fileSystem = SharedRef<VfsFileSystem>.CreateCopy(in fileSystem);
            _filePath = filePath;
        }

        protected override Result DoRead(out long bytesRead, long offset, Span<byte> destination, in ReadOption option)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                bytesRead = 0;
                // RomFS views can retain thousands of assets. Keep the host file open
                // only for this operation, instead of retaining a handle per asset.
                using UniqueRef<VfsFile> file = new();
                Result result = _fileSystem.Get.OpenFile(ref file.Ref, _filePath.ToU8Span(), OpenMode.Read);
                if (result.IsFailure())
                {
                    return result;
                }

                return file.Get.Read(out bytesRead, offset, destination, in option);
            }
        }

        protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option)
        {
            throw new NotSupportedException();
        }

        protected override Result DoFlush()
        {
            throw new NotSupportedException();
        }

        protected override Result DoSetSize(long size)
        {
            throw new NotSupportedException();
        }

        protected override Result DoGetSize(out long size)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                // Building a RomFS asks for every asset's size. Keep these opens temporary
                // so metadata collection does not retain one host handle per loose file.
                using UniqueRef<VfsFile> file = new();
                Result result = _fileSystem.Get.OpenFile(ref file.Ref, _filePath.ToU8Span(), OpenMode.Read);
                if (result.IsFailure())
                {
                    size = 0;
                    return result;
                }

                return file.Get.GetSize(out size);
            }
        }

        protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer)
        {
            throw new NotSupportedException();
        }

        public override void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _fileSystem.Destroy();
                base.Dispose();
            }
        }
    }
}
