using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace EZSnap
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // --selftest <目录>：不截屏，用合成图片渲染界面、检查跨屏拼接等逻辑，结果写进 selftest.log，失败时退出码非 0。
            var i = Array.IndexOf(args, "--selftest");
            if (i >= 0) return SelfTest.Run(args.Length > i + 1 ? args[i + 1] : Path.GetTempPath());

            using (var running = new Mutex(true, @"Local\EZSnap.Running", out var first))
            {
                if (!first)
                {
                    // 已经在运行：通知它再提示一次自己在托盘里，然后退出。
                    try
                    {
                        using (var reopen = EventWaitHandle.OpenExisting(TrayApp.ReopenEventName)) reopen.Set();
                    }
                    catch (WaitHandleCannotBeOpenedException) { }
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
                GC.KeepAlive(running);
            }
            return 0;
        }
    }
}
