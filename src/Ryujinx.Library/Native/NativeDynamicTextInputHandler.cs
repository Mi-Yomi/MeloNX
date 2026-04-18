using Ryujinx.HLE.UI;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Library
{
    /// <summary>
    /// iOS text processing class, TODO: Implement this once callbacks are finished
    /// </summary>
    internal class NativeDynamicTextInputHandler : IDynamicTextInputHandler
    {
        private bool _canProcessInput;

        public event DynamicTextChangedHandler TextChangedEvent;
        public event KeyPressedHandler KeyPressedEvent { add { } remove { } }
        public event KeyReleasedHandler KeyReleasedEvent { add { } remove { } }

        public bool TextProcessingEnabled
        {
            get => Volatile.Read(ref _canProcessInput);

            set
            {
                Volatile.Write(ref _canProcessInput, value);

                // Launch a task to update the text.
                Task.Run(() =>
                {
                    Thread.Sleep(100);
                    TextChangedEvent?.Invoke("MeloNX", 7, 7, false);
                });
            }
        }

        public NativeDynamicTextInputHandler()
        {
            // Start with input processing turned off so the text box won't accumulate text
            // if the user is playing on the keyboard.
            _canProcessInput = false;
        }

        public void SetText(string text, int cursorBegin) { }

        public void SetText(string text, int cursorBegin, int cursorEnd) { }

        public void Dispose() { }
    }
}
