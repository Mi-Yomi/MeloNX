using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands
{
    struct TryHostConditionalRenderingCommand : IGALCommand, IGALCommand<TryHostConditionalRenderingCommand>
    {
        public readonly CommandType CommandType => CommandType.TryHostConditionalRendering;
        private TableRef<ThreadedCounterEvent> _value;
        private TableRef<ResultBox<bool>> _result;
        private ulong _compare;
        private bool _isEqual;

        public void Set(TableRef<ThreadedCounterEvent> value, TableRef<ResultBox<bool>> result, ulong compare, bool isEqual)
        {
            _value = value;
            _result = result;
            _compare = compare;
            _isEqual = isEqual;
        }

        public static void Run(ref TryHostConditionalRenderingCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            ICounterEvent value = command._value.Get(threaded)?.Base;
            ResultBox<bool> result = command._result.Get(threaded);
            result.Result = renderer.Pipeline.TryHostConditionalRendering(value, command._compare, command._isEqual);
        }
    }
}
