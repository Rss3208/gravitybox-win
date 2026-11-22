using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GravityBox.App.ViewModels;
using GravityBox.App.Views;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace GravityBox.App.Core;

/// <summary>
/// Renders deterministic, privacy-sanitized Light and Dark Windows WPF screenshots
/// using 100% synthetic fixture data (@example.invalid, C:\Users\demo\...).
/// Invoked only via --export-screenshots &lt;outputDir&gt;.
/// </summary>
public static class DemoScreenshotExporter
{
    private const double Dpi = 192.0; // 2x high-DPI crisp output

    public static async Task ExportAsync(System.Windows.Application app, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        string tempStateDir = Path.Combine(Path.GetTempPath(), $"gb-shot-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempStateDir);

        try
        {
            string baseDir = AppContext.BaseDirectory;
            string i18nDir = Path.Combine(baseDir, "assets", "i18n");
            var config = new ConfigurationStore(tempStateDir);
            config.Update(c =>
            {
                c.AppLanguage = "zh-Hans";
                c.HudEnabled = true;
                c.QuotaRefreshMinutes = 15;
                c.LaunchAtLogin = true;
                c.SyncAntigravityLifecycle = false;
                c.AutoUpdateEnabled = false;
                c.ShortcutGuideDismissed = false;
                c.AccountsCollapsed = false;
                c.MenuUsagePeriod = 0;
                c.MenuSelectedTab = 0;
            });

            var l10n = new L10nService(i18nDir);
            l10n.SetLanguage("zh-Hans");

            using var themeService = new ThemeService(() => true, subscribeSystemEvents: false);
            using var lifecycle = new LifecycleSyncService();
            using var updateManager = new AppUpdateManager("1.1.3");
            await using var bridge = new DemoScreenshotBridge();

            var overviewVm = new OverviewViewModel(bridge, config, l10n, lifecycle);
            var accountsVm = new AccountsViewModel(bridge, config, l10n, lifecycle);
            var usageVm = new UsageViewModel(bridge, config, l10n);
            var settingsVm = new SettingsViewModel(bridge, config, l10n, lifecycle);
            var aboutVm = new AboutViewModel(config, l10n, updateManager, bridge);
            var flyoutVm = new TrayFlyoutViewModel(bridge, config, l10n);

            using var shellVm = new MainShellViewModel(
                bridge,
                config,
                l10n,
                lifecycle,
                new CommunityToolkit.Mvvm.ComponentModel.ObservableObject[]
                {
                    overviewVm,
                    accountsVm,
                    usageVm,
                    settingsVm,
                    aboutVm,
                });

            await shellVm.RefreshStatusAsync();
            await overviewVm.RefreshAsync(force: true);
            await accountsVm.RefreshAsync();
            await usageVm.RefreshAsync();
            await settingsVm.RefreshAsync();
            await aboutVm.RefreshAsync();
            await flyoutVm.RefreshAsync();

            // Explicitly sanitize displayed data directory to allowlisted demo path
            aboutVm.DataDirectory = @"C:\Users\demo\AppData\Local\GravityBox";

            var mainWindow = new MainWindow(shellVm, themeService)
            {
                Width = 1080,
                Height = 800,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
                AllowExplicitClose = true,
            };

            var flyoutWindow = new TrayFlyoutWindow(flyoutVm)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = false,
            };

            mainWindow.Show();
            flyoutWindow.Show();

            var themes = new (AppThemeMode Mode, string Suffix)[]
            {
                (AppThemeMode.Light, string.Empty),
                (AppThemeMode.Dark, "-dark"),
            };

            var pages = new (int Index, string FileBase)[]
            {
                (0, "win-overview"),
                (1, "win-accounts"),
                (2, "win-usage"),
                (3, "win-settings"),
                (4, "win-about"),
            };

            foreach (var (mode, suffix) in themes)
            {
                themeService.SetThemeForPreview(mode, app);
                await WaitForRenderAsync(app);

                BitmapSource? overviewFrame = null;
                BitmapSource? trayFrame = null;

                foreach (var (pageIndex, fileBase) in pages)
                {
                    shellVm.NavigateTo(pageIndex);
                    await WaitForRenderAsync(app);

                    if (mainWindow.Content is UIElement content)
                    {
                        var shot = RenderFramedMainWindow(content, mode, 1080, 800);
                        SavePng(shot, Path.Combine(outputDir, $"{fileBase}{suffix}.png"));
                        if (pageIndex == 0) overviewFrame = shot;
                    }
                }

                // Tray Flyout
                flyoutVm.SelectTab(0);
                await WaitForRenderAsync(app);
                if (flyoutWindow.Content is UIElement flyoutContent)
                {
                    trayFrame = RenderFramedTrayFlyout(flyoutContent, mode);
                    SavePng(trayFrame, Path.Combine(outputDir, $"win-tray{suffix}.png"));
                }

                if (overviewFrame != null && trayFrame != null)
                {
                    var hero = ComposeHeroImage(overviewFrame, trayFrame, mode);
                    SavePng(hero, Path.Combine(outputDir, $"win-hero{suffix}.png"));
                }
            }

            flyoutWindow.Close();
            mainWindow.Close();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempStateDir))
                {
                    Directory.Delete(tempStateDir, recursive: true);
                }
            }
            catch
            {
                // Ignore temp cleanup errors
            }
        }
    }

    private static async Task WaitForRenderAsync(System.Windows.Application app)
    {
        await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Task.Delay(120);
        await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(80);
    }

    private static BitmapSource RenderFramedMainWindow(UIElement windowContent, AppThemeMode mode, double widthDip, double heightDip)
    {
        windowContent.Measure(new System.Windows.Size(widthDip, heightDip));
        windowContent.Arrange(new Rect(0, 0, widthDip, heightDip));
        windowContent.UpdateLayout();

        int contentPxW = (int)Math.Round(widthDip * (Dpi / 96.0));
        int contentPxH = (int)Math.Round(heightDip * (Dpi / 96.0));
        var contentBmp = new RenderTargetBitmap(contentPxW, contentPxH, Dpi, Dpi, PixelFormats.Pbgra32);
        contentBmp.Render(windowContent);
        contentBmp.Freeze();

        bool dark = mode == AppThemeMode.Dark;
        const double titleBarH = 34.0;
        const double pad = 24.0;
        double frameW = widthDip;
        double frameH = heightDip + titleBarH;
        double totalW = frameW + pad * 2;
        double totalH = frameH + pad * 2;

        Color bgColor = dark ? Color.FromRgb(0x16, 0x18, 0x1D) : Color.FromRgb(0xF6, 0xF7, 0xF9);
        Color titleBg = dark ? Color.FromRgb(0x1E, 0x20, 0x26) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        Color borderCol = dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x28, 0x00, 0x00, 0x00);
        Color titleText = dark ? Color.FromRgb(0xEB, 0xED, 0xF2) : Color.FromRgb(0x1F, 0x29, 0x37);
        Color glyphCol = dark ? Color.FromRgb(0x9C, 0xA3, 0xAF) : Color.FromRgb(0x4B, 0x55, 0x63);

        var titleGrid = new Grid
        {
            Height = titleBarH,
            Background = new SolidColorBrush(titleBg),
        };
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftTitle = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };
        leftTitle.Children.Add(new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        leftTitle.Children.Add(new TextBlock
        {
            Text = "GravityBox — Windows Native Companion",
            FontFamily = new FontFamily("Segoe UI Variable, Microsoft YaHei UI, Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(titleText),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(leftTitle, 0);
        titleGrid.Children.Add(leftTitle);

        var winButtons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        foreach (string glyph in new[] { "─", "□", "✕" })
        {
            winButtons.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                Foreground = new SolidColorBrush(glyphCol),
                Margin = new Thickness(14, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        Grid.SetColumn(winButtons, 1);
        titleGrid.Children.Add(winButtons);

        var innerStack = new Grid();
        innerStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(titleBarH) });
        innerStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        innerStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(heightDip) });

        Grid.SetRow(titleGrid, 0);
        innerStack.Children.Add(titleGrid);

        var divider = new Border { Background = new SolidColorBrush(borderCol) };
        Grid.SetRow(divider, 1);
        innerStack.Children.Add(divider);

        var img = new System.Windows.Controls.Image
        {
            Source = contentBmp,
            Width = widthDip,
            Height = heightDip,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        Grid.SetRow(img, 2);
        innerStack.Children.Add(img);

        var clipRect = new RectangleGeometry(new Rect(0, 0, frameW, frameH), 12, 12);
        var clippedHost = new Border
        {
            Width = frameW,
            Height = frameH,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(bgColor),
            Clip = clipRect,
            Child = innerStack,
        };

        var outerFrame = new Border
        {
            Width = frameW,
            Height = frameH,
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(borderCol),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(bgColor),
            Effect = new DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 8,
                Direction = 270,
                Opacity = dark ? 0.55 : 0.22,
                Color = Colors.Black,
            },
            Child = clippedHost,
        };

        var canvas = new Grid
        {
            Width = totalW,
            Height = totalH,
            Background = Brushes.Transparent,
        };
        canvas.Children.Add(outerFrame);

        return RenderElementToBitmap(canvas, totalW, totalH);
    }

    private static BitmapSource RenderFramedTrayFlyout(UIElement flyoutContent, AppThemeMode mode)
    {
        flyoutContent.Measure(new System.Windows.Size(424, 620));
        var desired = flyoutContent.DesiredSize;
        double w = Math.Max(424, desired.Width);
        double h = Math.Min(600, Math.Max(360, desired.Height));
        flyoutContent.Arrange(new Rect(0, 0, w, h));
        flyoutContent.UpdateLayout();

        int contentPxW = (int)Math.Round(w * (Dpi / 96.0));
        int contentPxH = (int)Math.Round(h * (Dpi / 96.0));
        var contentBmp = new RenderTargetBitmap(contentPxW, contentPxH, Dpi, Dpi, PixelFormats.Pbgra32);
        contentBmp.Render(flyoutContent);
        contentBmp.Freeze();

        const double pad = 20.0;
        double totalW = w + pad * 2;
        double totalH = h + pad * 2;
        bool dark = mode == AppThemeMode.Dark;

        var img = new System.Windows.Controls.Image
        {
            Source = contentBmp,
            Width = w,
            Height = h,
            Stretch = Stretch.Fill,
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = dark ? 0.55 : 0.20,
                Color = Colors.Black,
            },
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        var canvas = new Grid
        {
            Width = totalW,
            Height = totalH,
            Background = Brushes.Transparent,
        };
        canvas.Children.Add(img);

        return RenderElementToBitmap(canvas, totalW, totalH);
    }

    private static BitmapSource ComposeHeroImage(BitmapSource mainFrame, BitmapSource trayFrame, AppThemeMode mode)
    {
        const double heroW = 1280.0;
        const double heroH = 720.0;
        bool dark = mode == AppThemeMode.Dark;

        var bgBrush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
        };
        if (dark)
        {
            bgBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x0E, 0x13, 0x1F), 0.0));
            bgBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x13, 0x1C, 0x2E), 0.55));
            bgBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x16, 0x14, 0x26), 1.0));
        }
        else
        {
            bgBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xEC, 0xF3, 0xFF), 0.0));
            bgBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF5, 0xF7, 0xFA), 0.55));
            bgBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE8, 0xEE, 0xFB), 1.0));
        }

        var canvas = new Canvas
        {
            Width = heroW,
            Height = heroH,
            Background = bgBrush,
        };

        double mainTargetW = 920.0;
        double mainTargetH = mainTargetW * (mainFrame.PixelHeight / (double)mainFrame.PixelWidth);
        var mainImg = new System.Windows.Controls.Image
        {
            Source = mainFrame,
            Width = mainTargetW,
            Height = mainTargetH,
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapScalingMode(mainImg, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(mainImg, 28);
        Canvas.SetTop(mainImg, (heroH - mainTargetH) / 2.0);
        canvas.Children.Add(mainImg);

        double trayTargetW = 340.0;
        double trayTargetH = trayTargetW * (trayFrame.PixelHeight / (double)trayFrame.PixelWidth);
        var trayImg = new System.Windows.Controls.Image
        {
            Source = trayFrame,
            Width = trayTargetW,
            Height = trayTargetH,
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapScalingMode(trayImg, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(trayImg, heroW - trayTargetW - 22);
        Canvas.SetTop(trayImg, (heroH - trayTargetH) / 2.0 + 36);
        canvas.Children.Add(trayImg);

        return RenderElementToBitmap(canvas, heroW, heroH);
    }

    private static BitmapSource RenderElementToBitmap(UIElement element, double widthDip, double heightDip)
    {
        element.Measure(new System.Windows.Size(widthDip, heightDip));
        element.Arrange(new Rect(0, 0, widthDip, heightDip));
        element.UpdateLayout();

        int pxW = (int)Math.Round(widthDip * (Dpi / 96.0));
        int pxH = (int)Math.Round(heightDip * (Dpi / 96.0));
        var bmp = new RenderTargetBitmap(pxW, pxH, Dpi, Dpi, PixelFormats.Pbgra32);
        bmp.Render(element);
        bmp.Freeze();
        return bmp;
    }

    private static void SavePng(BitmapSource bitmap, string filePath)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(fs);
    }

    private sealed class DemoScreenshotBridge : IBackendBridge
    {
        public string StateDir => @"C:\Users\demo\AppData\Local\GravityBox";
        public string PipeName => "gravitybox-ipc-demo";
        public bool IsOwnedProcessRunning => true;
        public int? OwnedPid => 4242;
        public string? PreferredLocale { get; set; } = "zh-Hans";

        public event Action<string>? OnExit { add { } remove { } }

        public Task LaunchAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<JsonObject> SendAsync(
            string command,
            JsonObject? parameters = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            long nowSec = now.ToUnixTimeSeconds();
            string reset5h = now.AddHours(3).AddMinutes(42).ToString("O");
            string resetWeekly = now.AddDays(4).AddHours(9).ToString("O");
            string resetAlt5h = now.AddHours(4).AddMinutes(18).ToString("O");
            string resetAltWeekly = now.AddDays(5).AddHours(14).ToString("O");

            JsonObject response = command switch
            {
                "platform_info" => new JsonObject
                {
                    ["ok"] = true,
                    ["platform"] = "win32",
                    ["executablePath"] = @"C:\Users\demo\AppData\Local\Programs\Antigravity\Antigravity.exe",
                    ["profileDir"] = @"C:\Users\demo\AppData\Roaming\Antigravity",
                },

                "status" => new JsonObject
                {
                    ["ok"] = true,
                    ["taskRevision"] = 12,
                    ["runningTasks"] = 0,
                    ["status"] = new JsonObject
                    {
                        ["mode"] = "connected",
                        ["message"] = "已连接 · 中文汉化与 HUD 运行中",
                        ["appVersion"] = "2.34.0",
                        ["entries"] = 2486,
                        ["activeAccountEmail"] = "alex@example.invalid",
                        ["liveAccountEmail"] = "alex@example.invalid",
                        ["activeSubscriptionTier"] = "PRO",
                        ["oauthClientConfigured"] = true,
                    },
                },

                "dashboard_summary" => new JsonObject
                {
                    ["ok"] = true,
                    ["accounts"] = new JsonObject
                    {
                        ["activeName"] = "Alex (主力工作号)",
                        ["activeEmail"] = "alex@example.invalid",
                        ["activeTier"] = "PRO",
                        ["quotaLastUpdated"] = nowSec - 180,
                        ["total"] = 3,
                        ["familyWindows"] = BuildFamilyWindows(0.84, reset5h, 0.72, resetWeekly, 0.66, reset5h, 0.78, resetWeekly, 0.91, resetWeekly),
                    },
                    ["usage"] = new JsonObject
                    {
                        ["today"] = new JsonObject
                        {
                            ["total_tokens"] = 18_420_500L,
                            ["requests"] = 142L,
                            ["estimated_usd"] = 14.86,
                        },
                        ["totalTokens"] = 286_940_000L,
                        ["totalRequestCount"] = 2184L,
                        ["allTimeEstimatedUsd"] = 219.45,
                        ["todayHourly"] = BuildHourlyArray(),
                        ["todayByModel"] = BuildModelsArray(),
                    },
                },

                "account_list" => new JsonObject
                {
                    ["ok"] = true,
                    ["recovery"] = new JsonObject { ["available"] = false },
                    ["accounts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "acct-alex",
                            ["name"] = "Alex (主力工作号)",
                            ["email"] = "alex@example.invalid",
                            ["subscription_tier"] = "PRO",
                            ["isActive"] = true,
                            ["standbyDeferred"] = false,
                            ["quota"] = new JsonObject
                            {
                                ["last_updated"] = nowSec - 180,
                                ["familyWindows"] = BuildFamilyWindows(0.84, reset5h, 0.72, resetWeekly, 0.66, reset5h, 0.78, resetWeekly, 0.91, resetWeekly),
                            },
                        },
                        new JsonObject
                        {
                            ["id"] = "acct-bob",
                            ["name"] = "Bob (高额备用号)",
                            ["email"] = "bob@example.invalid",
                            ["subscription_tier"] = "ULTRA",
                            ["isActive"] = false,
                            ["standbyDeferred"] = false,
                            ["quota"] = new JsonObject
                            {
                                ["last_updated"] = nowSec - 420,
                                ["familyWindows"] = BuildFamilyWindows(0.98, resetAlt5h, 0.94, resetAltWeekly, 0.95, resetAlt5h, 0.92, resetAltWeekly, 1.0, resetAltWeekly),
                            },
                        },
                    },
                },

                "auto_switch_get" => new JsonObject
                {
                    ["ok"] = true,
                    ["config"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["strategy"] = "priority",
                        ["reserve_percentage"] = 15,
                        ["candidate_min_percentage"] = 40,
                    },
                },

                "auto_switch_best" => new JsonObject
                {
                    ["ok"] = true,
                    ["shouldSwitch"] = true,
                    ["reason"] = "ULTRA 备用池余量充足 (Gemini 98% · Claude/GPT 95%)",
                    ["bestCandidate"] = new JsonObject
                    {
                        ["id"] = "acct-bob",
                        ["name"] = "Bob (高额备用号)",
                        ["email"] = "bob@example.invalid",
                    },
                },

                "usage_summary" => new JsonObject
                {
                    ["ok"] = true,
                    ["snapshotId"] = "snap-demo-001",
                    ["summary"] = new JsonObject
                    {
                        ["totalTokens"] = 18_420_500L,
                        ["promptTokens"] = 2_840_000L,
                        ["cachedTokens"] = 14_790_500L,
                        ["candidatesTokens"] = 790_000L,
                        ["requestCount"] = 142L,
                        ["estimatedUsd"] = 14.86,
                    },
                    ["todayHourly"] = BuildHourlyArray(),
                    ["byModel"] = BuildModelsArray(),
                },

                "shortcut_scan" => new JsonObject
                {
                    ["ok"] = true,
                    ["shortcuts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["path"] = @"C:\Users\demo\Desktop\Antigravity.lnk",
                            ["location"] = "desktop",
                            ["name"] = "Antigravity (桌面)",
                            ["arguments"] = "--remote-debugging-port=0",
                            ["hasDebugPort"] = true,
                            ["hasBackup"] = true,
                        },
                        new JsonObject
                        {
                            ["path"] = @"C:\Users\demo\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Antigravity.lnk",
                            ["location"] = "start_menu",
                            ["name"] = "Antigravity (开始菜单)",
                            ["arguments"] = string.Empty,
                            ["hasDebugPort"] = false,
                            ["hasBackup"] = false,
                        },
                    },
                },

                "maintenance_scan" => new JsonObject
                {
                    ["ok"] = true,
                    ["buckets"] = new JsonObject
                    {
                        ["runtimeCache"] = new JsonObject { ["bytes"] = 428_500_000L },
                        ["historyLogs"] = new JsonObject { ["bytes"] = 96_200_000L },
                    },
                    ["protectedBytes"] = 1_180_000_000L,
                },

                "maintenance_history" => new JsonObject
                {
                    ["ok"] = true,
                    ["snapshots"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "restore-2026-11-22-01" },
                    },
                },

                "pricing_sync_status" => new JsonObject
                {
                    ["ok"] = true,
                    ["status"] = new JsonObject
                    {
                        ["message"] = "已同步公开价格表 (v2026-11-21) · 涵盖 Gemini / Claude / GPT 主流模型",
                    },
                },

                _ => new JsonObject { ["ok"] = true },
            };

            return Task.FromResult(response);
        }

        private static JsonObject BuildFamilyWindows(
            double gem5h, string gem5hReset,
            double gemWk, string gemWkReset,
            double claude5h, string claude5hReset,
            double claudeWk, string claudeWkReset,
            double argonWk, string argonWkReset)
        {
            return new JsonObject
            {
                ["gemini"] = new JsonObject
                {
                    ["fiveHour"] = new JsonObject { ["known"] = true, ["remaining"] = gem5h, ["resetTime"] = gem5hReset, ["stale"] = false },
                    ["weekly"] = new JsonObject { ["known"] = true, ["remaining"] = gemWk, ["resetTime"] = gemWkReset, ["stale"] = false },
                },
                ["claude"] = new JsonObject
                {
                    ["fiveHour"] = new JsonObject { ["known"] = true, ["remaining"] = claude5h, ["resetTime"] = claude5hReset, ["stale"] = false },
                    ["weekly"] = new JsonObject { ["known"] = true, ["remaining"] = claudeWk, ["resetTime"] = claudeWkReset, ["stale"] = false },
                },
                ["argon"] = new JsonObject
                {
                    ["weekly"] = new JsonObject { ["known"] = true, ["remaining"] = argonWk, ["resetTime"] = argonWkReset, ["stale"] = false },
                },
            };
        }

        private static JsonArray BuildHourlyArray()
        {
            long[] values =
            {
                120_000, 80_000, 45_000, 0, 0, 0,
                90_000, 310_000, 780_000, 1_420_000, 1_960_000, 1_540_000,
                980_000, 1_240_000, 2_180_000, 2_490_000, 1_860_000, 1_320_000,
                740_000, 520_000, 410_000, 220_000, 110_000, 5_500,
            };
            var arr = new JsonArray();
            for (int h = 0; h < 24; h++)
            {
                arr.Add(new JsonObject
                {
                    ["hour"] = h,
                    ["label"] = $"{h:00}:00",
                    ["total_tokens"] = values[h],
                    ["request_count"] = Math.Max(0, values[h] / 130_000),
                });
            }
            return arr;
        }

        private static JsonArray BuildModelsArray()
        {
            return new JsonArray
            {
                new JsonObject
                {
                    ["model"] = "Claude Opus 4.6",
                    ["totalTokens"] = 7_850_000L,
                    ["requestCount"] = 52L,
                    ["estimatedUsd"] = 7.92,
                    ["priced"] = true,
                },
                new JsonObject
                {
                    ["model"] = "Gemini 3.1 Pro",
                    ["totalTokens"] = 5_420_000L,
                    ["requestCount"] = 44L,
                    ["estimatedUsd"] = 3.84,
                    ["priced"] = true,
                },
                new JsonObject
                {
                    ["model"] = "Claude Sonnet 4.6",
                    ["totalTokens"] = 3_190_000L,
                    ["requestCount"] = 29L,
                    ["estimatedUsd"] = 2.38,
                    ["priced"] = true,
                },
                new JsonObject
                {
                    ["model"] = "Gemini 3 Flash",
                    ["totalTokens"] = 1_960_500L,
                    ["requestCount"] = 17L,
                    ["estimatedUsd"] = 0.72,
                    ["priced"] = true,
                },
            };
        }
    }
}
