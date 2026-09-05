using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    interface IAuto
    {
        bool HasCommandBufferDependency(CommandBufferScoped cbs);

        void IncrementReferenceCount();
        void DecrementReferenceCount(int cbIndex);
        void DecrementReferenceCount();
    }

    interface IAutoPrivate : IAuto
    {
        void AddCommandBufferDependencies(CommandBufferScoped cbs);
    }

    interface IMirrorable<T> where T : IDisposable
    {
        Auto<T> GetMirrorable(CommandBufferScoped cbs, ref int offset, int size, out bool mirrored);
        void ClearMirrors(CommandBufferScoped cbs, int offset, int size);
    }

    class Auto<T> : IAutoPrivate, IDisposable where T : IDisposable
    {
        private int _referenceCount;
        private T _value;

        private readonly BitMap _cbOwnership;
        private MultiFenceHolder _waitable;
        private IAutoPrivate[] _referencedObjs;
        private IMirrorable<T> _mirrorable;

        private bool _disposed;
        private bool _destroyed;

        public Auto(T value)
        {
            _referenceCount = 1;
            _value = value;
            _cbOwnership = new BitMap(CommandBufferPool.MaxCommandBuffers);
        }

        public Auto(T value, IMirrorable<T> mirrorable, MultiFenceHolder waitable, params IAutoPrivate[] referencedObjs) : this(value, waitable, referencedObjs)
        {
            _mirrorable = mirrorable;
        }

        public Auto(T value, MultiFenceHolder waitable, params IAutoPrivate[] referencedObjs) : this(value)
        {
            _waitable = waitable;
            _referencedObjs = referencedObjs;

            for (int i = 0; i < referencedObjs.Length; i++)
            {
                referencedObjs[i].IncrementReferenceCount();
            }
        }

        public T GetMirrorable(CommandBufferScoped cbs, ref int offset, int size, out bool mirrored)
        {
            // Binding caches can retain a wrapper after its last resource reference is
            // retired. Match Get's existing empty-value contract without reviving or
            // calling the disposed mirror owner.
            if (_destroyed)
            {
                mirrored = false;
                return default;
            }

            Auto<T> mirror = _mirrorable.GetMirrorable(cbs, ref offset, size, out mirrored);
            mirror._waitable?.AddBufferUse(cbs.CommandBufferIndex, offset, size, false);
            return mirror.Get(cbs);
        }

        public T Get(CommandBufferScoped cbs, int offset, int size, bool write = false)
        {
            if (_destroyed)
            {
                return default;
            }

            _mirrorable?.ClearMirrors(cbs, offset, size);
            _waitable?.AddBufferUse(cbs.CommandBufferIndex, offset, size, write);
            return Get(cbs);
        }

        public T GetUnsafe()
        {
            return _value;
        }

        public T Get(CommandBufferScoped cbs)
        {
            if (!_destroyed)
            {
                AddCommandBufferDependencies(cbs);
            }

            return _value;
        }

        public bool HasCommandBufferDependency(CommandBufferScoped cbs)
        {
            return _cbOwnership.IsSet(cbs.CommandBufferIndex);
        }

        public bool HasRentedCommandBufferDependency(CommandBufferPool cbp)
        {
            return _cbOwnership.AnySet();
        }

        public void AddCommandBufferDependencies(CommandBufferScoped cbs)
        {
            // We don't want to add a reference to this object to the command buffer
            // more than once, so if we detect that the command buffer already has ownership
            // of this object, then we can just return without doing anything else.
            if (_cbOwnership.Set(cbs.CommandBufferIndex))
            {
                if (_waitable != null)
                {
                    cbs.AddWaitable(_waitable);
                }

                cbs.AddDependant(this);

                // We need to add a dependency on the command buffer to all objects this object
                // references aswell.
                if (_referencedObjs != null)
                {
                    for (int i = 0; i < _referencedObjs.Length; i++)
                    {
                        _referencedObjs[i].AddCommandBufferDependencies(cbs);
                    }
                }
            }
        }

        public bool TryIncrementReferenceCount()
        {
            int lastValue;
            do
            {
                lastValue = _referenceCount;

                if (lastValue == 0)
                {
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _referenceCount, lastValue + 1, lastValue) != lastValue);

            return true;
        }

        public void IncrementReferenceCount()
        {
            if (Interlocked.Increment(ref _referenceCount) == 1)
            {
                Interlocked.Decrement(ref _referenceCount);
                throw new InvalidOperationException("Attempted to increment the reference count of an object that was already destroyed.");
            }
        }

        public void DecrementReferenceCount(int cbIndex)
        {
            _cbOwnership.Clear(cbIndex);
            DecrementReferenceCount();
        }

        public void DecrementReferenceCount()
        {
            if (Interlocked.Decrement(ref _referenceCount) == 0)
            {
                T value = _value;
                IAutoPrivate[] referencedObjs = _referencedObjs;

                // Dispose() may only relinquish the creator's reference while command
                // buffers are still using this resource. Detach these managed roots ONLY
                // here, at the existing final native-release boundary. A stale binding
                // can retain this Auto, but must not retain a BufferHolder's upload data,
                // usage bitmap or a chain of already-retired resource wrappers.
                _value = default;
                _mirrorable = null;
                _waitable = null;
                _referencedObjs = null;
                _destroyed = true;

                ExceptionDispatchInfo failure = null;
                try
                {
                    value.Dispose();
                }
                catch (Exception error)
                {
                    failure = ExceptionDispatchInfo.Capture(error);
                }

                // Every dependency acquired in the constructor must be released even if
                // a native destructor throws. Preserve the first failure for the caller.
                if (referencedObjs != null)
                {
                    for (int i = 0; i < referencedObjs.Length; i++)
                    {
                        try
                        {
                            referencedObjs[i].DecrementReferenceCount();
                        }
                        catch (Exception error)
                        {
                            failure ??= ExceptionDispatchInfo.Capture(error);
                        }
                    }
                }

                failure?.Throw();
            }

            Debug.Assert(_referenceCount >= 0);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                // A throwing final destructor must not permit a second Dispose to
                // decrement the already-zero reference count again.
                _disposed = true;
                DecrementReferenceCount();
            }
        }
    }
}
