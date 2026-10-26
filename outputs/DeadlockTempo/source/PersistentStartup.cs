using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeadlockTempo
{
    // Windows owns this ordinary, unelevated interactive task, so the watcher
    // survives its launcher and is recovered even when both Tempo processes stop.
    internal static class PersistentStartup
    {
        internal const string TaskName = "DeadlockTempo Watcher";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);

        private static string PhysicalFile(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var buffer = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(file.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot resolve startup path: " + path);
                string resolved = buffer.ToString();
                return resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + resolved.Substring(8) :
                    resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved.Substring(4) : resolved;
            }
        }

        internal static string Configure(bool enabled, string executable, string enabledFile)
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            dynamic root = service.GetFolder(@"\");
            if (!enabled)
            {
                try { root.GetTask(TaskName).Enabled = false; }
                catch (COMException ex) { if ((uint)ex.ErrorCode != 0x80070002) throw; }
                return executable;
            }

            // A launcher packaged by Windows may redirect LocalAppData. Resolve
            // real paths once and preserve the SAME settings for scheduled starts.
            executable = PhysicalFile(executable);
            string dataFolder = Path.GetDirectoryName(PhysicalFile(enabledFile));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable), "data-directory.txt"), dataFolder, new UTF8Encoding(false));
            string sid = WindowsIdentity.GetCurrent().User.Value;
            dynamic task = service.NewTask(0);
            task.RegistrationInfo.Description = "Deadlock Tempo: start with Deadlock and recover the watcher while autostart is enabled.";
            task.Principal.UserId = sid;
            task.Principal.LogonType = 3; // InteractiveToken: this user's existing session, no password.
            task.Principal.RunLevel = 0; // LeastPrivilege.
            // A surviving overlay child can keep an old task instance running
            // after its watcher dies. Allow recovery probes; the watcher mutex
            // makes every probe exit immediately while a healthy watcher exists.
            task.Settings.MultipleInstances = 0;
            task.Settings.ExecutionTimeLimit = "PT0S";
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.StartWhenAvailable = true;
            task.Settings.RestartInterval = "PT1M";
            task.Settings.RestartCount = 3;
            dynamic logon = task.Triggers.Create(9);
            logon.UserId = sid;
            dynamic recovery = task.Triggers.Create(1);
            recovery.StartBoundary = DateTime.Now.AddSeconds(5).ToString("s");
            recovery.Repetition.Interval = "PT1M";
            dynamic action = task.Actions.Create(0);
            action.Path = executable;
            action.Arguments = "--watch";
            action.WorkingDirectory = Path.GetDirectoryName(executable);
            dynamic registered = root.RegisterTaskDefinition(TaskName, task, 6, sid, null, 3, null);
            registered.Run(null);
            GameAutoStart.Log("Windows recovery task enabled; executable " + executable + "; data " + dataFolder);
            return executable;
        }
    }
}
