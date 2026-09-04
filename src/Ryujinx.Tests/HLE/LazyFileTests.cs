using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.RomFs;
using NUnit.Framework;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS.Services.Fs.FileSystemProxy;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IFile = LibHac.Fs.Fsa.IFile;
using IFileSystem = LibHac.Fs.Fsa.IFileSystem;
using IStorage = LibHac.Fs.IStorage;
using Path = System.IO.Path;

namespace Ryujinx.Tests.HLE
{
    public class LazyFileTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "Ryujinx-LazyFileTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_directory, true);
        }

        [Test]
        public void MetadataAndReadsReleaseHostFilesWithoutDisposingTheView()
        {
            string path = Path.Combine(_directory, "asset.dat");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            using SharedRef<IFileSystem> fs = new(new LocalFileSystem(_directory));
            using LazyFile file = new("/asset.dat", in fs);

            file.GetSize(out long size).ThrowIfFailure();
            Assert.AreEqual(4, size);
            AssertExclusiveOpen(path);

            byte[] output = new byte[2];
            file.Read(out long read, 1, output).ThrowIfFailure();
            Assert.AreEqual(2, read);
            CollectionAssert.AreEqual(new byte[] { 2, 3 }, output);
            AssertExclusiveOpen(path);

            file.Read(out read, 3, output).ThrowIfFailure();
            Assert.AreEqual(1, read);
            Assert.AreEqual(4, output[0]);
            AssertExclusiveOpen(path);
        }

        [Test]
        public void ManyAssetsCanBeRereadWithoutRetainingTheirHandles()
        {
            const int count = 512;
            using SharedRef<IFileSystem> fs = new(new LocalFileSystem(_directory));
            LazyFile[] files = new LazyFile[count];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    string name = $"asset-{i:D4}.dat";
                    File.WriteAllBytes(Path.Combine(_directory, name), BitConverter.GetBytes(i));
                    files[i] = new LazyFile("/" + name, in fs);
                }

                byte[] output = new byte[4];
                foreach (int i in Enumerable.Range(0, count).Concat(Enumerable.Range(0, count).Reverse()))
                {
                    files[i].GetSize(out long size).ThrowIfFailure();
                    Assert.AreEqual(4, size);
                    files[i].Read(out long read, 0, output).ThrowIfFailure();
                    Assert.AreEqual(4, read);
                    Assert.AreEqual(i, BitConverter.ToInt32(output));
                    AssertExclusiveOpen(Path.Combine(_directory, $"asset-{i:D4}.dat"));
                }
            }
            finally
            {
                foreach (LazyFile file in files) { file?.Dispose(); }
            }
        }

        [Test]
        public void RomFsContainerSizeUsesVfsAndSurvivesCreatorScope()
        {
            string path = Path.Combine(_directory, "romfs.bin");
            CreateRomFs(path);
            LazyFile file;
            using (SharedRef<IStorage> storage = new(File.OpenRead(path).AsStorage(false)))
            using (SharedRef<IFileSystem> fs = new(new RomFsFileSystem(in storage)))
            {
                file = new LazyFile("/inside.dat", in fs);
            }

            using (file)
            {
                file.GetSize(out long size).ThrowIfFailure();
                Assert.AreEqual(4, size);
                byte[] output = new byte[4];
                file.Read(out long read, 0, output).ThrowIfFailure();
                Assert.AreEqual(4, read);
                CollectionAssert.AreEqual(new byte[] { 9, 8, 7, 6 }, output);
            }

            AssertExclusiveOpen(path);
        }

        [Test]
        public void MissingFileReturnsVfsErrorAndDisposedViewCannotReopen()
        {
            using SharedRef<IFileSystem> fs = new(new LocalFileSystem(_directory));
            using LazyFile file = new("/missing.dat", in fs);
            Assert.IsTrue(ResultFs.PathNotFound.Includes(file.GetSize(out long size)));
            Assert.AreEqual(0, size);
            Assert.IsTrue(ResultFs.PathNotFound.Includes(file.Read(out long read, 0, new byte[1])));
            Assert.AreEqual(0, read);
            file.Dispose();
            file.Dispose();
            Assert.Throws<ObjectDisposedException>(() => file.GetSize(out _));
            Assert.Throws<ObjectDisposedException>(() => file.Read(out _, 0, new byte[1]));
        }

        [Test]
        public void RebuiltRomFsOwnsInputsUntilItsStreamCloses()
        {
            string path = Path.Combine(_directory, "romfs.bin");
            CreateRomFs(path);
            IStorage rebuilt;
            LazyFile input;
            using (OwnedRomFsBuilder builder = new())
            {
                using (SharedRef<IStorage> storage = new(File.OpenRead(path).AsStorage(false)))
                using (SharedRef<IFileSystem> fs = new(new RomFsFileSystem(in storage)))
                {
                    input = new LazyFile("/inside.dat", in fs);
                    builder.AddFile("/inside.dat", input);
                }

                rebuilt = builder.Build();
            }

            // This is the ownership transfer used by NcaExtensions -> VirtualFileSystem.
            using (Stream stream = rebuilt.AsStream(FileAccess.Read, false))
            using (RomFsFileSystem fs = new(rebuilt))
            using (UniqueRef<IFile> file = new())
            {
                fs.OpenFile(ref file.Ref, "/inside.dat".ToU8Span(), OpenMode.Read).ThrowIfFailure();
                byte[] output = new byte[4];
                file.Get.Read(out long read, 0, output).ThrowIfFailure();
                Assert.AreEqual(4, read);
                CollectionAssert.AreEqual(new byte[] { 9, 8, 7, 6 }, output);
            }

            AssertExclusiveOpen(path);
            Assert.Throws<ObjectDisposedException>(() => input.GetSize(out _));
            Assert.Throws<ObjectDisposedException>(() => rebuilt.GetSize(out _));
            rebuilt.Dispose();
        }

        [Test]
        public void FailedBuildReleasesInputsWhenBuilderScopeExits()
        {
            File.WriteAllBytes(Path.Combine(_directory, "asset.dat"), [1]);
            using SharedRef<IFileSystem> fs = new(new LocalFileSystem(_directory));
            LazyFile input = new("/asset.dat", in fs);
            LazyFile missing = new("/missing.dat", in fs);
            Assert.Catch(() =>
            {
                using OwnedRomFsBuilder builder = new();
                builder.AddFile("/asset.dat", input);
                builder.AddFile("/missing.dat", missing);
            });
            Assert.Throws<ObjectDisposedException>(() => input.GetSize(out _));
            Assert.Throws<ObjectDisposedException>(() => missing.GetSize(out _));
            AssertExclusiveOpen(Path.Combine(_directory, "asset.dat"));
        }

        [Test]
        public void RebuiltRomFsOwnsBaseStorage()
        {
            string path = Path.Combine(_directory, "romfs.bin");
            CreateRomFs(path);
            using IStorage baseStorage = File.OpenRead(path).AsStorage(false);
            using OwnedRomFsBuilder builder = new();
            using (RomFsFileSystem fs = new(baseStorage))
            using (UniqueRef<IFile> file = new())
            {
                fs.OpenFile(ref file.Ref, "/inside.dat".ToU8Span(), OpenMode.Read).ThrowIfFailure();
                builder.AddFile("/inside.dat", file.Release());
            }

            using (IStorage rebuilt = builder.Build(baseStorage))
            using (RomFsFileSystem fs = new(rebuilt))
            using (UniqueRef<IFile> file = new())
            {
                fs.OpenFile(ref file.Ref, "/inside.dat".ToU8Span(), OpenMode.Read).ThrowIfFailure();
                byte[] output = new byte[4];
                file.Get.Read(out long read, 0, output).ThrowIfFailure();
                Assert.AreEqual(4, read);
                CollectionAssert.AreEqual(new byte[] { 9, 8, 7, 6 }, output);
            }

            AssertExclusiveOpen(path);
        }

        [Test]
        public void ConcurrentReadsAndDisposeDoNotUseClosedFiles()
        {
            string path = Path.Combine(_directory, "asset.dat");
            byte[] data = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
            File.WriteAllBytes(path, data);
            using SharedRef<IFileSystem> fs = new(new LocalFileSystem(_directory));
            using LazyFile file = new("/asset.dat", in fs);

            // First verify independent offsets with concurrent callers.
            Parallel.For(0, 256, offset =>
            {
                byte[] output = new byte[1];
                file.Read(out long read, offset, output).ThrowIfFailure();
                Assert.AreEqual(1, read);
                Assert.AreEqual(data[offset], output[0]);
            });

            using ManualResetEventSlim start = new(false);
            Task[] readers = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < 100; i++)
                {
                    try
                    {
                        byte[] output = new byte[1];
                        file.Read(out long read, index, output).ThrowIfFailure();
                        Assert.AreEqual(1, read);
                        Assert.AreEqual(data[index], output[0]);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            })).ToArray();
            start.Set();
            file.Dispose();
            Task.WaitAll(readers);
            AssertExclusiveOpen(path);
            Assert.Throws<ObjectDisposedException>(() => file.GetSize(out _));
        }

        private void CreateRomFs(string destination)
        {
            string sourceDirectory = Path.Combine(_directory, "source");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllBytes(Path.Combine(sourceDirectory, "inside.dat"), [9, 8, 7, 6]);
            using LocalFileSystem source = new(sourceDirectory);
            using UniqueRef<IFile> file = new();
            source.OpenFile(ref file.Ref, "/inside.dat".ToU8Span(), OpenMode.Read).ThrowIfFailure();
            RomFsBuilder builder = new();
            builder.AddFile("/inside.dat", file.Get);
            using IStorage storage = builder.Build();
            storage.GetSize(out long size).ThrowIfFailure();
            byte[] bytes = new byte[size];
            storage.Read(0, bytes).ThrowIfFailure();
            File.WriteAllBytes(destination, bytes);
        }

        private static void AssertExclusiveOpen(string path)
        {
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.IsTrue(file.CanRead);
        }
    }
}
