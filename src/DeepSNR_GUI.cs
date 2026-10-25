using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeepSNRGui
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Opt into WPF per-monitor DPI scaling (mixed-DPI multi-monitor setups).
            try { AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false); }
            catch { }
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var win = new MainWindow(args);
            app.Run(win);
        }
    }

    internal static class Paths2
    {
        public static string[] SupportedExtensions =
        {
            ".tif", ".tiff", ".png", ".jpg", ".jpeg", ".fit", ".fits", ".fts", ".fit.gz", ".fits.gz", ".fts.gz"
        };

        public static bool IsFits(string path)
        {
            string p = path.ToLowerInvariant();
            return p.EndsWith(".fit") || p.EndsWith(".fits") || p.EndsWith(".fts")
                || p.EndsWith(".fit.gz") || p.EndsWith(".fits.gz") || p.EndsWith(".fts.gz");
        }

        public static bool IsSupported(string path)
        {
            string p = path.ToLowerInvariant();
            foreach (string ext in SupportedExtensions)
            {
                if (p.EndsWith(ext)) return true;
            }
            return false;
        }

        public static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 1000; i++)
            {
                string candidate = Path.Combine(dir, name + "-" + i.ToString() + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            return path;
        }

        public static string DeriveOutput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            bool gz = input.ToLowerInvariant().EndsWith(".gz");
            string core = gz ? input.Substring(0, input.Length - 3) : input;
            string dir = Path.GetDirectoryName(core);
            string name = Path.GetFileNameWithoutExtension(core);
            string ext = IsFits(input) ? ".fits" : ".tif";
            string result = string.IsNullOrEmpty(dir)
                ? name + "_denoised" + ext
                : Path.Combine(dir, name + "_denoised" + ext);
            return UniquePath(result);
        }

        public static bool ContainsNonAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if (c > 127) return true;
            }
            return false;
        }
    }

    internal sealed class PreviewPane
    {
        public ScrollViewer Scroller;
        public Image Image;
        public TextBlock Placeholder;
        public TextBlock Meta;
        public Button OpenButton;
        public BitmapSource Source;
        public int PixelWidth;
        public int PixelHeight;
        public bool IsFits;
    }

    internal sealed class MainWindow : Window
    {
        private const string AppTitle = "DeepSNR 图形界面（非官方）";
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeepSNR-GUI", "settings.ini");

        private static readonly Brush StatusNormal = MakeFrozenBrush(0x55, 0x5B, 0x66);
        private static readonly Brush StatusWarn = MakeFrozenBrush(0x99, 0x66, 0x00);
        private static readonly Brush StatusOk = MakeFrozenBrush(0x1E, 0x7D, 0x3C);
        private static readonly Brush StatusError = MakeFrozenBrush(0xB0, 0x2A, 0x2A);

        private static Brush MakeFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private readonly ComboBox _modelCombo = new ComboBox();
        private readonly TextBox _inputBox = new TextBox();
        private readonly TextBox _outputBox = new TextBox();
        private readonly TextBox _strideBox = new TextBox();
        private readonly CheckBox _linearCheck = new CheckBox();
        private readonly TextBox _shadowsBox = new TextBox();
        private readonly TextBox _targetBox = new TextBox();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly TextBlock _statusText = new TextBlock();
        private readonly TextBox _logBox = new TextBox();
        private readonly Expander _logExpander = new Expander();
        private readonly TextBlock _queueText = new TextBlock();
        private readonly TextBlock _versionText = new TextBlock();
        private readonly Button _runBtn = new Button();
        private readonly Button _cancelBtn = new Button();
        private readonly Button _openFolderBtn = new Button();
        private readonly CheckBox _syncScrollCheck = new CheckBox();
        private double _zoom; // 0 = fit to window

        private readonly PreviewPane _inputPane = new PreviewPane();
        private readonly PreviewPane _outputPane = new PreviewPane();

        private readonly Dictionary<string, string> _settings =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Process _proc;
        private bool _running;
        private bool _cancelled;
        private bool _suppressAuto;
        private bool _outputManual;
        private bool _syncingScroll;
        private string _exePath = "";
        private string _version = "";
        private string _backend = "";
        private string _lastDir = "";
        private string _outputPath = "";
        private string _tempInputPath;
        private string _lastErrorLine = "";
        private DateTime _startTime;
        private DispatcherTimer _queueTimer;
        private readonly Queue<string> _queue = new Queue<string>();
        private readonly string[] _startArgs;
        private readonly bool _noSettings;

        private static readonly Regex PercentRegex = new Regex(
            @"Working:\s*([0-9]+(?:\.[0-9]+)?)\s*%", RegexOptions.Compiled);

        private static readonly Regex BackendRegex = new Regex(
            @"inference_backend\]:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public MainWindow(string[] args)
        {
            _startArgs = args ?? new string[0];
            foreach (string a in _startArgs)
            {
                if (string.Equals(a, "--no-settings", StringComparison.OrdinalIgnoreCase)) _noSettings = true;
            }
            if (!_noSettings) LoadSettings();
            BuildUi();
            if (!_noSettings) ApplySettings();
            DetectDeepSnr();
            HookEvents();
            if (_startArgs.Length > 0) LoadStartupArgs();

            if (IsLowIntegrity())
            {
                SetStatus("提示：当前以受限权限（Low 完整性）运行，无法写入桌面/系统临时目录。请关闭本窗口，在 Windows 资源管理器里双击 exe 启动。", StatusWarn);
                AppendLog("警告：检测到当前进程以受限（Low 完整性）权限运行，通常说明程序是从“对话窗口 / 文件卡片 / 沙箱环境”启动的。这种模式只能写工作区，处理桌面上的图片会报“访问被拒绝”。请关闭本窗口，在 Windows 资源管理器里直接双击 DeepSNR_GUI.exe 后重试。");
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        /// True when the process token integrity level is Low/Untrusted. A Low-integrity
        /// process can only write to locations explicitly allowing low integrity (e.g. the
        /// sandbox workspace), so writing to the Desktop or the real %TEMP% fails.
        /// </summary>
        private static bool IsLowIntegrity()
        {
            IntPtr tokenHandle = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out tokenHandle)) return false;
                int length = 0;
                GetTokenInformation(tokenHandle, 25, IntPtr.Zero, 0, out length); // TokenIntegrityLevel
                if (length <= 0) return false;
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    int returned = 0;
                    if (!GetTokenInformation(tokenHandle, 25, buffer, length, out returned)) return false;
                    IntPtr sid = Marshal.ReadIntPtr(buffer);
                    byte subAuthorityCount = Marshal.ReadByte(sid, 1);
                    int size = 8 + subAuthorityCount * 4;
                    byte[] sidBytes = new byte[size];
                    Marshal.Copy(sid, sidBytes, 0, size);
                    int rid = BitConverter.ToInt32(sidBytes, size - 4);
                    return rid < 0x2000; // Low (0x1000) or Untrusted (0x0000)
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                if (tokenHandle != IntPtr.Zero) CloseHandle(tokenHandle);
            }
        }

        // ------------------------------------------------------------------
        //  UI construction
        // ------------------------------------------------------------------

        private void BuildUi()
        {
            Title = AppTitle;
            Width = 1180;
            Height = 860;
            MinWidth = 980;
            MinHeight = 720;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
            FontSize = 13;
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF9));
            AllowDrop = true;

            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // --- header ---
            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = "DeepSNR",
                FontSize = 25,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x3C, 0x62)),
                VerticalAlignment = VerticalAlignment.Center
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "  天文图像降噪 · 非官方 GUI",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x7E, 0x88)),
                Margin = new Thickness(2, 8, 0, 0)
            });
            Grid.SetColumn(titleStack, 0);
            header.Children.Add(titleStack);

            var infoStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            _versionText.Text = "正在检测 DeepSNR…";
            _versionText.VerticalAlignment = VerticalAlignment.Center;
            _versionText.Margin = new Thickness(0, 0, 8, 0);
            _versionText.Foreground = StatusNormal;
            AutomationProperties.SetAutomationId(_versionText, "VersionText");
            var licenseBtn = new Button
            {
                Content = "DeepSNR 许可",
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "打开 DeepSNR 官方页面或安装包内的 LICENSE.txt"
            };
            licenseBtn.Click += delegate { OpenDeepSnrLicense(); };
            AutomationProperties.SetAutomationId(licenseBtn, "LicenseButton");

            var locateBtn = new Button
            {
                Content = "更改程序路径…",
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            locateBtn.Click += delegate { BrowseDeepSnrExe(); };
            AutomationProperties.SetAutomationId(locateBtn, "LocateButton");
            infoStack.Children.Add(_versionText);
            infoStack.Children.Add(licenseBtn);
            infoStack.Children.Add(locateBtn);
            Grid.SetColumn(infoStack, 1);
            header.Children.Add(infoStack);

            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // --- file selection ---
            var fileBox = new GroupBox
            {
                Header = "1. 选择图像",
                Padding = new Thickness(10, 6, 10, 10),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var fileGrid = new Grid();
            fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fileGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            fileGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            fileGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddFileRow(fileGrid, 0, "输入图片", _inputBox, "浏览…", BrowseInput, "InputPath", "输入图片路径");
            AddFileRow(fileGrid, 1, "输出图片", _outputBox, "浏览…", BrowseOutput, "OutputPath", "输出图片路径");

            var hint = new TextBlock
            {
                Text = "支持 TIFF / PNG / FITS（JPEG 可用但有损，建议导出 TIFF），至少 512×512；带 alpha 通道的 PNG/TIFF 会自动去掉 alpha 再处理。也可以直接把图片拖进本窗口。Lightroom 用户：先在 LR 里导出 16-bit TIFF（或直接拖出），处理完把 *_denoised.tif 导入 LR。",
                Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0x8C, 0x96)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(hint, 2);
            Grid.SetColumn(hint, 0);
            Grid.SetColumnSpan(hint, 3);
            fileGrid.Children.Add(hint);

            fileBox.Content = fileGrid;
            Grid.SetRow(fileBox, 1);
            root.Children.Add(fileBox);

            // --- options ---
            var optBox = new GroupBox
            {
                Header = "2. 处理选项",
                Padding = new Thickness(10, 6, 10, 10),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var optGrid = new Grid();
            optGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            optGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            optGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var row1 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row1.Children.Add(MakeLabel("模型"));
            _modelCombo.Items.Add("模型 2（默认，彩色 / 灰度）");
            _modelCombo.Items.Add("模型 1（旧模型，仅彩色 RGB）");
            _modelCombo.SelectedIndex = 0;
            _modelCombo.Width = 230;
            _modelCombo.Margin = new Thickness(0, 0, 18, 0);
            _modelCombo.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAutomationId(_modelCombo, "ModelCombo");
            row1.Children.Add(_modelCombo);

            row1.Children.Add(MakeLabel("步长 stride"));
            _strideBox.Width = 60;
            _strideBox.Text = "480";
            _strideBox.Margin = new Thickness(0, 0, 6, 0);
            _strideBox.Padding = new Thickness(4, 2, 4, 2);
            _strideBox.VerticalContentAlignment = VerticalAlignment.Center;
            _strideBox.ToolTip = "默认 480；必须是 2–512 之间的偶数，一般不用改。";
            AutomationProperties.SetAutomationId(_strideBox, "StrideBox");
            row1.Children.Add(_strideBox);
            var strideHint = new TextBlock
            {
                Text = "（默认 480，偶数 2–512）",
                Foreground = Brushes.Gray,
                FontSize = 12,
                Margin = new Thickness(0, 0, 18, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row1.Children.Add(strideHint);

            _linearCheck.Content = "线性 FITS（--linear）";
            _linearCheck.VerticalAlignment = VerticalAlignment.Center;
            _linearCheck.ToolTip = "只对未拉伸的线性 FITS 生效。Lightroom 导出的 TIFF 已经拉伸过，请勿勾选。";
            AutomationProperties.SetAutomationId(_linearCheck, "LinearCheck");
            row1.Children.Add(_linearCheck);
            Grid.SetRow(row1, 0);
            optGrid.Children.Add(row1);

            var row2 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row2.Children.Add(MakeLabel("阴影裁剪"));
            _shadowsBox.Width = 70;
            _shadowsBox.Text = "-2.8";
            _shadowsBox.Margin = new Thickness(0, 0, 6, 0);
            _shadowsBox.Padding = new Thickness(4, 2, 4, 2);
            _shadowsBox.VerticalContentAlignment = VerticalAlignment.Center;
            _shadowsBox.ToolTip = "仅 --linear 时生效，一般 -2.8。";
            AutomationProperties.SetAutomationId(_shadowsBox, "ShadowsBox");
            row2.Children.Add(_shadowsBox);
            var shadowsHint = new TextBlock
            {
                Text = "（默认 -2.8，一般不用改）",
                Foreground = Brushes.Gray,
                FontSize = 12,
                Margin = new Thickness(0, 0, 18, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row2.Children.Add(shadowsHint);

            row2.Children.Add(MakeLabel("目标背景"));
            _targetBox.Width = 70;
            _targetBox.Text = "0.25";
            _targetBox.Margin = new Thickness(0, 0, 6, 0);
            _targetBox.Padding = new Thickness(4, 2, 4, 2);
            _targetBox.VerticalContentAlignment = VerticalAlignment.Center;
            _targetBox.ToolTip = "仅 --linear 时生效，0 与 1 之间，一般 0.25。";
            AutomationProperties.SetAutomationId(_targetBox, "TargetBgBox");
            row2.Children.Add(_targetBox);
            var targetHint = new TextBlock
            {
                Text = "（默认 0.25）",
                Foreground = Brushes.Gray,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            row2.Children.Add(targetHint);
            Grid.SetRow(row2, 1);
            optGrid.Children.Add(row2);

            var linHint = new TextBlock
            {
                Text = "提示：“线性 FITS”只适用于未拉伸的线性天文 FITS 文件；Lightroom / 普通 TIFF / PNG 都是已拉伸的显示图像，保持不勾选即可。",
                Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0x8C, 0x96)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(linHint, 2);
            optGrid.Children.Add(linHint);

            optBox.Content = optGrid;
            Grid.SetRow(optBox, 2);
            root.Children.Add(optBox);

            // --- action buttons ---
            var actions = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var leftActions = new StackPanel { Orientation = Orientation.Horizontal };
            _runBtn.Content = "开始去噪";
            _runBtn.Padding = new Thickness(22, 9, 22, 9);
            _runBtn.FontSize = 15;
            _runBtn.FontWeight = FontWeights.Bold;
            _runBtn.Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0));
            _runBtn.Foreground = Brushes.White;
            _runBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0));
            _runBtn.Margin = new Thickness(0, 0, 10, 0);
            AutomationProperties.SetAutomationId(_runBtn, "RunButton");
            leftActions.Children.Add(_runBtn);

            _cancelBtn.Content = "取消";
            _cancelBtn.Padding = new Thickness(16, 8, 16, 8);
            _cancelBtn.IsEnabled = false;
            _cancelBtn.Margin = new Thickness(0, 0, 10, 0);
            AutomationProperties.SetAutomationId(_cancelBtn, "CancelButton");
            leftActions.Children.Add(_cancelBtn);

            _openFolderBtn.Content = "打开输出文件夹";
            _openFolderBtn.Padding = new Thickness(12, 8, 12, 8);
            _openFolderBtn.IsEnabled = false;
            AutomationProperties.SetAutomationId(_openFolderBtn, "OpenFolderButton");
            leftActions.Children.Add(_openFolderBtn);

            var clearResultBtn = new Button
            {
                Content = "清空结果",
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(10, 0, 0, 0),
                ToolTip = "只清空右侧结果预览，不删除已经写出的文件"
            };
            clearResultBtn.Click += delegate { ClearResult(); };
            AutomationProperties.SetAutomationId(clearResultBtn, "ClearResultButton");
            leftActions.Children.Add(clearResultBtn);

            DockPanel.SetDock(leftActions, Dock.Left);
            actions.Children.Add(leftActions);

            var rightActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var clearLogBtn = new Button { Content = "清空日志", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
            clearLogBtn.Click += delegate { _logBox.Clear(); };
            AutomationProperties.SetAutomationId(clearLogBtn, "ClearLogButton");
            var copyLogBtn = new Button { Content = "复制日志", Padding = new Thickness(10, 6, 10, 6) };
            copyLogBtn.Click += delegate
            {
                try { if (!string.IsNullOrEmpty(_logBox.Text)) Clipboard.SetText(_logBox.Text); } catch { }
            };
            AutomationProperties.SetAutomationId(copyLogBtn, "CopyLogButton");
            rightActions.Children.Add(clearLogBtn);
            rightActions.Children.Add(copyLogBtn);
            actions.Children.Add(rightActions);

            Grid.SetRow(actions, 3);
            root.Children.Add(actions);

            // --- progress ---
            var progressPanel = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            progressPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            progressPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            progressPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            progressPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _progress.Height = 16;
            _progress.Minimum = 0;
            _progress.Maximum = 100;
            _progress.Margin = new Thickness(0, 0, 0, 4);
            _progress.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAutomationId(_progress, "ProgressBar");
            Grid.SetRow(_progress, 0);
            Grid.SetColumn(_progress, 0);
            Grid.SetColumnSpan(_progress, 2);
            progressPanel.Children.Add(_progress);

            _statusText.Text = "就绪";
            _statusText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x6C, 0x76));
            _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
            _statusText.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAutomationId(_statusText, "StatusText");
            Grid.SetRow(_statusText, 1);
            Grid.SetColumn(_statusText, 0);
            progressPanel.Children.Add(_statusText);

            _queueText.Text = "";
            _queueText.Foreground = StatusWarn;
            _queueText.VerticalAlignment = VerticalAlignment.Center;
            _queueText.Margin = new Thickness(12, 0, 0, 0);
            AutomationProperties.SetAutomationId(_queueText, "QueueText");
            Grid.SetRow(_queueText, 1);
            Grid.SetColumn(_queueText, 1);
            progressPanel.Children.Add(_queueText);

            Grid.SetRow(progressPanel, 4);
            root.Children.Add(progressPanel);

            // --- preview panes ---
            var previewGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            FrameworkElement inputPaneElement = BuildPreviewPane(_inputPane, "原图", "选择输入图片后在这里预览");
            Grid.SetColumn(inputPaneElement, 0);
            previewGrid.Children.Add(inputPaneElement);

            FrameworkElement outputPaneElement = BuildPreviewPane(_outputPane, "DeepSNR 结果", "处理完成后在这里显示结果");
            Grid.SetColumn(outputPaneElement, 2);
            previewGrid.Children.Add(outputPaneElement);

            Grid.SetRow(previewGrid, 5);
            root.Children.Add(previewGrid);

            // --- log ---
            _logExpander.Header = "运行日志（处理失败时请展开查看原因）";
            _logExpander.IsExpanded = false;
            _logExpander.Margin = new Thickness(0, 0, 0, 0);
            AutomationProperties.SetAutomationId(_logExpander, "LogExpander");
            _logBox.IsReadOnly = true;
            _logBox.Height = 140;
            _logBox.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
            _logBox.FontSize = 11.5;
            _logBox.Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFC));
            _logBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _logBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _logBox.TextWrapping = TextWrapping.NoWrap;
            AutomationProperties.SetAutomationId(_logBox, "LogText");
            _logExpander.Content = _logBox;
            Grid.SetRow(_logExpander, 6);
            root.Children.Add(_logExpander);

            Content = root;

            // --- zoom toolbar (placed above preview area, inside its own row) ---
            // Simpler: put zoom controls at the top of previewGrid as an overlay row.
            // Implemented by re-parenting: add a small toolbar row above previews.
            var zoomBar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            zoomBar.Children.Add(MakeLabel("预览缩放"));
            zoomBar.Children.Add(MakeZoomButton("适应窗口", 0.0, "ZoomFitButton"));
            zoomBar.Children.Add(MakeZoomButton("50%", 0.5, "Zoom50Button"));
            zoomBar.Children.Add(MakeZoomButton("100%", 1.0, "Zoom100Button"));
            zoomBar.Children.Add(MakeZoomButton("200%", 2.0, "Zoom200Button"));
            zoomBar.Children.Add(MakeZoomButton("400%", 4.0, "Zoom400Button"));
            _zoom = 0.0;

            _syncScrollCheck.Content = "同步平移";
            _syncScrollCheck.IsChecked = true;
            _syncScrollCheck.VerticalAlignment = VerticalAlignment.Center;
            _syncScrollCheck.Margin = new Thickness(0, 0, 16, 0);
            AutomationProperties.SetAutomationId(_syncScrollCheck, "SyncScrollCheck");
            zoomBar.Children.Add(_syncScrollCheck);

            var zoomHint = new TextBlock
            {
                Text = "判断降噪效果请切到 100% 或 200% 查看细节；滚轮/滚动条可平移。",
                Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0x8C, 0x96)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            zoomBar.Children.Add(zoomHint);
            AutomationProperties.SetAutomationId(zoomBar, "ZoomBar");

            // Put zoom bar into a new row: shift preview row down.
            root.RowDefinitions.Insert(5, new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(zoomBar, 5);
            Grid.SetRow(previewGrid, 6);
            Grid.SetRow(_logExpander, 7);
            root.Children.Add(zoomBar);
            // Re-order: zoomBar must be added after previewGrid for correct z-order but Grid rows handle it.
        }

        private static TextBlock MakeLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
        }

        private Button MakeZoomButton(string text, double zoom, string automationId)
        {
            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = zoom <= 0 ? "缩放整张图片以适合窗口" : string.Format("按 {0:0}% 显示（1 个屏幕像素 = 1 个图片像素）", zoom * 100)
            };
            btn.Click += delegate
            {
                _zoom = zoom;
                ApplyZoom();
            };
            AutomationProperties.SetAutomationId(btn, automationId);
            return btn;
        }

        private void AddFileRow(Grid grid, int row, string label, TextBox box, string buttonText,
            RoutedEventHandler browseHandler, string automationId, string automationName)
        {
            var lbl = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 8)
            };
            Grid.SetRow(lbl, row);
            Grid.SetColumn(lbl, 0);
            grid.Children.Add(lbl);

            box.Margin = new Thickness(0, 0, 8, 8);
            box.Padding = new Thickness(6, 4, 6, 4);
            box.VerticalContentAlignment = VerticalAlignment.Center;
            box.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
            box.FontSize = 12.5;
            AutomationProperties.SetAutomationId(box, automationId);
            AutomationProperties.SetName(box, automationName);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            var btn = new Button
            {
                Content = buttonText,
                Padding = new Thickness(14, 4, 14, 4),
                Margin = new Thickness(0, 0, 0, 8)
            };
            btn.Click += browseHandler;
            AutomationProperties.SetAutomationId(btn, automationId + "Browse");
            Grid.SetRow(btn, row);
            Grid.SetColumn(btn, 2);
            grid.Children.Add(btn);
        }

        private FrameworkElement BuildPreviewPane(PreviewPane pane, string title, string placeholderText)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = false };
            var titleBlock = new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x3A, 0x44)),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(titleBlock, Dock.Left);
            head.Children.Add(titleBlock);

            var openBtn = new Button
            {
                Content = "用其他软件打开",
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = false
            };
            pane.OpenButton = openBtn;
            head.Children.Add(openBtn);

            pane.Meta = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x8E, 0x98)),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0),
                MaxWidth = 420,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = ""
            };
            DockPanel.SetDock(openBtn, Dock.Right);
            DockPanel.SetDock(pane.Meta, Dock.Left);
            head.Children.Insert(1, pane.Meta);

            Grid.SetRow(head, 0);
            grid.Children.Add(head);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1C)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD2, 0xD6, 0xDC)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true
            };

            var inner = new Grid();
            pane.Scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                Padding = new Thickness(0)
            };
            pane.Image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(pane.Image, BitmapScalingMode.NearestNeighbor);
            pane.Scroller.Content = pane.Image;

            pane.Placeholder = new TextBlock
            {
                Text = placeholderText,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x9A)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontSize = 14,
                IsHitTestVisible = false
            };

            inner.Children.Add(pane.Scroller);
            inner.Children.Add(pane.Placeholder);
            border.Child = inner;

            pane.Scroller.ScrollChanged += OnPreviewScrollChanged;
            pane.Scroller.SizeChanged += delegate
            {
                if (_zoom <= 0) ApplyZoom();
            };

            Grid.SetRow(border, 1);
            grid.Children.Add(border);
            return grid;
        }

        // ------------------------------------------------------------------
        //  Wire-up
        // ------------------------------------------------------------------

        private void HookEvents()
        {
            _runBtn.Click += delegate { StartProcessing(); };
            _cancelBtn.Click += delegate { CancelProcessing(); };
            _openFolderBtn.Click += delegate { OpenOutputFolder(); };
            _linearCheck.Checked += delegate { UpdateLinearFields(); };
            _linearCheck.Unchecked += delegate { UpdateLinearFields(); };
            _inputBox.TextChanged += delegate { OnInputTextChanged(); };
            _outputBox.TextChanged += delegate
            {
                if (!_suppressAuto) _outputManual = true;
            };
            _inputPane.OpenButton.Click += delegate
            {
                string p = ToFullPathSafe(_inputBox.Text.Trim().Trim('"'));
                if (File.Exists(p)) OpenInDefaultApp(p);
            };
            _outputPane.OpenButton.Click += delegate
            {
                if (File.Exists(_outputPath)) OpenInDefaultApp(_outputPath);
            };
            Drop += OnDrop;
            DragOver += OnDragOver;
            Closing += OnClosing;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                StartProcessing();
                e.Handled = true;
            }
            else if (e.Key == Key.O && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                BrowseInput(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        // ------------------------------------------------------------------
        //  File pickers / drag & drop
        // ------------------------------------------------------------------

        private void BrowseInput(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择要降噪的图片",
                Filter = "支持的图像|*.tif;*.tiff;*.png;*.jpg;*.jpeg;*.fits;*.fit;*.fts|TIFF|*.tif;*.tiff|PNG|*.png|JPEG（有损，建议用 TIFF）|*.jpg;*.jpeg|FITS|*.fits;*.fit;*.fts|所有文件|*.*",
                CheckFileExists = true
            };
            if (!string.IsNullOrEmpty(_lastDir) && Directory.Exists(_lastDir)) dlg.InitialDirectory = _lastDir;
            if (dlg.ShowDialog(this) == true)
            {
                _lastDir = Path.GetDirectoryName(dlg.FileName);
                _queue.Clear();
                _queueText.Text = "";
                SetInput(dlg.FileName, true);
            }
        }

        private void BrowseOutput(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "选择输出文件",
                Filter = "TIFF|*.tif;*.tiff|PNG|*.png|FITS|*.fits;*.fit;*.fts|所有文件|*.*",
                DefaultExt = ".tif",
                AddExtension = true,
                OverwritePrompt = true
            };
            string current = _outputBox.Text.Trim().Trim('"');
            if (!string.IsNullOrEmpty(current))
            {
                try
                {
                    dlg.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(current));
                    dlg.FileName = Path.GetFileName(current);
                }
                catch { }
            }
            if (dlg.ShowDialog(this) == true)
            {
                _outputManual = true;
                SetOutputText(dlg.FileName);
            }
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            var list = new List<string>();
            foreach (string f in files)
            {
                try
                {
                    if (Directory.Exists(f))
                    {
                        foreach (string inner in Directory.GetFiles(f))
                        {
                            if (Paths2.IsSupported(inner)) list.Add(inner);
                        }
                    }
                    else if (File.Exists(f) && Paths2.IsSupported(f))
                    {
                        list.Add(f);
                    }
                }
                catch { }
            }
            if (list.Count == 0)
            {
                Warn("拖入的文件不是支持的图像格式。支持 TIFF / PNG / FITS（JPEG 也可，但有损）。");
                return;
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            _queue.Clear();
            for (int i = 1; i < list.Count; i++) _queue.Enqueue(list[i]);
            SetInput(list[0], true);
            _lastDir = Path.GetDirectoryName(list[0]);
            if (_queue.Count > 0) _queueText.Text = "队列剩余 " + _queue.Count + " 张（处理完当前图后自动继续）";
            else _queueText.Text = "";
        }

        // ------------------------------------------------------------------
        //  Input/output handling
        // ------------------------------------------------------------------

        private void SetInput(string path, bool resetOutput)
        {
            if (resetOutput)
            {
                _outputManual = false;
                ClearPreview(_outputPane, "处理完成后在这里显示结果");
            }
            _suppressAuto = true;
            _inputBox.Text = path;
            _suppressAuto = false;
            LoadPreviewImage(path, _inputPane);
            if (!_outputManual) SetOutputText(Paths2.DeriveOutput(path));
            UpdateOpenButtons();
        }

        private void SetOutputText(string path)
        {
            _suppressAuto = true;
            _outputBox.Text = path;
            _suppressAuto = false;
        }

        private void OnInputTextChanged()
        {
            if (_suppressAuto) return;
            string input = _inputBox.Text.Trim().Trim('"');
            if (!_outputManual)
            {
                SetOutputText(Paths2.DeriveOutput(input));
            }
            if (File.Exists(ToFullPathSafe(input)))
            {
                LoadPreviewImage(ToFullPathSafe(input), _inputPane);
                _lastDir = Path.GetDirectoryName(ToFullPathSafe(input));
            }
            UpdateOpenButtons();
        }

        private void UpdateOpenButtons()
        {
            string input = ToFullPathSafe(_inputBox.Text.Trim().Trim('"'));
            _inputPane.OpenButton.IsEnabled = File.Exists(input);
        }

        private static string ToFullPathSafe(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private void LoadPreviewImage(string path, PreviewPane pane)
        {
            string fullPath = ToFullPathSafe(path);
            pane.Source = null;
            pane.PixelWidth = 0;
            pane.PixelHeight = 0;
            pane.IsFits = Paths2.IsFits(fullPath);
            pane.Image.Source = null;
            pane.OpenButton.IsEnabled = File.Exists(fullPath);
            pane.Meta.Text = Path.GetFileName(fullPath);

            if (pane.IsFits)
            {
                pane.Placeholder.Text = "FITS 文件无法在界面中预览\n处理完成后可用 PixInsight / Siril 或其他天文软件打开结果";
                pane.Placeholder.Visibility = Visibility.Visible;
                pane.Image.Visibility = Visibility.Collapsed;
                ApplyZoom();
                return;
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(fullPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                pane.Source = bmp;
                pane.PixelWidth = bmp.PixelWidth;
                pane.PixelHeight = bmp.PixelHeight;
                pane.Image.Source = bmp;
                pane.Meta.Text = string.Format("{0}   {1}×{2}", Path.GetFileName(path), bmp.PixelWidth, bmp.PixelHeight);
                pane.Placeholder.Visibility = Visibility.Collapsed;
                pane.Image.Visibility = Visibility.Visible;
            }
            catch (Exception)
            {
                pane.Image.Source = null;
                pane.Placeholder.Text = "无法预览此图像\n（不影响处理，可打开输出文件查看结果）";
                pane.Placeholder.Visibility = Visibility.Visible;
                pane.Image.Visibility = Visibility.Collapsed;
                pane.Meta.Text = Path.GetFileName(path) + "（无预览）";
            }
            ApplyZoom();
        }

        private void ClearPreview(PreviewPane pane, string text)
        {
            pane.Source = null;
            pane.PixelWidth = 0;
            pane.PixelHeight = 0;
            pane.Image.Source = null;
            pane.Image.Visibility = Visibility.Collapsed;
            pane.Placeholder.Text = text;
            pane.Placeholder.Visibility = Visibility.Visible;
            pane.Meta.Text = "";
            pane.OpenButton.IsEnabled = false;
        }

        // ------------------------------------------------------------------
        //  Zoom / pan
        // ------------------------------------------------------------------

        private void ApplyZoom()
        {
            if (_inputPane.Scroller == null) return;
            double zoom;
            if (_zoom <= 0)
            {
                double z1 = _inputPane.Source != null ? FitZoom(_inputPane) : double.MaxValue;
                double z2 = _outputPane.Source != null ? FitZoom(_outputPane) : double.MaxValue;
                zoom = Math.Min(z1, z2);
                if (double.IsInfinity(zoom) || zoom == double.MaxValue) zoom = 1.0;
                zoom = Math.Max(0.03, Math.Min(1.0, zoom));
            }
            else
            {
                zoom = _zoom;
            }

            RenderPane(_inputPane, zoom);
            RenderPane(_outputPane, zoom);
        }

        private static double FitZoom(PreviewPane pane)
        {
            if (pane.Source == null || pane.PixelWidth <= 0 || pane.PixelHeight <= 0) return 1.0;
            double availW = pane.Scroller.ActualWidth - 2;
            double availH = pane.Scroller.ActualHeight - 2;
            if (availW <= 10 || availH <= 10) return 1.0;
            return Math.Min(availW / pane.PixelWidth, availH / pane.PixelHeight);
        }

        private static void RenderPane(PreviewPane pane, double zoom)
        {
            if (pane.Source == null || pane.PixelWidth <= 0)
            {
                pane.Image.Visibility = Visibility.Collapsed;
                return;
            }
            pane.Image.Width = Math.Round(pane.PixelWidth * zoom);
            pane.Image.Height = Math.Round(pane.PixelHeight * zoom);
            RenderOptions.SetBitmapScalingMode(pane.Image,
                zoom >= 1.0 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            pane.Image.Visibility = Visibility.Visible;
            pane.Placeholder.Visibility = Visibility.Collapsed;
        }

        private void OnPreviewScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_syncingScroll) return;
            if (_syncScrollCheck.IsChecked != true) return;
            var source = sender as ScrollViewer;
            if (source == null) return;
            ScrollViewer target = source == _inputPane.Scroller ? _outputPane.Scroller : _inputPane.Scroller;
            if (target == null) return;
            _syncingScroll = true;
            try
            {
                target.ScrollToHorizontalOffset(source.HorizontalOffset);
                target.ScrollToVerticalOffset(source.VerticalOffset);
            }
            finally
            {
                _syncingScroll = false;
            }
        }

        // ------------------------------------------------------------------
        //  DeepSNR location / version
        // ------------------------------------------------------------------

        private void DetectDeepSnr()
        {
            string exe = GetSetting("ExePath");
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                exe = FindDeepSnrExe();
            }
            _exePath = exe;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                _versionText.Text = "未找到 deepsnr.exe";
                _versionText.Foreground = Brushes.Firebrick;
                SetStatus("未找到 deepsnr.exe：请点右上角“更改程序路径…”，选择 D:\\DeepSNR\\bin\\deepsnr.exe", Brushes.Firebrick);
                _runBtn.IsEnabled = false;
                return;
            }
            _settings["ExePath"] = exe;
            _runBtn.IsEnabled = true;
            QueryVersion();
        }

        private static string FindDeepSnrExe()
        {
            string[] candidates =
            {
                @"D:\DeepSNR\bin\deepsnr.exe",
                @"C:\Program Files\DeepSNR\bin\deepsnr.exe",
                @"C:\Program Files (x86)\DeepSNR\bin\deepsnr.exe"
            };
            foreach (string c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string dir in path.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string p = Path.Combine(dir.Trim(), "deepsnr.exe");
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            catch { }
            return "";
        }

        private void QueryVersion()
        {
            _versionText.Text = "正在检测 DeepSNR…";
            _versionText.Foreground = StatusNormal;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _exePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(6000);
                    Match m = Regex.Match(output, @"version:\s*([0-9][0-9A-Za-z\.\-]*)");
                    if (m.Success) _version = m.Groups[1].Value;
                }
            }
            catch { }
            UpdateVersionLine();
        }

        private string BackendShort()
        {
            if (_backend.Length == 0) return "";
            if (_backend.IndexOf("DirectML", StringComparison.OrdinalIgnoreCase) >= 0) return "DirectML GPU";
            if (_backend.IndexOf("CUDA", StringComparison.OrdinalIgnoreCase) >= 0) return "CUDA GPU";
            return "CPU";
        }

        private void UpdateVersionLine()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateVersionLine));
                return;
            }
            string text = "DeepSNR " + (_version.Length > 0 ? _version : "就绪");
            if (_backend.Length > 0) text += "  ·  " + BackendShort();
            _versionText.Text = text;
            _versionText.ToolTip = _exePath;
            _versionText.Foreground = StatusNormal;
        }

        private void BrowseDeepSnrExe()
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择 deepsnr.exe",
                Filter = "deepsnr.exe|deepsnr.exe|可执行文件|*.exe",
                CheckFileExists = true
            };
            if (File.Exists(_exePath)) dlg.InitialDirectory = Path.GetDirectoryName(_exePath);
            if (dlg.ShowDialog(this) == true)
            {
                _exePath = dlg.FileName;
                _settings["ExePath"] = _exePath;
                _version = "";
                _backend = "";
                _runBtn.IsEnabled = true;
                QueryVersion();
                SaveSettings();
            }
        }

        // ------------------------------------------------------------------
        //  Processing
        // ------------------------------------------------------------------

        private void StartProcessing()
        {
            if (_running) return;

            string input = _inputBox.Text.Trim().Trim('"');
            if (input.Length == 0)
            {
                Warn("请先选择输入图片。");
                return;
            }
            if (!File.Exists(input))
            {
                Warn("找不到输入文件：\n" + input);
                return;
            }
            if (!File.Exists(_exePath))
            {
                Warn("找不到 deepsnr.exe，请点右上角“更改程序路径…”重新指定。");
                return;
            }

            string output = _outputBox.Text.Trim().Trim('"');
            if (output.Length == 0)
            {
                output = Paths2.DeriveOutput(input);
                SetOutputText(output);
            }
            string fullInput;
            string fullOutput;
            try
            {
                fullInput = Path.GetFullPath(input);
                fullOutput = Path.GetFullPath(output);
            }
            catch (Exception ex)
            {
                Warn("路径无效：" + ex.Message);
                return;
            }
            if (string.Equals(fullInput, fullOutput, StringComparison.OrdinalIgnoreCase))
            {
                Warn("输出文件不能与输入文件相同，请换一个输出文件名。");
                return;
            }
            string outputDirectory = Path.GetDirectoryName(fullOutput);
            try
            {
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                    Directory.CreateDirectory(outputDirectory);
            }
            catch (Exception ex)
            {
                Warn("无法创建输出目录：\n" + outputDirectory + "\n" + ex.Message);
                return;
            }

            // Fail fast with an actionable message when the output folder is not writable.
            // Typical causes: the exe was started from a restricted/sandboxed context
            // (only the workspace is writable), or security software blocks the write.
            string writeError;
            if (!CanWriteDirectory(outputDirectory, out writeError))
            {
                Warn("无法写入输出目录：\n" + outputDirectory + "\n" + writeError + "\n\n" +
                     "请检查：\n" +
                     "1) 程序是不是从“对话窗口 / 文件卡片 / 沙箱环境”启动的？那种启动方式只能写入工作区，请在 Windows 资源管理器里直接双击 DeepSNR_GUI.exe 再试；\n" +
                     "2) 安全软件（如火绒）是否拦截了写入？请把 DeepSNR_GUI.exe 和 deepsnr.exe 加入信任；\n" +
                     "3) 换一个输出目录（例如 D:\\astro）试试。");
                return;
            }
            if (File.Exists(fullOutput))
            {
                try
                {
                    FileAttributes attrs = File.GetAttributes(fullOutput);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        Warn("输出文件是只读的，无法覆盖：\n" + fullOutput + "\n请先取消只读属性或换一个文件名。");
                        return;
                    }
                }
                catch { }
            }

            string args = BuildArguments(fullInput, fullOutput);
            if (args == null) return;

            int w, h;
            if (TryGetRasterSize(fullInput, out w, out h) && (w < 512 || h < 512))
            {
                Warn(string.Format("图片尺寸 {0}×{1} 小于 DeepSNR 要求的 512×512。", w, h));
                return;
            }

            // DeepSNR accepts only 1 or 3 image channels. PNG/TIFF exports often carry an
            // opaque alpha channel (4 channels) which DeepSNR rejects; strip it first.
            string effectiveInput = fullInput;
            string prepareNote;
            string preparedInput = PrepareInputWithoutAlpha(fullInput, outputDirectory, out prepareNote);
            if (preparedInput == null)
            {
                Warn(prepareNote);
                return;
            }
            if (!string.Equals(preparedInput, fullInput, StringComparison.OrdinalIgnoreCase))
            {
                effectiveInput = preparedInput;
                args = BuildArguments(effectiveInput, fullOutput);
                if (args == null) return;
            }
            if (!string.IsNullOrEmpty(prepareNote)) AppendLog(prepareNote);

            _outputPath = fullOutput;
            _cancelled = false;
            _lastErrorLine = "";
            _running = true;
            _startTime = DateTime.Now;
            _progress.Value = 0;
            _logExpander.IsExpanded = false;
            SetStatus("正在启动 DeepSNR…", StatusNormal);
            _runBtn.IsEnabled = false;
            _cancelBtn.IsEnabled = true;
            _openFolderBtn.IsEnabled = false;

            AppendLog("============================================================");
            AppendLog("输入：" + fullInput);
            if (!string.Equals(effectiveInput, fullInput, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("实际处理输入：" + effectiveInput);
            }
            AppendLog("输出：" + fullOutput);
            AppendLog("命令：\"" + _exePath + "\" " + args);
            if (Paths2.ContainsNonAscii(fullInput) || Paths2.ContainsNonAscii(fullOutput))
            {
                AppendLog("提示：路径含非 ASCII 字符（中文等），通常可以正常处理；如遇异常可把文件移到纯英文路径后重试。");
            }

            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_exePath) ?? Environment.CurrentDirectory
            };
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += OnStdout;
            proc.ErrorDataReceived += OnStderr;
            proc.Exited += OnProcessExited;
            _proc = proc;
            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _running = false;
                _proc = null;
                _runBtn.IsEnabled = true;
                _cancelBtn.IsEnabled = false;
                CleanupTempInput();
                SetStatus("启动 deepsnr.exe 失败：" + ex.Message, Brushes.Firebrick);
                Warn("启动 deepsnr.exe 失败：\n" + ex.Message);
            }
        }

        /// <summary>
        /// True when the WPF/WIC pixel format carries an alpha channel.
        /// 3-channel formats (Bgr24, Rgb48, ...) expose 3 masks; alpha formats expose 4.
        /// </summary>
        private static bool IsAlphaPixelFormat(PixelFormat format)
        {
            try
            {
                if (format == PixelFormats.Cmyk32) return false;
                var masks = format.Masks;
                return masks != null && masks.Count == 4;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// DeepSNR accepts only 1 or 3 image channels. PNG/TIFF exports often carry an
        /// opaque alpha channel (4 channels) and are rejected by DeepSNR. Convert such
        /// files to a temporary 3-channel TIFF (same bit depth) and feed that instead.
        /// The temp file is written next to the output first (that folder must be
        /// writable anyway), falling back to the input folder and the system temp dir.
        /// Returns the original path when no conversion is needed, or null on error.
        /// </summary>
        private string PrepareInputWithoutAlpha(string fullInput, string outputDirectory, out string note)
        {
            note = "";
            try
            {
                var metaDecoder = BitmapDecoder.Create(
                    new Uri(fullInput, UriKind.Absolute),
                    BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);
                if (metaDecoder.Frames.Count == 0) return fullInput;
                BitmapFrame metaFrame = metaDecoder.Frames[0];
                if (!IsAlphaPixelFormat(metaFrame.Format)) return fullInput;
                int bitsPerPixel = metaFrame.Format.BitsPerPixel;

                var decoder = BitmapDecoder.Create(
                    new Uri(fullInput, UriKind.Absolute),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                BitmapFrame frame = decoder.Frames[0];

                PixelFormat target = bitsPerPixel >= 48 ? PixelFormats.Rgb48 : PixelFormats.Bgr24;
                BitmapSource converted;
                try
                {
                    converted = new FormatConvertedBitmap(frame, target, null, 0.0);
                }
                catch
                {
                    converted = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0.0);
                }

                var candidateDirs = new List<string>();
                if (!string.IsNullOrEmpty(outputDirectory)) candidateDirs.Add(outputDirectory);
                string inputDirectory = Path.GetDirectoryName(fullInput);
                if (!string.IsNullOrEmpty(inputDirectory) && !candidateDirs.Contains(inputDirectory))
                    candidateDirs.Add(inputDirectory);
                candidateDirs.Add(Path.Combine(Path.GetTempPath(), "DeepSNR-GUI"));

                string tempPath = null;
                string lastError = "";
                foreach (string dir in candidateDirs)
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        string candidate = Path.Combine(dir,
                            "deepnr_noalpha_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".tif");
                        var encoder = new TiffBitmapEncoder();
                        encoder.Compression = TiffCompressOption.Lzw;
                        encoder.Frames.Add(BitmapFrame.Create(converted));
                        using (FileStream fs = File.Create(candidate))
                        {
                            encoder.Save(fs);
                        }
                        tempPath = candidate;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                    }
                }
                if (tempPath == null)
                {
                    note = "这张图带 alpha（4 通道），DeepSNR 只接受 1 或 3 通道，但无法创建去 alpha 的临时文件。\n" +
                           "请检查输出目录/图片目录的写入权限，或关闭安全软件拦截后重试。\n" +
                           "最后一次错误：" + lastError;
                    return null;
                }

                CleanupTempInput();
                _tempInputPath = tempPath;
                note = "检测到 alpha（4 通道）：DeepSNR 只接受 1/3 通道，已自动生成无 alpha 的 3 通道临时 TIFF 再处理（" + tempPath + "）";
                return tempPath;
            }
            catch (Exception ex)
            {
                note = "这张图带 alpha（4 通道），DeepSNR 只接受 1 或 3 通道，自动去除 alpha 失败：" + ex.Message;
                return null;
            }
        }

        private static bool CanWriteDirectory(string directory, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(directory)) return true;
            try
            {
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory,
                    ".deepsnr_gui_write_test_" + Guid.NewGuid().ToString("N") + ".tmp");
                using (FileStream fs = File.Create(probe))
                {
                    fs.WriteByte(0x2A);
                }
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void CleanupTempInput()
        {
            if (string.IsNullOrEmpty(_tempInputPath)) return;
            try
            {
                if (File.Exists(_tempInputPath)) File.Delete(_tempInputPath);
            }
            catch { }
            _tempInputPath = null;
        }

        private string BuildArguments(string input, string output)
        {
            int model = _modelCombo.SelectedIndex == 0 ? 2 : 1;
            int stride;
            if (!int.TryParse(_strideBox.Text.Trim(), out stride))
            {
                Warn("步长必须是数字（默认 480）。");
                return null;
            }
            if (stride < 2 || stride > 512 || stride % 2 != 0)
            {
                Warn("步长必须是 2–512 之间的偶数（默认 480）。");
                return null;
            }

            var sb = new StringBuilder();
            sb.Append("-i ").Append(Quote(input));
            sb.Append(" -o ").Append(Quote(output));
            sb.Append(" -m ").Append(model.ToString(CultureInfo.InvariantCulture));
            sb.Append(" -s ").Append(stride.ToString(CultureInfo.InvariantCulture));

            if (_linearCheck.IsChecked == true)
            {
                double shadows;
                double target;
                if (!double.TryParse(_shadowsBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out shadows) || shadows >= 0)
                {
                    Warn("阴影裁剪必须是一个负数（默认 -2.8）。");
                    return null;
                }
                if (!double.TryParse(_targetBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out target) || target <= 0 || target >= 1)
                {
                    Warn("目标背景必须严格在 0 与 1 之间（默认 0.25）。");
                    return null;
                }
                sb.Append(" --linear");
                sb.Append(" --shadows-clipping ").Append(shadows.ToString("0.#####", CultureInfo.InvariantCulture));
                sb.Append(" --target-background ").Append(target.ToString("0.#####", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private void OnStdout(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            string line = e.Data;
            AppendLog(line);
            Match m = PercentRegex.Match(line);
            if (m.Success)
            {
                double pct;
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out pct))
                {
                    PostProgress(pct);
                }
            }
            else if (line.IndexOf("Working: Done", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                PostProgress(100);
                SetStatus("处理完成，正在保存文件…", StatusNormal);
            }
            else if (line.StartsWith("Writing denoised image to:", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("正在写入输出文件…", StatusNormal);
            }
        }

        private void OnStderr(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            string line = e.Data;
            AppendLog(line);
            if (line.Trim().Length > 0 && line.IndexOf("Info [", StringComparison.Ordinal) < 0)
            {
                _lastErrorLine = line.Trim();
            }
            Match m = BackendRegex.Match(line);
            if (m.Success)
            {
                _backend = m.Groups[1].Value.Trim();
                UpdateVersionLine();
            }
        }

        private void PostProgress(double pct)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<double>(PostProgress), pct);
                return;
            }
            double value = Math.Max(0, Math.Min(100, pct));
            if (value > _progress.Value) _progress.Value = value;
            SetStatus(string.Format("正在处理… {0:0.0}%", value), StatusNormal);
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            Process proc = sender as Process;
            if (proc == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { proc.WaitForExit(); } catch { }
                Dispatcher.BeginInvoke(new Action<Process>(CompleteRun), proc);
            });
        }

        private void CompleteRun(Process proc)
        {
            int code = -1;
            try { code = proc.ExitCode; } catch { }
            CleanupTempInput();
            if (_proc != null)
            {
                try { _proc.Dispose(); } catch { }
            }
            _proc = null;
            _running = false;
            _runBtn.IsEnabled = true;
            _cancelBtn.IsEnabled = false;

            double seconds = (DateTime.Now - _startTime).TotalSeconds;

            if (_cancelled)
            {
                SetStatus("已取消。", StatusWarn);
                AppendLog("用户取消了处理。");
                _queue.Clear();
                _queueText.Text = "";
                SaveSettings();
                return;
            }

            bool ok = code == 0 && File.Exists(_outputPath);
            if (ok)
            {
                _progress.Value = 100;
                SetStatus(string.Format("完成 · 用时 {0:0.0} 秒 · 已保存：{1}", seconds, Path.GetFileName(_outputPath)),
                    StatusOk);
                LoadPreviewImage(_outputPath, _outputPane);
                _outputPane.OpenButton.IsEnabled = true;
                _openFolderBtn.IsEnabled = true;
                AppendLog(string.Format("完成，用时 {0:0.0} 秒。", seconds));

                if (_queue.Count > 0)
                {
                    _queueText.Text = "队列剩余 " + _queue.Count + " 张…";
                    _queueTimer = new DispatcherTimer();
                    _queueTimer.Interval = TimeSpan.FromMilliseconds(700);
                    _queueTimer.Tick += delegate
                    {
                        if (_queueTimer != null) { _queueTimer.Stop(); _queueTimer = null; }
                        if (_queue.Count > 0 && !_running)
                        {
                            string next = _queue.Dequeue();
                            SetInput(next, true);
                            StartProcessing();
                        }
                    };
                    _queueTimer.Start();
                }
                else
                {
                    _queueText.Text = "";
                }
            }
            else
            {
                string detail = _lastErrorLine.Length > 0 ? "：" + Truncate(_lastErrorLine, 120) : "";
                SetStatus(string.Format("处理失败（退出码 {0}）{1} · 详见运行日志", code, detail), StatusError);
                _logExpander.IsExpanded = true;
                _progress.Value = 0;
                AppendLog("处理失败，退出码 " + code.ToString() + "。");
                _queue.Clear();
                _queueText.Text = "";
                if (IsPermissionError(_lastErrorLine) || IsPermissionError(_logBox.Text))
                {
                    Warn("无法写入输出文件：\n" + _outputPath + "\n\n" +
                         "常见原因和处理办法：\n" +
                         "1) 程序是从“对话窗口 / 文件卡片 / 沙箱环境”启动的——那种方式只能写入工作区，请在 Windows 资源管理器里直接双击 DeepSNR_GUI.exe 后重试；\n" +
                         "2) 安全软件（如火绒）拦截了写入——把 DeepSNR_GUI.exe 和 deepsnr.exe 加入信任区；\n" +
                         "3) 输出目录没有写入权限——换到 D:\\astro 之类的目录或取消文件只读属性。");
                }
            }
            SaveSettings();
        }

        private void CancelProcessing()
        {
            if (!_running || _proc == null) return;
            _cancelled = true;
            try { _proc.Kill(); } catch { }
            SetStatus("正在取消…", StatusWarn);
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        private static bool TryGetRasterSize(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                var decoder = BitmapDecoder.Create(
                    new Uri(path, UriKind.Absolute),
                    BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);
                if (decoder.Frames.Count == 0) return false;
                width = decoder.Frames[0].PixelWidth;
                height = decoder.Frames[0].PixelHeight;
                return width > 0 && height > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPermissionError(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string lower = text.ToLowerInvariant();
            if (lower.Contains("permission denied") || lower.Contains("access denied")
                || lower.Contains("unable to write output image") || lower.Contains("unable to write")) return true;
            if (text.Contains("拒绝访问") || text.Contains("无法写入输出文件")) return true;
            return false;
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "…";
        }

        private void ClearResult()
        {
            if (_running) return;
            ClearPreview(_outputPane, "处理完成后在这里显示结果");
            _outputPath = "";
            _openFolderBtn.IsEnabled = false;
            _progress.Value = 0;
            SetStatus("就绪", StatusNormal);
        }

        private void OpenDeepSnrLicense()
        {
            try
            {
                if (!string.IsNullOrEmpty(_exePath))
                {
                    string dir = Path.GetDirectoryName(_exePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        string candidate = Path.GetFullPath(Path.Combine(dir, "..", "LICENSE.txt"));
                        if (File.Exists(candidate))
                        {
                            Process.Start(new ProcessStartInfo { FileName = candidate, UseShellExecute = true });
                            return;
                        }
                    }
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://starnetastro.com/cli-tools/deepsnr/",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private static void OpenInDefaultApp(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch { }
        }

        private void OpenOutputFolder()
        {
            try
            {
                string target = File.Exists(_outputPath) ? _outputPath : _outputBox.Text.Trim().Trim('"');
                if (string.IsNullOrEmpty(target)) return;
                if (File.Exists(target))
                {
                    Process.Start("explorer.exe", "/select,\"" + target + "\"");
                }
                else
                {
                    string dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        Process.Start("explorer.exe", "\"" + dir + "\"");
                }
            }
            catch { }
        }

        private void SetStatus(string text, Brush color)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string, Brush>(SetStatus), text, color);
                return;
            }
            _statusText.Text = text;
            _statusText.Foreground = color ?? Brushes.DimGray;
            AutomationProperties.SetName(_statusText, text);
        }

        private void SetStatus(string text)
        {
            SetStatus(text, Brushes.DimGray);
        }

        private void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(AppendLog), line);
                return;
            }
            if (_logBox.Text.Length > 500000)
            {
                _logBox.Text = _logBox.Text.Substring(_logBox.Text.Length - 200000);
            }
            _logBox.AppendText(line + Environment.NewLine);
            _logBox.ScrollToEnd();
        }

        private void Warn(string message)
        {
            MessageBox.Show(this, message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void UpdateLinearFields()
        {
            bool enabled = _linearCheck.IsChecked == true;
            _shadowsBox.IsEnabled = enabled;
            _targetBox.IsEnabled = enabled;
        }

        // ------------------------------------------------------------------
        //  Settings
        // ------------------------------------------------------------------

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (string raw in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("[")) continue;
                    int idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    _settings[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
                }
            }
            catch { }
        }

        private string GetSetting(string key)
        {
            string value;
            return _settings.TryGetValue(key, out value) ? value : "";
        }

        private void ApplySettings()
        {
            int width;
            int height;
            if (int.TryParse(GetSetting("Width"), out width) && int.TryParse(GetSetting("Height"), out height)
                && width >= 980 && height >= 720)
            {
                Width = width;
                Height = height;
            }
            if (GetSetting("Maximized") == "1") WindowState = WindowState.Maximized;

            if (GetSetting("Model") == "1") _modelCombo.SelectedIndex = 1;
            if (!string.IsNullOrEmpty(GetSetting("Stride"))) _strideBox.Text = GetSetting("Stride");
            if (!string.IsNullOrEmpty(GetSetting("Shadows"))) _shadowsBox.Text = GetSetting("Shadows");
            if (!string.IsNullOrEmpty(GetSetting("TargetBg"))) _targetBox.Text = GetSetting("TargetBg");
            if (GetSetting("Linear") == "1") _linearCheck.IsChecked = true;
            UpdateLinearFields();

            _lastDir = GetSetting("LastDir");
        }

        private void SaveSettings()
        {
            if (_noSettings) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                var sb = new StringBuilder();
                sb.AppendLine("[DeepSNR-GUI]");
                sb.AppendLine("ExePath=" + (_exePath ?? ""));
                sb.AppendLine("LastDir=" + (_lastDir ?? ""));
                sb.AppendLine("Model=" + (_modelCombo.SelectedIndex == 0 ? "2" : "1"));
                sb.AppendLine("Stride=" + _strideBox.Text.Trim());
                sb.AppendLine("Linear=" + (_linearCheck.IsChecked == true ? "1" : "0"));
                sb.AppendLine("Shadows=" + _shadowsBox.Text.Trim());
                sb.AppendLine("TargetBg=" + _targetBox.Text.Trim());
                Rect bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
                sb.AppendLine("Width=" + ((int)bounds.Width).ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Height=" + ((int)bounds.Height).ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Maximized=" + (WindowState == WindowState.Maximized ? "1" : "0"));
                File.WriteAllText(SettingsPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private void LoadStartupArgs()
        {
            foreach (string arg in _startArgs)
            {
                if (string.IsNullOrWhiteSpace(arg)) continue;
                try
                {
                    if (File.Exists(arg) && Paths2.IsSupported(arg))
                    {
                        SetInput(arg, true);
                        _lastDir = Path.GetDirectoryName(Path.GetFullPath(arg));
                        break;
                    }
                }
                catch { }
            }
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_running)
            {
                MessageBoxResult result = MessageBox.Show(this,
                    "DeepSNR 正在处理图像，退出会中断处理。确定要退出吗？",
                    AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cancelled = true;
                try { if (_proc != null) _proc.Kill(); } catch { }
            }
            SaveSettings();
        }
    }
}
