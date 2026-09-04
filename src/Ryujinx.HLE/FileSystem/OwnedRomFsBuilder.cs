using LibHac;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem.RomFs;
using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.FileSystem
{
    // LibHac's builder allows several views of the same inputs and leaves them open.
    // Mod loading produces one view, which must own those inputs until it is closed.
    internal sealed class OwnedRomFsBuilder : IDisposable
    {
        private readonly RomFsBuilder _builder = new();
        private List<IFile> _files = new();

        public void AddFile(string path, IFile file)
        {
            ObjectDisposedException.ThrowIf(_files == null, this);
            _files.Add(file);
            _builder.AddFile(path, file);
        }

        public IStorage Build(IStorage baseStorage = null)
        {
            ObjectDisposedException.ThrowIf(_files == null, this);
            OwnedStorage storage = new(_builder.Build(), _files, baseStorage);
            _files = null;
            return storage;
        }

        public void Dispose()
        {
            if (_files != null)
            {
                foreach (IFile file in _files)
                {
                    file.Dispose();
                }

                _files = null;
            }
        }

        private sealed class OwnedStorage(IStorage storage, List<IFile> files, IStorage baseStorage) : IStorage
        {
            private readonly object _lock = new();
            private bool _disposed;

            public override Result Read(long offset, Span<byte> destination)
            {
                lock (_lock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    return storage.Read(offset, destination);
                }
            }

            public override Result Write(long offset, ReadOnlySpan<byte> source)
            {
                lock (_lock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    return storage.Write(offset, source);
                }
            }

            public override Result Flush()
            {
                lock (_lock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    return storage.Flush();
                }
            }

            public override Result SetSize(long size)
            {
                lock (_lock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    return storage.SetSize(size);
                }
            }

            public override Result GetSize(out long size)
            {
                lock (_lock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    return storage.GetSize(out size);
                }
            }

            public override Result OperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer)
            {
                lock (_lock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    return storage.OperateRange(outBuffer, operationId, offset, size, inBuffer);
                }
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
                    storage.Dispose();
                    foreach (IFile file in files)
                    {
                        file.Dispose();
                    }

                    files.Clear();
                    baseStorage?.Dispose();
                    base.Dispose();
                }
            }
        }
    }
}
