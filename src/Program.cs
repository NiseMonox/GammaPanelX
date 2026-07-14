using System;
using System.Threading;
using System.Windows.Forms;

namespace GammaPanelX
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "GammaPanelX_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // 已有实例在运行: 通知它把窗口调到前台, 然后退出
                    uint msg = NativeMethods.RegisterWindowMessage("GammaPanelX_ShowWindow");
                    if (msg != 0)
                        NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, msg, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
