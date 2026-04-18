using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Library
{
    // TODO: Implement this correctly using callbacks instead of RyujinxHelper.framework
    public static class AlertHelper
    {
        //[DllImport("RyujinxHelper.framework/RyujinxHelper", CallingConvention = CallingConvention.Cdecl)]
        //public static extern void showKeyboardAlert(string title, string message, string placeholder);

        //[DllImport("RyujinxHelper.framework/RyujinxHelper", CallingConvention = CallingConvention.Cdecl)]
        //public static extern void showAlert(string title, string message, bool showCancel);

        //[DllImport("RyujinxHelper.framework/RyujinxHelper", CallingConvention = CallingConvention.Cdecl)]
        //private static extern IntPtr getKeyboardInput();

        //[DllImport("RyujinxHelper.framework/RyujinxHelper", CallingConvention = CallingConvention.Cdecl)]
        //private static extern void clearKeyboardInput();

        public static void ShowAlertWithTextInput(string title, string message, string placeholder, Action<string> onTextEntered)
        {
            
            //showKeyboardAlert(title, message, placeholder);

            //ThreadPool.QueueUserWorkItem(_ =>
            //{
            //    string result = null;
            //    while (result == null)
            //    {
            //        Thread.Sleep(100);

            //        IntPtr inputPtr = getKeyboardInput();
            //        if (inputPtr != IntPtr.Zero)
            //        {
            //            result = Marshal.PtrToStringAnsi(inputPtr);
            //            clearKeyboardInput(); 

            //            onTextEntered?.Invoke(result);
            //        }
            //    }
            //});
        }


        public static void ShowAlert(string title, string message, bool cancel) {
            //showAlert(title, message, cancel);
        }
    }
}
