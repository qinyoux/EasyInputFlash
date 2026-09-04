// EasyInputFlash GUI (WPF) - ESP32 一键烧录 / 串口监视工具
// 纯 C# 构建 WPF 界面（无 XAML 编译），现代深色主题：渐变背景、圆角卡片、动画按钮、彩色日志。
// 说明：本代码用 csc 直接编译，仅支持 C# 5 语法（避免字符串插值与 ?. 等新特性）。
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;
using Gdi = System.Drawing;
using WinForms = System.Windows.Forms;

namespace EasyInputFlashWPF
{
    public class PortItem
    {
        public string Com;
        public string Name;
        public string Usb;
        public bool IsEsp;
        public override string ToString()
        {
            string n = Name;
            if (n != null) n = Regex.Replace(n, @"\s*\(COM\d+\)", "");
            return IsEsp ? (Com + " · ESP32 · " + n) : (Com + " · " + n);
        }
    }

    public class Program
    {
        [STAThread]
        static void Main()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { LogCrash(e.ExceptionObject as Exception); };
            Application app = new Application();
            app.DispatcherUnhandledException += (s, e) =>
            {
                LogCrash(e.Exception);
                e.Handled = true;
                Environment.Exit(1);
            };
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Run(new MainWindow());
        }

        static void LogCrash(Exception ex)
        {
            try
            {
                string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                File.WriteAllText(Path.Combine(dir, "crash.log"), (ex == null ? "unknown" : ex.ToString()), Encoding.UTF8);
            }
            catch { }
        }
    }

    public class MainWindow : Window
    {
        // ---- 配色 ----
        static readonly Color ACCENT = Color.FromRgb(0x2F, 0x81, 0xF7);
        static readonly Color ACCENT2 = Color.FromRgb(0x00, 0xC6, 0xFF);
        static readonly Color DANGER = Color.FromRgb(0xF7, 0x56, 0x3F);
        static readonly Color CARD_BG = Color.FromRgb(0x16, 0x1B, 0x22);
        static readonly Color CARD_BD = Color.FromRgb(0x26, 0x2C, 0x36);

        // ---- 控件 ----
        ComboBox cbVersion;
        ComboBox cbPort;
        TextBox txtProject;
        Button btnRefresh;
        Button btnBrowse;
        Button btnRun;
        Button btnOneKey;
        Button btnStop;
        Button btnMonStart;
        Button btnMonStop;
        RadioButton rbBuildFlash;
        RadioButton rbFlashOnly;
        RadioButton rbBuildOnly;
        RadioButton rbIdentify;
        TextBlock txtStatus;

        // 日志
        FlowDocument _doc;
        Paragraph _para;
        FlowDocumentScrollViewer _logViewer;
        ScrollViewer _logSv;
        CheckBox _followLog;

        // 进程
        Process _proc;
        bool _busy;

        // 系统托盘 / 图标
        WinForms.NotifyIcon _tray;
        Gdi.Icon _appIcon;
        bool _reallyQuit;

        // 状态
        string RootTools;
        System.Collections.Generic.List<string> versions = new System.Collections.Generic.List<string>();
        string _lastVersion = "", _lastProject = "", _lastPort = "";

        static string StateFile
        {
            get
            {
                string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                return Path.Combine(dir, "EasyInputFlashGUI.state.txt");
            }
        }

        public MainWindow()
        {
            Title = "EasyInput Flash · ESP32 一键烧录 / 监视";
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 1080;
            Height = 720;
            MinHeight = 640;
            MinWidth = 940;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            BuildChrome();
            BuildUi();
            LoadState();
            DetectEnv();
            RefreshPorts();

            // 工具图标 + 系统托盘（缩小到托盘时通过图标辨识）
            InitIcon();
            InitTray();
            // 系统注销 / 关机时不要拦截退出，避免阻断会话结束
            if (Application.Current != null)
                Application.Current.SessionEnding += delegate(object s, SessionEndingCancelEventArgs e) { _reallyQuit = true; };

            // 窗口淡入
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(300))));
        }

        // ========================= 窗口外观 / 自绘标题栏 =========================
        void BuildChrome()
        {
            // 无系统边框，改用 WindowChrome 保留缩放边框。
            // CaptionHeight 设为 0，不把顶部当作“标题栏拖拽区”；
            // 否则标题栏内的按钮点击会被 Chrome 当作拖拽吞掉（SetIsHitTestVisibleInChrome 并不可靠）。
            // 因此标题栏拖动改由下方 TitleBar_MouseDown 手动 DragMove 处理。
            var chrome = new System.Windows.Shell.WindowChrome();
            chrome.CaptionHeight = 0;
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.GlassFrameThickness = new Thickness(0);
            chrome.CornerRadius = new CornerRadius(0);
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;

            Background = new LinearGradientBrush(
                Color.FromRgb(0x0B, 0x0E, 0x14),
                Color.FromRgb(0x14, 0x1A, 0x24), 90);
        }

        // 创建自绘标题栏按钮
        Button MakeCaptionButton(string glyph, double w, string id, Action click)
        {
            var b = new Button();
            b.Content = glyph;
            b.Width = w;
            b.Height = 56;
            b.Foreground = Brushes.White;
            b.FontSize = 14;
            b.Cursor = Cursors.Hand;
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            b.VerticalContentAlignment = VerticalAlignment.Center;
            // 便于自动化定位（同时改善无障碍可识别性）
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            b.Template = (ControlTemplate)Xaml(
                "<ControlTemplate TargetType='Button'" +
                " xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                "<Border x:Name='bd' Background='Transparent'>" +
                "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#22FFFFFF'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");
            b.Click += (s, e) => click();
            return b;
        }

        // 标题栏手动拖动：CaptionHeight=0 时 Chrome 不接管拖拽，这里自己处理。
        // 点击到窗口按钮（最小化/最大化/关闭）时跳过，避免与按钮点击冲突。
        void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            DependencyObject d = e.OriginalSource as DependencyObject;
            while (d != null && !(d is Button)) d = VisualTreeHelper.GetParent(d);
            if (d is Button) return; // 点在按钮上，交给按钮处理
            try { DragMove(); } catch { }
        }

        // 切换最大化/还原；WindowStyle.None 最大化时会顶到屏幕边缘盖住任务栏，
        // 这里把最大化高度限制在可用工作区内，避免遮挡任务栏。
        void ToggleMaximize()
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
            else
            {
                Rect wa = SystemParameters.WorkArea;
                MaxHeight = wa.Height;
                MaxWidth = wa.Width;
                WindowState = WindowState.Maximized;
            }
        }

        // ========================= 界面搭建 =========================
        void BuildUi()
        {
            // ---- 标题栏 ----
            Grid top = new Grid();
            top.Height = 56;
            top.Background = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x00, 0x00));

            // 顶部渐变强调条
            Border accentBar = new Border();
            accentBar.Height = 2;
            accentBar.VerticalAlignment = VerticalAlignment.Top;
            accentBar.HorizontalAlignment = HorizontalAlignment.Stretch;
            accentBar.Background = new LinearGradientBrush(ACCENT, ACCENT2, 45);
            accentBar.Opacity = 0.9;
            top.Children.Add(accentBar);

            StackPanel brandWrap = new StackPanel();
            brandWrap.Orientation = Orientation.Horizontal;
            brandWrap.VerticalAlignment = VerticalAlignment.Center;
            brandWrap.Margin = new Thickness(16, 0, 0, 0);
            Border dot = new Border();
            dot.Width = 9; dot.Height = 9; dot.CornerRadius = new CornerRadius(4);
            dot.Background = new LinearGradientBrush(ACCENT, ACCENT2, 45);
            dot.VerticalAlignment = VerticalAlignment.Center; dot.Margin = new Thickness(0, 0, 10, 0);
            dot.Effect = new DropShadowEffect { Color = ACCENT, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.7 };
            brandWrap.Children.Add(dot);
            TextBlock brand = new TextBlock();
            brand.Text = "EasyInput Flash";
            brand.FontSize = 17;
            brand.FontWeight = FontWeights.Bold;
            brand.Foreground = Brushes.White;
            brand.VerticalAlignment = VerticalAlignment.Center;
            brandWrap.Children.Add(brand);
            TextBlock brandSub = new TextBlock();
            brandSub.Text = "ESP32 一键烧录 · 串口监视";
            brandSub.FontSize = 12;
            brandSub.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6));
            brandSub.VerticalAlignment = VerticalAlignment.Center;
            brandSub.Margin = new Thickness(12, 0, 0, 0);
            brandWrap.Children.Add(brandSub);
            top.Children.Add(brandWrap);

            StackPanel winBtns = new StackPanel();
            winBtns.Orientation = Orientation.Horizontal;
            winBtns.HorizontalAlignment = HorizontalAlignment.Right;
            winBtns.Children.Add(MakeCaptionButton("—", 46, "WinMin", () => MinimizeToTray()));
            winBtns.Children.Add(MakeCaptionButton("▢", 46, "WinMax", () => ToggleMaximize()));
            winBtns.Children.Add(MakeCaptionButton("✕", 46, "WinClose", () => Close()));
            top.Children.Add(winBtns);
            // 标题栏空白处可拖动窗口（按钮区域已在上方方法中跳过）
            top.MouseLeftButtonDown += TitleBar_MouseDown;

            // ---- 根布局：2 列 ----
            Grid root = new Grid();
            root.Margin = new Thickness(16, 0, 16, 16);
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(392) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左列（可滚动）
            ScrollViewer leftScroll = new ScrollViewer();
            leftScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            leftScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            leftScroll.VerticalAlignment = VerticalAlignment.Stretch;
            StackPanel left = new StackPanel();
            left.Margin = new Thickness(0, 8, 0, 0);
            leftScroll.Content = left;

            // ---------- 卡片：编译器版本 ----------
            StackPanel p1 = new StackPanel();
            cbVersion = new ComboBox();
            cbVersion.MinHeight = 34;
            cbVersion.Margin = new Thickness(0, 8, 0, 0);
            cbVersion.FontFamily = new FontFamily("Microsoft YaHei UI");
            p1.Children.Add(MakeLabel("编译器 / ESP-IDF 版本"));
            p1.Children.Add(cbVersion);
            left.Children.Add(MakeCard("①  编译器 / ESP-IDF 版本", p1));

            // ---------- 卡片：端口 + 项目 ----------
            StackPanel p2 = new StackPanel();
            Grid rowPort = new Grid();
            rowPort.Margin = new Thickness(0, 8, 0, 0);
            rowPort.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowPort.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cbPort = new ComboBox();
            cbPort.MinHeight = 34;
            cbPort.FontFamily = new FontFamily("Microsoft YaHei UI");
            btnRefresh = MakeButton("⟳ 刷新", ACCENT, null, r: 8);
            btnRefresh.Width = 74;
            btnRefresh.Height = 34;
            Grid.SetColumn(btnRefresh, 1);
            rowPort.Children.Add(cbPort);
            rowPort.Children.Add(btnRefresh);

            Grid rowProj = new Grid();
            rowProj.Margin = new Thickness(0, 10, 0, 0);
            rowProj.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowProj.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            txtProject = new TextBox();
            txtProject.MinHeight = 34;
            btnBrowse = MakeButton("浏览…", ACCENT, null, r: 8);
            btnBrowse.Width = 74;
            btnBrowse.Height = 34;
            Grid.SetColumn(btnBrowse, 1);
            rowProj.Children.Add(txtProject);
            rowProj.Children.Add(btnBrowse);

            p2.Children.Add(MakeLabel("设备串口"));
            p2.Children.Add(rowPort);
            p2.Children.Add(MakeLabel("项目地址（含 CMakeLists.txt）", 12, true));
            p2.Children.Add(rowProj);
            left.Children.Add(MakeCard("②  端口 与  项目地址", p2));

            // ---------- 卡片：操作方式 ----------
            StackPanel p3 = new StackPanel();
            rbBuildFlash = MakeSeg("构建 + 烧录");
            rbFlashOnly = MakeSeg("仅烧录（跳过编译）");
            rbBuildOnly = MakeSeg("仅构建  build");
            rbIdentify = MakeSeg("识别设备芯片 / MAC");
            rbBuildFlash.IsChecked = true;
            UIElement[] segs = { rbBuildFlash, rbFlashOnly, rbBuildOnly, rbIdentify };
            foreach (UIElement s in segs) { ((Control)s).Margin = new Thickness(0, 4, 0, 0); p3.Children.Add(s); }
            left.Children.Add(MakeCard("③  操作方式", p3));

            // ---------- 一键烧录 ----------
            btnOneKey = MakeButton("►   一键烧录", ACCENT, ACCENT2, r: 12, big: true);
            btnOneKey.Height = 56;
            btnOneKey.Margin = new Thickness(0, 14, 0, 0);
            left.Children.Add(btnOneKey);

            // ---------- 执行 / 停止 ----------
            Grid rowAct = new Grid();
            rowAct.Margin = new Thickness(0, 8, 0, 0);
            rowAct.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowAct.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            rowAct.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnRun = MakeButton("执行所选操作", ACCENT, null, r: 10);
            btnRun.Height = 42;
            btnStop = MakeButton("停止", DANGER, null, r: 10);
            btnStop.Height = 42;
            Grid.SetColumn(btnStop, 2);
            rowAct.Children.Add(btnRun);
            rowAct.Children.Add(btnStop);
            left.Children.Add(rowAct);

            // ---------- 卡片：串口监视 ----------
            StackPanel p4 = new StackPanel();
            Grid rowMon = new Grid();
            rowMon.Margin = new Thickness(0, 8, 0, 0);
            rowMon.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowMon.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            rowMon.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnMonStart = MakeButton("▶ 开始监视", ACCENT2, null, r: 8);
            btnMonStart.Height = 36;
            btnMonStop = MakeButton("■ 停止监视", DANGER, null, r: 8);
            btnMonStop.Height = 36;
            Grid.SetColumn(btnMonStop, 2);
            rowMon.Children.Add(btnMonStart);
            rowMon.Children.Add(btnMonStop);
            p4.Children.Add(rowMon);
            left.Children.Add(MakeCard("④  串口监视", p4));

            Grid.SetColumn(leftScroll, 0);
            root.Children.Add(leftScroll);

            // ---------- 右列：日志 ----------
            _doc = new FlowDocument();
            _doc.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI, Segoe UI");
            _doc.FontSize = 12.5;
            _para = new Paragraph();
            _para.Margin = new Thickness(6);
            _doc.Blocks.Add(_para);
            FlowDocumentScrollViewer logViewer = new FlowDocumentScrollViewer();
            logViewer.Document = _doc;
            logViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            logViewer.Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x18));
            logViewer.Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xDC, 0xE6));
            _logViewer = logViewer;

            Border logOuter = new Border();
            logOuter.CornerRadius = new CornerRadius(12);
            logOuter.Background = new SolidColorBrush(CARD_BG);
            logOuter.BorderBrush = new SolidColorBrush(CARD_BD);
            logOuter.BorderThickness = new Thickness(1);
            logOuter.Margin = new Thickness(6, 8, 0, 0);
            Grid logGrid = new Grid();
            logGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            logGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid logHead = new Grid();
            logHead.Margin = new Thickness(12, 8, 12, 2);
            logHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            logHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock logTitle = new TextBlock();
            logTitle.Text = "⑤  日志 / 输出";
            logTitle.FontSize = 14;
            logTitle.FontWeight = FontWeights.SemiBold;
            logTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEF));
            logTitle.VerticalAlignment = VerticalAlignment.Center;
            logHead.Children.Add(logTitle);
            _followLog = new CheckBox();
            _followLog.Content = "跟随日志";
            _followLog.IsChecked = true;
            _followLog.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            _followLog.FontSize = 12;
            _followLog.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6));
            _followLog.VerticalAlignment = VerticalAlignment.Center;
            _followLog.Margin = new Thickness(10, 0, 0, 0);
            _followLog.Cursor = Cursors.Hand;
            _followLog.Template = (ControlTemplate)Xaml(
                "<ControlTemplate TargetType='CheckBox'" +
                " xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                "<StackPanel Orientation='Horizontal'>" +
                "<Border x:Name='box' Width='15' Height='15' CornerRadius='4' BorderBrush='#5A6472'" +
                " BorderThickness='1' Background='#20262F' VerticalAlignment='Center'>" +
                "<Path x:Name='check' Stretch='Uniform' Width='8' Height='8' Data='M0,4 L3,7 L8,1'" +
                " Stroke='#00C6FF' StrokeThickness='2' Visibility='Collapsed'/>" +
                "</Border>" +
                "<ContentPresenter Margin='6,0,0,0' VerticalAlignment='Center'/>" +
                "</StackPanel>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsChecked' Value='True'>" +
                "<Setter TargetName='box' Property='BorderBrush' Value='#2F81F7'/>" +
                "<Setter TargetName='box' Property='Background' Value='#1B3350'/>" +
                "<Setter TargetName='check' Property='Visibility' Value='Visible'/>" +
                "</Trigger>" +
                "<Trigger Property='IsMouseOver' Value='True'>" +
                "<Setter TargetName='box' Property='BorderBrush' Value='#2F81F7'/>" +
                "</Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");
            Grid.SetColumn(_followLog, 1);
            logHead.Children.Add(_followLog);
            Grid.SetRow(logHead, 0);
            Grid.SetRow(logViewer, 1);
            logGrid.Children.Add(logHead);
            logGrid.Children.Add(logViewer);
            logOuter.Child = logGrid;

            Grid.SetColumn(logOuter, 1);
            root.Children.Add(logOuter);

            // 状态条
            StackPanel statusPanel = new StackPanel();
            statusPanel.Orientation = Orientation.Horizontal;
            statusPanel.Margin = new Thickness(4, 6, 4, 2);
            statusPanel.VerticalAlignment = VerticalAlignment.Bottom;
            txtStatus = new TextBlock();
            txtStatus.Text = "就绪";
            txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6));
            txtStatus.VerticalAlignment = VerticalAlignment.Center;
            statusPanel.Children.Add(txtStatus);

            // 整体
            Grid main = new Grid();
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(top, 0);
            Grid.SetRow(root, 1);
            Grid.SetRow(statusPanel, 2);
            main.Children.Add(top);
            main.Children.Add(root);
            main.Children.Add(statusPanel);

            Content = main;

            StyleInput(txtProject);

            // ---- 事件 ----
            btnRefresh.Click += (s, e) => RefreshPorts();
            btnBrowse.Click += (s, e) => BrowseProject();
            btnRun.Click += (s, e) => RunSelected();
            btnOneKey.Click += (s, e) => OneKeyBurn();
            btnStop.Click += (s, e) => StopAll();
            btnMonStart.Click += (s, e) => StartMonitor();
            btnMonStop.Click += (s, e) => StopAll();
        }

        // ========================= 样式工具 =========================
        static object Xaml(string xaml)
        {
            return XamlReader.Parse(xaml);
        }

        TextBlock MakeLabel(string text, double size = 12.5, bool secondary = false)
        {
            var t = new TextBlock();
            t.Text = text;
            t.FontSize = size;
            t.Foreground = new SolidColorBrush(secondary ? Color.FromRgb(0x8A, 0x93, 0xA6) : Color.FromRgb(0xC9, 0xD1, 0xDD));
            t.Margin = new Thickness(0, 4, 0, 0);
            return t;
        }

        Border MakeCard(string title, UIElement content)
        {
            Border card = new Border();
            card.CornerRadius = new CornerRadius(12);
            card.Background = new SolidColorBrush(CARD_BG);
            card.BorderBrush = new SolidColorBrush(CARD_BD);
            card.BorderThickness = new Thickness(1);
            card.Margin = new Thickness(0, 10, 0, 0);
            card.Padding = new Thickness(14, 10, 14, 12);
            card.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.35, BlurRadius = 14, ShadowDepth = 3 };

            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock h = new TextBlock();
            h.Text = title;
            h.FontSize = 14;
            h.FontWeight = FontWeights.SemiBold;
            h.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEF));
            Grid.SetRow(h, 0);
            Grid.SetRow(content, 1);
            g.Children.Add(h);
            g.Children.Add(content);
            card.Child = g;
            return card;
        }

        // 渐变圆角按钮（big=右侧可指定第二色做渐变 + 悬停辉光）
        Button MakeButton(string text, Color c1, Color? c2, double r, bool big = false)
        {
            Color end = c2 ?? c1;
            var b = new Button();
            b.Content = text;
            b.Foreground = Brushes.White;
            b.FontSize = big ? 17 : 14;
            b.FontWeight = FontWeights.SemiBold;
            b.Cursor = Cursors.Hand;
            b.Focusable = false;
            var grad = new LinearGradientBrush(c1, end, 45);
            b.Background = grad;
            b.Template = BuildButtonTemplate(r, big ? c1 : (Color?)null);
            return b;
        }

        ControlTemplate BuildButtonTemplate(double r, Color? glow)
        {
            string glowSetter = "";
            if (glow.HasValue)
            {
                string col = glow.Value.ToString();
                glowSetter =
                    "<Setter TargetName='bd' Property='Effect'>" +
                    "<Setter.Value><DropShadowEffect Color='" + col + "' BlurRadius='20' ShadowDepth='0' Opacity='0.55'/></Setter.Value>" +
                    "</Setter>";
            }
            return (ControlTemplate)Xaml(
                "<ControlTemplate TargetType='Button'" +
                " xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                "<Border x:Name='bd' CornerRadius='" + r.ToString(System.Globalization.CultureInfo.InvariantCulture) + "' Background='{TemplateBinding Background}' BorderThickness='0'>" +
                "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' Margin='10,0'/>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'>" +
                "<Setter TargetName='bd' Property='Opacity' Value='0.96'/>" +
                glowSetter +
                "</Trigger>" +
                "<Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Opacity' Value='0.72'/></Trigger>" +
                "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='bd' Property='Opacity' Value='0.35'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");
        }

        // 可切换的“分段”单选按钮
        RadioButton MakeSeg(string text)
        {
            var rb = new RadioButton();
            rb.Content = text;
            rb.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0xBA, 0xC7));
            rb.FontSize = 12.5;
            rb.FontFamily = new FontFamily("Microsoft YaHei UI");
            rb.Cursor = Cursors.Hand;
            rb.Template = (ControlTemplate)Xaml(
                "<ControlTemplate TargetType='RadioButton'" +
                " xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                "<Border x:Name='bd' CornerRadius='8' Background='#151B23' BorderBrush='#2A2F3A' BorderThickness='1' Padding='11,7'>" +
                "<StackPanel Orientation='Horizontal'>" +
                "<Ellipse x:Name='dot' Width='10' Height='10' Fill='#3A4150' VerticalAlignment='Center'/>" +
                "<ContentPresenter Margin='9,0,0,0' VerticalAlignment='Center'/>" +
                "</StackPanel></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#1C242E'/></Trigger>" +
                "<Trigger Property='IsChecked' Value='True'>" +
                "<Setter TargetName='bd' Property='Background' Value='#1B3350'/>" +
                "<Setter TargetName='bd' Property='BorderBrush' Value='#2F81F7'/>" +
                "<Setter TargetName='dot' Property='Fill' Value='#2F81F7'/>" +
                "<Setter Property='Foreground' Value='White'/>" +
                "</Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");
            return rb;
        }

        // 主题化文本框：给默认模板设置深色背景与圆角
        void StyleInput(Control c)
        {
            c.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1A, 0x22));
            c.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEF));
            c.BorderBrush = new SolidColorBrush(CARD_BD);
            c.BorderThickness = new Thickness(1);
            c.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            TextBox tb = c as TextBox;
            if (tb != null) tb.Padding = new Thickness(8, 0, 8, 0);
        }

        // 深色下拉框（自定义模板，含弹出列表样式）
        void StyleComboBox(ComboBox cb)
        {
            cb.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEF));
            cb.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            // 用明确字体的 TextBlock 渲染条目，杜绝中文豆腐字，且超长自动省略号
            cb.ItemTemplate = (DataTemplate)Xaml(
                "<DataTemplate" +
                " xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                "<TextBlock Text='{Binding}' FontFamily='Microsoft YaHei UI, Segoe UI'" +
                " TextTrimming='CharacterEllipsis'/>" +
                "</DataTemplate>");
            cb.Template = (ControlTemplate)Xaml(
                "<ControlTemplate TargetType='ComboBox'" +
                " xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                "<Grid>" +
                "<Border x:Name='bd' Background='#141A22' BorderBrush='#262C36' BorderThickness='1' CornerRadius='6'>" +
                "<Grid>" +
                "<Grid.ColumnDefinitions>" +
                "<ColumnDefinition Width='*'/><ColumnDefinition Width='26'/>" +
                "</Grid.ColumnDefinitions>" +
                "<TextBlock x:Name='selTxt' Grid.Column='0' Margin='10,0,26,0' VerticalAlignment='Center'" +
                " Text='{Binding SelectedItem, RelativeSource={RelativeSource TemplatedParent}}'" +
                " TextTrimming='CharacterEllipsis' FontFamily='Microsoft YaHei UI, Segoe UI'/>" +
                "<Path Grid.Column='1' HorizontalAlignment='Center' VerticalAlignment='Center'" +
                " Data='M0,0 L7,0 L3.5,4 Z' Fill='#8A93A6'/>" +
                "</Grid>" +
                "</Border>" +
                "<ToggleButton Background='Transparent' BorderThickness='0' Focusable='False' ClickMode='Press'" +
                " IsChecked='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}'>" +
                "<ToggleButton.Template>" +
                "<ControlTemplate TargetType='ToggleButton'><Border Background='Transparent'/></ControlTemplate>" +
                "</ToggleButton.Template>" +
                "</ToggleButton>" +
                "<Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom'" +
                " AllowsTransparency='True'>" +
                "<Border Background='#1A212B' BorderBrush='#2A2F3A' BorderThickness='1' CornerRadius='8' Margin='0,4,0,0'" +
                " MinWidth='{TemplateBinding ActualWidth}' MaxHeight='{TemplateBinding MaxDropDownHeight}'>" +
                "<ScrollViewer><ItemsPresenter/></ScrollViewer>" +
                "</Border>" +
                "</Popup>" +
                "</Grid>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#2F81F7'/></Trigger>" +
                "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='bd' Property='Opacity' Value='0.4'/></Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate>");
            cb.ItemContainerStyle = (Style)Xaml(
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBoxItem'>" +
                "<Setter Property='Foreground' Value='#E6E9EF'/>" +
                "<Setter Property='Padding' Value='9,6'/>" +
                "<Setter Property='Template'>" +
                "<Setter.Value>" +
                "<ControlTemplate TargetType='ComboBoxItem'>" +
                "<Border x:Name='bd' Background='#1A212B' Padding='{TemplateBinding Padding}'>" +
                "<ContentPresenter/>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsHighlighted' Value='True'><Setter TargetName='bd' Property='Background' Value='#2A3A55'/></Trigger>" +
                "<Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='#1B3350'/></Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate>" +
                "</Setter.Value>" +
                "</Setter>" +
                "<Setter Property='FontFamily' Value='Microsoft YaHei UI, Segoe UI'/>" +
                "</Style>");
        }

        // ========================= 环境 / 版本识别 =========================
        string FindRootTools()
        {
            string[] cands = { Environment.GetEnvironmentVariable("IDF_TOOLS_PATH"), @"C:\Espressif\tools" };
            foreach (string c in cands)
            {
                if (!String.IsNullOrEmpty(c) && Directory.Exists(c))
                {
                    if (Directory.GetFiles(c, "Microsoft.*PowerShell_profile.ps1", SearchOption.TopDirectoryOnly).Length > 0)
                        return Path.GetFullPath(c);
                }
            }
            return null;
        }

        void DetectEnv()
        {
            RootTools = FindRootTools();
            versions = new System.Collections.Generic.List<string>();
            if (!String.IsNullOrEmpty(RootTools))
            {
                try
                {
                    foreach (string f in Directory.GetFiles(RootTools, "Microsoft.*PowerShell_profile.ps1", SearchOption.TopDirectoryOnly))
                    {
                        Match m = Regex.Match(Path.GetFileName(f), @"Microsoft\.v(\d+\.\d+\.\d+).*PowerShell_profile\.ps1");
                        if (m.Success)
                        {
                            string v = m.Groups[1].Value;
                            if (!versions.Contains(v)) versions.Add(v);
                        }
                    }
                }
                catch { }
                versions.Sort();
                versions.Reverse();
            }

            StyleComboBox(cbVersion);
            cbVersion.Items.Add("自动检测");
            foreach (string v in versions) cbVersion.Items.Add(v);
            if (_lastVersion != "" && cbVersion.Items.Contains(_lastVersion)) cbVersion.SelectedItem = _lastVersion;
            else cbVersion.SelectedIndex = 0;

            if (versions.Count == 0 || String.IsNullOrEmpty(RootTools))
                SetStatus("ESP-IDF 未检测到：请先安装 ESP-IDF v5.x（Espressif 安装器）。", Color.FromRgb(0xF7, 0x56, 0x3F));
            else
                SetStatus("ESP-IDF 已检测到：" + cbVersion.Text + "（" + RootTools + "）", Color.FromRgb(0x3D, 0xD6, 0x8C));
        }

        string GetSelectedVersion()
        {
            if (cbVersion.SelectedIndex <= 0) return "";
            return cbVersion.SelectedItem.ToString();
        }

        string ProfilePathForVersion(string version)
        {
            if (String.IsNullOrEmpty(version)) version = versions.Count > 0 ? versions[0] : "";
            if (String.IsNullOrEmpty(version) || String.IsNullOrEmpty(RootTools)) return null;
            string p = Path.Combine(RootTools, "Microsoft.v" + version + ".PowerShell_profile.ps1");
            return File.Exists(p) ? p : null;
        }

        // ========================= 端口刷新 =========================
        void RefreshPorts()
        {
            cbPort.Items.Clear();
            string snapshot = CaptureInvoke("");
            if (!String.IsNullOrEmpty(snapshot))
            {
                foreach (string line in snapshot.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] f = line.Split('|');
                    if (f.Length >= 2 && f[0].StartsWith("COM"))
                    {
                        PortItem it = new PortItem();
                        it.Com = f[0];
                        it.Name = f.Length > 1 ? f[1] : "";
                        it.Usb = f.Length > 2 ? f[2] : "";
                        it.IsEsp = f.Length > 3 ? (f[3].Equals("True", StringComparison.OrdinalIgnoreCase)) : false;
                        cbPort.Items.Add(it);
                    }
                }
            }
            StyleComboBox(cbPort);
            if (cbPort.Items.Count > 0)
            {
                int idx = -1;
                for (int i = 0; i < cbPort.Items.Count; i++)
                {
                    PortItem it = (PortItem)cbPort.Items[i];
                    if (_lastPort == it.Com) { idx = i; break; }
                    if (idx < 0 && it.IsEsp) idx = i;
                }
                cbPort.SelectedIndex = idx >= 0 ? idx : 0;
                AppendLog("已检测到 " + cbPort.Items.Count + " 个串口。");
            }
            else
            {
                AppendLog("未检测到可用串口：请确认 USB 已连接且已装驱动；若固件运行中接管了 USB，请按住 BOOT 进入下载模式。", WarnBrush);
            }
            SaveState();
        }

        static string Encode(string cmd)
        {
            return Convert.ToBase64String(Encoding.Unicode.GetBytes(cmd));
        }

        static string CaptureInvoke(string extra)
        {
            string script =
"try{ $OutputEncoding=[System.Text.Encoding]::UTF8; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 }catch{};" +
"$ports=[System.IO.Ports.SerialPort]::GetPortNames();" +
"$pnp=@();" +
"try{ $pnp=Get-PnpDevice -PresentOnly -Class Ports -ErrorAction SilentlyContinue }catch{}" +
"$out=@();" +
"foreach($p in $pnp){" +
"  if($p.FriendlyName -match '\\(COM(\\d+)\\)'){ $com='COM'+$Matches[1]; if($com -in $ports){" +
"    $usb=''; if($p.InstanceId -match '^USB\\\\'){ $usb=($p.InstanceId -split '\\\\')[1] -replace '&MI_.*$','' }" +
"    $esp=($usb -match 'VID_303A|VID_10C4|VID_1A86|ESP');" +
"    $out += ($com+'|'+$p.FriendlyName+'|'+$usb+'|'+$esp)" +
"  }}" +
"}" +
"foreach($c in $ports){ if($c -notin ($out | ForEach-Object { ($_ -split '\\|')[0] })){ $out += ($c+'|未知串口||False') } }" +
"$out | Sort-Object";
            try
            {
                Process p = new Process();
                p.StartInfo.FileName = "powershell.exe";
                p.StartInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Encode(script);
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                p.Start();
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return o;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ========================= 项目浏览 =========================
        void BrowseProject()
        {
            System.Windows.Forms.FolderBrowserDialog dlg = new System.Windows.Forms.FolderBrowserDialog();
            dlg.Description = "选择 ESP-IDF 项目目录（需含 CMakeLists.txt）";
            dlg.ShowNewFolderButton = false;
            if (!String.IsNullOrEmpty(txtProject.Text) && Directory.Exists(txtProject.Text)) dlg.SelectedPath = txtProject.Text;
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                txtProject.Text = dlg.SelectedPath;
                AppendLog("已选择项目：" + dlg.SelectedPath);
                if (!File.Exists(Path.Combine(dlg.SelectedPath, "CMakeLists.txt")))
                    AppendLog("提醒：所选目录未找到 CMakeLists.txt，可能不是有效的 ESP-IDF 项目。", WarnBrush);
                SaveState();
            }
        }

        bool ValidateProject()
        {
            string proj = txtProject.Text.Trim();
            if (String.IsNullOrEmpty(proj)) { AppendLog("请先填写项目地址。", WarnBrush); return false; }
            if (!Directory.Exists(proj)) { AppendLog("项目目录不存在：" + proj, WarnBrush); return false; }
            if (!File.Exists(Path.Combine(proj, "CMakeLists.txt")))
            {
                AppendLog("该项目缺少 CMakeLists.txt，不是有效的 ESP-IDF 项目：" + proj, WarnBrush);
                return false;
            }
            return true;
        }

        string GetSelectedPort()
        {
            PortItem it = cbPort.SelectedItem as PortItem;
            return it == null ? "" : it.Com;
        }

        // ========================= 命令构建 =========================
        static string EscPs(string s)
        {
            return s.Replace("'", "''");
        }

        string BuildScript(string operation, string port)
        {
            string prof = ProfilePathForVersion(GetSelectedVersion());
            var sb = new StringBuilder();
            if (!String.IsNullOrEmpty(prof)) sb.AppendLine(". '" + EscPs(prof) + "';");
            string proj = txtProject.Text.Trim();
            if (!String.IsNullOrEmpty(proj)) sb.AppendLine("Set-Location '" + EscPs(proj) + "';");

            switch (operation)
            {
                case "build": sb.AppendLine("idf.py build"); break;
                case "flash": sb.AppendLine("idf.py -p " + port + " flash"); break;
                case "buildflash": sb.AppendLine("idf.py build"); sb.AppendLine("if ($LASTEXITCODE -eq 0) { idf.py -p " + port + " flash }"); break;
                case "identify": sb.AppendLine("esptool --port " + port + " chip_id"); break;
                case "monitor": sb.AppendLine("idf.py -p " + port + " monitor"); break;
            }
            return sb.ToString();
        }

        // ========================= 操作 =========================
        void RunSelected()
        {
            if (rbIdentify.IsChecked == true)
            {
                string port = GetSelectedPort();
                if (String.IsNullOrEmpty(port)) { AppendLog("请先选择串口。", WarnBrush); return; }
                StartProcess(BuildScript("identify", port), "== 识别设备：端口 " + port + " ==", false);
                return;
            }
            if (rbBuildOnly.IsChecked == true)
            {
                if (!ValidateProject()) return;
                StartProcess(BuildScript("build", ""), "== 仅构建 ==", false);
                return;
            }
            string p = GetSelectedPort();
            if (String.IsNullOrEmpty(p)) { AppendLog("请先选择串口。", WarnBrush); return; }
            if (!ValidateProject()) return;
            if (rbBuildFlash.IsChecked == true)
                StartProcess(BuildScript("buildflash", p), "== 构建 + 烧录 => " + p + " ==", false);
            else if (rbFlashOnly.IsChecked == true)
                StartProcess(BuildScript("flash", p), "== 仅烧录 => " + p + " ==", false);
        }

        void OneKeyBurn()
        {
            if (!ValidateProject()) return;
            string port = GetSelectedPort();
            if (String.IsNullOrEmpty(port))
            {
                AppendLog("未选择串口，尝试自动识别唯一 ESP32。", WarnBrush);
                RefreshPorts();
                port = GetSelectedPort();
            }
            if (String.IsNullOrEmpty(port)) { AppendLog("无法识别串口，请检查 USB 连接或手动选择。", WarnBrush); return; }

            string prof = ProfilePathForVersion(GetSelectedVersion());
            var sb = new StringBuilder();
            if (!String.IsNullOrEmpty(prof)) sb.AppendLine(". '" + EscPs(prof) + "';");
            sb.AppendLine("Set-Location '" + EscPs(txtProject.Text.Trim()) + "';");
            sb.AppendLine("Write-Host '[信息] 写前验身 chip_id：' " + port);
            sb.AppendLine("esptool --port " + port + " chip_id");
            sb.AppendLine("Write-Host ''");
            sb.AppendLine("Write-Host '[信息] 开始构建 + 烧录 => " + port + "'");
            sb.AppendLine("idf.py build");
            sb.AppendLine("if ($LASTEXITCODE -eq 0) { idf.py -p " + port + " flash }");

            StartProcess(sb.ToString(), "== 一键烧录：端口 " + port + " ==", false);
        }

        void StartMonitor()
        {
            string port = GetSelectedPort();
            if (String.IsNullOrEmpty(port)) { AppendLog("请先选择串口。", WarnBrush); return; }
            if (!ValidateProject()) return;
            StartProcess(BuildScript("monitor", port), "== 串口监视：" + port + "（停止按钮退出）==", true);
        }

        // ========================= 进程管理 =========================
        void StartProcess(string script, string header, bool isMonitor)
        {
            if (_busy && _proc != null && !_proc.HasExited)
            {
                AppendLog("有任务正在进行，请先点「停止」。", WarnBrush);
                return;
            }
            AppendLog("");
            AppendLog(header, AccentBrush);
            AppendLog("----------------------------------------", DimBrush);
            SaveState();

            var psi = new ProcessStartInfo();
            psi.FileName = "powershell.exe";
            psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Encode(script);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WindowStyle = ProcessWindowStyle.Hidden;

            try
            {
                _proc = new Process();
                _proc.StartInfo = psi;
                _proc.OutputDataReceived += (s, e) => { if (e.Data != null) AppendLog(e.Data); };
                _proc.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendLog(e.Data); };
                _proc.Exited += (s, e) =>
                {
                    int code = _proc.ExitCode;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AppendLog("");
                        AppendLog("----------------------------------------", DimBrush);
                        if (code == 0) AppendLog("任务结束：成功（退出码 0）", SuccessBrush);
                        else AppendLog("任务结束：出错（退出码 " + code + "）", ErrorBrush);
                        _busy = false;
                        UpdateButtons();
                    }));
                };
                _proc.EnableRaisingEvents = true;
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                _busy = true;
                UpdateButtons();
                SetStatus("运行中：请查看右侧日志，结束后可继续。", Color.FromRgb(0x00, 0xC6, 0xFF));
            }
            catch (Exception ex)
            {
                AppendLog("启动失败：" + ex.Message, ErrorBrush);
                _busy = false;
                UpdateButtons();
            }
        }

        void StopAll()
        {
            if (_proc != null && !_proc.HasExited)
            {
                try { Process.Start(new ProcessStartInfo("taskkill", "/F /T /PID " + _proc.Id) { CreateNoWindow = true }).WaitForExit(); }
                catch { try { _proc.Kill(); } catch { } }
                AppendLog("== 已发出停止指令 ==", WarnBrush);
            }
            _busy = false;
            UpdateButtons();
        }

        void UpdateButtons()
        {
            btnRun.IsEnabled = !_busy;
            btnOneKey.IsEnabled = !_busy;
            btnMonStart.IsEnabled = !_busy;
            btnRefresh.IsEnabled = !_busy;
            btnBrowse.IsEnabled = !_busy;
            btnStop.IsEnabled = _busy;
            cbPort.IsEnabled = !_busy;
            cbVersion.IsEnabled = !_busy;
            txtProject.IsEnabled = !_busy;
        }

        // ========================= 日志 =========================
        static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xC6, 0xFF));
        static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x60, 0x70));
        static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
        static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x5B));
        static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x4C));
        static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0xDC, 0xE6));

        void AppendLog(string line)
        {
            Brush c = InfoBrush;
            if (line != null)
            {
                if (line.Contains("[错误]")) c = ErrorBrush;
                else if (line.Contains("[完成]")) c = SuccessBrush;
                else if (line.Contains("[提醒]")) c = WarnBrush;
                else if (line.StartsWith("Hash of data verified")) c = SuccessBrush;
            }
            AppendLog(line, c);
        }

        void AppendLog(string line, Brush c)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (line == null) line = "";
                _para.Inlines.Add(new Run(line) { Foreground = c });
                _para.Inlines.Add(new LineBreak());
                // 跟随日志：勾选时自动滚到最底，便于查看最新输出
                if (_followLog != null && _followLog.IsChecked == true && _logViewer != null)
                {
                    ScrollLogToBottom();
                    // 布局尚未刷新时再补一次，确保真正落到底部
                    Dispatcher.BeginInvoke(new Action(ScrollLogToBottom), DispatcherPriority.Background);
                }
            }));
        }

        // FlowDocumentScrollViewer(.NET4.0) 没有公开的滚动方法，改从模板内部取得 ScrollViewer 来滚动到底
        void ScrollLogToBottom()
        {
            try
            {
                if (_logSv == null && _logViewer != null)
                    _logSv = FindDescendant<ScrollViewer>(_logViewer);
                if (_logSv != null)
                    _logSv.ScrollToBottom();
            }
            catch { }
        }

        T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject c = VisualTreeHelper.GetChild(root, i);
                if (c is T) return (T)c;
                T r = FindDescendant<T>(c);
                if (r != null) return r;
            }
            return null;
        }

        void SetStatus(string text, Color c)
        {
            txtStatus.Foreground = new SolidColorBrush(c);
            txtStatus.Text = text;
        }

        // ========================= 状态记忆 =========================
        void SaveState()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("version=" + GetSelectedVersion());
                sb.AppendLine("project=" + txtProject.Text.Trim());
                sb.AppendLine("port=" + GetSelectedPort());
                File.WriteAllText(StateFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        void LoadState()
        {
            try
            {
                if (!File.Exists(StateFile)) return;
                foreach (string line in File.ReadAllLines(StateFile, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();
                    if (k == "version") _lastVersion = v;
                    else if (k == "project") _lastProject = v;
                    else if (k == "port") _lastPort = v;
                }
                txtProject.Text = _lastProject;
            }
            catch { }
        }

        // ========================= 工具图标 / 系统托盘 =========================
        // 运行时绘制一个“ESP32 芯片 + 闪电”（烧录）风格的图标，作为标题栏与托盘图标，
        // 使窗口缩到系统托盘时也能一眼辨识。
        void InitIcon()
        {
            try
            {
                _appIcon = MakeIcon();
                if (_appIcon != null) Icon = ToImageSource(_appIcon);
            }
            catch { }
        }

        void InitTray()
        {
            try
            {
                if (_tray != null) return;
                _tray = new WinForms.NotifyIcon();
                _tray.Icon = _appIcon;
                _tray.Text = "EasyInput Flash - ESP32 烧录工具";
                _tray.Visible = true;

                var menu = new WinForms.ContextMenuStrip();
                menu.Items.Add("显示主界面", null, delegate(object s, EventArgs e) { ShowMainWindow(); });
                menu.Items.Add(new WinForms.ToolStripSeparator());
                menu.Items.Add("退出", null, delegate(object s, EventArgs e) { _reallyQuit = true; Close(); });
                _tray.ContextMenuStrip = menu;

                _tray.DoubleClick += delegate(object s, EventArgs e) { ShowMainWindow(); };
            }
            catch { }
        }

        // 缩小到系统托盘：隐藏窗口（任务栏同步消失），仅保留托盘图标便于辨识
        void MinimizeToTray()
        {
            try
            {
                Hide();
                if (_tray != null && _tray.Visible)
                    _tray.ShowBalloonTip(1500, "EasyInput Flash",
                        "已缩小到系统托盘，双击图标即可恢复窗口。", WinForms.ToolTipIcon.Info);
            }
            catch { }
        }

        // 从托盘恢复主窗口并置前
        void ShowMainWindow()
        {
            try
            {
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                Topmost = true;
                Topmost = false;
                Focus();
            }
            catch { }
        }

        // 点“关闭”默认缩到托盘而不是退出；真正退出走托盘菜单“退出”（_reallyQuit）
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_reallyQuit)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }
            base.OnClosing(e);
        }

        // 真正退出时清理托盘与图标，避免图标残留
        protected override void OnClosed(EventArgs e)
        {
            try
            {
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                if (_appIcon != null) { _appIcon.Dispose(); _appIcon = null; }
            }
            catch { }
            base.OnClosed(e);
        }

        // 生成 48x48 图标：深蓝圆角底 + 芯片方块 + 四边引脚 + 白色闪电
        Gdi.Icon MakeIcon()
        {
            const int S = 48;
            using (var bmp = new Gdi.Bitmap(S, S))
            {
                using (var g = Gdi.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = Gdi.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Gdi.Color.Transparent);

                    // 背景圆角方块（深蓝渐变）
                    var rect = new Gdi.Rectangle(1, 1, S - 2, S - 2);
                    using (var path = RoundRect(rect, 10))
                    {
                        using (var bg = new Gdi.Drawing2D.LinearGradientBrush(
                            rect, Gdi.Color.FromArgb(0x1B, 0x33, 0x50), Gdi.Color.FromArgb(0x0B, 0x0E, 0x14), 55f))
                        {
                            g.FillPath(bg, path);
                        }
                        using (var pen = new Gdi.Pen(Gdi.Color.FromArgb(0x3B, 0x82, 0xA0), 2f))
                            g.DrawPath(pen, path);
                    }

                    // 芯片主体（亮青渐变方块）
                    var chip = new Gdi.Rectangle(13, 13, 22, 22);
                    using (var cp = RoundRect(chip, 4))
                    using (var cg = new Gdi.Drawing2D.LinearGradientBrush(
                        chip, Gdi.Color.FromArgb(0x2F, 0x81, 0xF7), Gdi.Color.FromArgb(0x00, 0xC6, 0xFF), 55f))
                    {
                        g.FillPath(cg, cp);
                    }

                    // 四边引脚
                    using (var p = new Gdi.Pen(Gdi.Color.FromArgb(210, 210, 230, 240), 1.6f))
                    {
                        g.DrawLine(p, 7, 18, 13, 18);
                        g.DrawLine(p, 7, 24, 13, 24);
                        g.DrawLine(p, 7, 30, 13, 30);
                        g.DrawLine(p, 35, 18, 41, 18);
                        g.DrawLine(p, 35, 24, 41, 24);
                        g.DrawLine(p, 35, 30, 41, 30);
                        g.DrawLine(p, 18, 7, 18, 13);
                        g.DrawLine(p, 24, 7, 24, 13);
                        g.DrawLine(p, 30, 7, 30, 13);
                        g.DrawLine(p, 18, 35, 18, 41);
                        g.DrawLine(p, 24, 35, 24, 41);
                        g.DrawLine(p, 30, 35, 30, 41);
                    }

                    // 闪电（烧录/上电）
                    var bolt = new Gdi.PointF[]
                    {
                        new Gdi.PointF(23, 14), new Gdi.PointF(30, 14), new Gdi.PointF(25, 24),
                        new Gdi.PointF(29, 24), new Gdi.PointF(18, 34), new Gdi.PointF(22, 26),
                        new Gdi.PointF(17, 26)
                    };
                    using (var fb = new Gdi.SolidBrush(Gdi.Color.FromArgb(235, 0xE8, 0xF6, 0xFF)))
                        g.FillPolygon(fb, bolt);
                }

                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Gdi.Icon tmp = Gdi.Icon.FromHandle(h))
                        return (Gdi.Icon)tmp.Clone();
                }
                finally
                {
                    DestroyIcon(h);
                }
            }
        }

        // 圆角矩形路径
        Gdi.Drawing2D.GraphicsPath RoundRect(Gdi.Rectangle r, int radius)
        {
            var p = new Gdi.Drawing2D.GraphicsPath();
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 将 System.Drawing.Icon 转为 WPF ImageSource（用于窗口标题栏 / 任务栏）
        System.Windows.Media.ImageSource ToImageSource(Gdi.Icon icon)
        {
            try
            {
                return Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            catch { return null; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern bool DestroyIcon(IntPtr handle);
    }
}
