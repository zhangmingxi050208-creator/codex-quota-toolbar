using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexQuotaToolbarInstaller
{
    internal static class Program
    {
        private const string ProductName = "Codex 额度工具条";
        private const string AppFileName = "CodexQuotaToolbar.exe";
        private const string ResourceName = "CodexQuotaToolbar.Payload";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexQuotaToolbar";
        private const int MoveFileDelayUntilReboot = 0x4;

        private static string InstallDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CodexQuotaToolbar"); }
        }

        private static string InstalledApp
        {
            get { return Path.Combine(InstallDirectory, AppFileName); }
        }

        private static string InstalledUninstaller
        {
            get { return Path.Combine(InstallDirectory, "Uninstall.exe"); }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                if (HasArgument(args, "/cleanup"))
                {
                    FinishUninstall(args);
                    return;
                }

                if (HasArgument(args, "/uninstall"))
                {
                    BeginUninstall(HasArgument(args, "/silent"));
                    return;
                }

                Install(HasArgument(args, "/silent"));
            }
            catch (Exception ex)
            {
                MessageBox.Show("操作失败：\r\n" + ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
        }

        private static void Install(bool silent)
        {
            if (!silent)
            {
                var answer = MessageBox.Show(
                    "将把“Codex 额度工具条”安装到当前账户，并设置为登录 Windows 后自动启动。\r\n\r\n是否继续？",
                    ProductName,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }

            StopToolbarProcesses();
            Directory.CreateDirectory(InstallDirectory);

            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (resource == null) throw new InvalidOperationException("安装包中缺少程序文件。");
                using (var output = new FileStream(InstalledApp, FileMode.Create, FileAccess.Write, FileShare.None))
                    resource.CopyTo(output);
            }

            File.Copy(Application.ExecutablePath, InstalledUninstaller, true);
            CreateShortcuts();
            WriteUninstallEntry();

            Process.Start(new ProcessStartInfo { FileName = InstalledApp, UseShellExecute = true });
            if (!silent)
                MessageBox.Show(
                    "安装完成。\r\n\r\n工具条会随 Codex 显示，并在登录 Windows 后自动启动。",
                    ProductName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
        }

        private static void BeginUninstall(bool silent)
        {
            if (!silent)
            {
                var answer = MessageBox.Show(
                    "是否卸载“Codex 额度工具条”？\r\n\r\n这不会删除 Codex 或任何对话。",
                    ProductName,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }

            StopToolbarProcesses();
            DeleteShortcuts();
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

            var helper = Path.Combine(Path.GetTempPath(), "CodexQuotaToolbar-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(Application.ExecutablePath, helper, true);
            var arguments = "/cleanup \"" + InstallDirectory + "\"" + (silent ? " /silent" : "");
            Process.Start(new ProcessStartInfo { FileName = helper, Arguments = arguments, UseShellExecute = false });
        }

        private static void FinishUninstall(string[] args)
        {
            var installDirectory = ArgumentAfter(args, "/cleanup");
            if (string.IsNullOrWhiteSpace(installDirectory)) throw new InvalidOperationException("缺少卸载目录。");

            var expected = Path.GetFullPath(InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var requested = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(expected, requested, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("卸载目录校验失败。");

            Thread.Sleep(900);
            if (Directory.Exists(requested)) Directory.Delete(requested, true);

            if (!HasArgument(args, "/silent"))
                MessageBox.Show("已卸载 Codex 额度工具条。", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);

            MoveFileEx(Application.ExecutablePath, null, MoveFileDelayUntilReboot);
        }

        private static void CreateShortcuts()
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            CreateShortcut(Path.Combine(programs, ProductName + ".lnk"), InstalledApp);
            CreateShortcut(Path.Combine(startup, ProductName + ".lnk"), InstalledApp);
        }

        private static void DeleteShortcuts()
        {
            var paths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName + ".lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ProductName + ".lnk")
            };
            foreach (var path in paths)
                if (File.Exists(path)) File.Delete(path);
        }

        private static void CreateShortcut(string shortcutPath, string targetPath)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("无法创建 Windows 快捷方式。");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                dynamic shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                shortcut.TargetPath = targetPath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
                shortcut.IconLocation = targetPath + ",0";
                shortcut.Description = ProductName;
                shortcut.Save();
                Marshal.FinalReleaseComObject(shortcut);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void WriteUninstallEntry()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                if (key == null) throw new InvalidOperationException("无法创建卸载信息。");
                key.SetValue("DisplayName", ProductName);
                key.SetValue("DisplayVersion", "1.0.0");
                key.SetValue("Publisher", "Codex Quota Toolbar");
                key.SetValue("InstallLocation", InstallDirectory);
                key.SetValue("DisplayIcon", InstalledApp + ",0");
                key.SetValue("UninstallString", "\"" + InstalledUninstaller + "\" /uninstall");
                key.SetValue("QuietUninstallString", "\"" + InstalledUninstaller + "\" /uninstall /silent");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", 80, RegistryValueKind.DWord);
            }
        }

        private static void StopToolbarProcesses()
        {
            foreach (var process in Process.GetProcessesByName("CodexQuotaToolbar"))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch { }
                finally { process.Dispose(); }
            }
        }

        private static bool HasArgument(string[] args, string expected)
        {
            foreach (var value in args)
                if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ArgumentAfter(string[] args, string expected)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], expected, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
    }
}

