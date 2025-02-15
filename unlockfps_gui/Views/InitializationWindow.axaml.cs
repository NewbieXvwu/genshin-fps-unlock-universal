﻿using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using UnlockFps.Gui.Utils;
using UnlockFps.Gui.ViewModels;
using UnlockFps.Services;
using UnlockFps.Utils;

namespace UnlockFps.Gui.ViewModels
{
    public class InitializationWindowViewModel : ViewModelBase
    {
        public required Config Config { get; init; }
        public bool IsSearching { get; set; } = true;
        public ObservableCollection<string> InstallationPaths { get; } = new();
        public string? SelectedInstallationPath { get; set; }
    }
}

namespace UnlockFps.Gui.Views
{
    public partial class InitializationWindow : Window
    {
        private readonly ConfigService _configService;
        private readonly InitializationWindowViewModel _viewModel;

        private CancellationTokenSource? _cts;

#if DEBUG
        public InitializationWindow()
        {
            if (!Design.IsDesignMode) throw new InvalidOperationException();
            InitializeComponent();
        }
#endif

        public InitializationWindow(ConfigService configService)
        {
            this.SetSystemChrome();
            _configService = configService;
            DataContext = _viewModel = new InitializationWindowViewModel()
            {
                Config = _configService.Config,
                SelectedInstallationPath = configService.Config.LaunchOptions.GamePath
            };

            InitializeComponent();
        }

        private async void Control_OnLoaded(object? sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            StartSearchWindow();
            await Task.Run(() => SearchRegistry(_cts.Token));
        }

        private void Control_OnUnloaded(object? sender, RoutedEventArgs e)
        {
            if (_cts is { } cts)
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        private void StartSearchWindow()
        {
            Task.Run(async () =>
            {
                while (_cts is { Token: { IsCancellationRequested: false } token })
                {
                    if (await FindWindowAsync())
                    {
                        break;
                    }

                    await Task.Delay(1000, token);
                }

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var infoWindow = App.DefaultServices.GetRequiredService<AlertWindow>();
                    infoWindow.IsError = false;
                    infoWindow.Text = $"""
                                       Game Found!
                                       {_configService.Config.LaunchOptions.GamePath}
                                       """;
                    await infoWindow.ShowDialog(this);
                    Close();
                });
            });
        }

        private async ValueTask<bool> FindWindowAsync()
        {
            IntPtr windowHandle = IntPtr.Zero;
            IntPtr processHandle = IntPtr.Zero;
            string processPath = string.Empty;

            Native.EnumWindows((hWnd, lParam) =>
            {
                var win32Window = new Win32Window(hWnd);
                if (win32Window.ClassName != "UnityWndClass") return true;

                windowHandle = hWnd;
                var err = Native.GetWindowThreadProcessId(hWnd, out var pid);
                if (err == 0) return true;

                processPath = ProcessUtils.GetProcessPathFromPid(pid, out processHandle);
                return false;
            }, IntPtr.Zero);

            if (windowHandle == IntPtr.Zero)
                return false;

            if (string.IsNullOrEmpty(processPath))
            {
                var alertWindow = App.DefaultServices.GetRequiredService<AlertWindow>();
                alertWindow.Text = """
                                   Failed to find process path.
                                   Please use "Browse" instead.
                                   """;
                await alertWindow.ShowDialog(this);
                return false;
            }

            Native.TerminateProcess(processHandle, 0);
            Native.CloseHandle(processHandle);

            _configService.Config.LaunchOptions.GamePath = Path.GetFullPath(processPath);
            _configService.Save();
            return true;
        }

#pragma warning disable CA1416
        private void SearchRegistry(CancellationToken token = default)
        {
            if (_viewModel == null) return;

            using var uninstallKey =
                Registry.LocalMachine?.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstallKey == null) return;

            var keys = uninstallKey.GetSubKeyNames();
            var installationPaths = _viewModel.InstallationPaths;

            foreach (var key in keys)
            {
                if (key is not ("Genshin Impact" or "\u539f\u795e")) continue;

                using var subKey = uninstallKey.OpenSubKey(key);
                if (subKey == null)
                {
                    return;
                }

                var installationDir = (string?)subKey.GetValue("InstallPath");
                if (!Directory.Exists(installationDir)) continue;

                var configPath = Path.Combine(installationDir, "config.ini");
                if (!File.Exists(configPath)) continue;

                string? gamePath = null;
                string? gameName = null;
                var configLines = File.ReadLines(configPath);
                foreach (var line in configLines)
                {
                    var indexOf = line.IndexOf('=');
                    if (indexOf < 0) continue;

                    var iniKey = GetIniKey(line, indexOf);
                    if (iniKey.Equals("game_install_path", StringComparison.Ordinal))
                    {
                        gamePath = ConvertIniValue(GetIniValue(line, indexOf));
                    }
                    else if (iniKey.Equals("game_start_name", StringComparison.Ordinal))
                    {
                        gameName = GetIniValue(line, indexOf);
                    }
                }

                if (gamePath == null || gameName == null) continue;

                var combine = Path.GetFullPath(Path.Combine(gamePath, gameName));
                if (File.Exists(combine))
                {
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        installationPaths.Add(Path.GetFullPath(combine));
                    });
                }
            }

            var selectedPath = _viewModel.SelectedInstallationPath;
            if (installationPaths.Count > 0 && (selectedPath == null || !installationPaths.Contains(selectedPath)))
            {
                _viewModel.SelectedInstallationPath = installationPaths[0];
            }

            _viewModel.IsSearching = false;
        }
#pragma warning restore CA1416

        private static string GetIniKey(string s, int indexOf)
        {
            return s.Substring(0, indexOf).Trim();
        }

        private static string GetIniValue(string s, int indexOf)
        {
            return s.Substring(indexOf + 1).Trim();
        }

        private static string ConvertIniValue(string iniValue)
        {
            var stringBuilder = new StringBuilder();
            var charSpan = iniValue.AsSpan();
            for (var i = 0; i < charSpan.Length; i++)
            {
                var c = iniValue[i];
                if (c == '\\' && i < charSpan.Length - 5 && charSpan[i + 1] == 'x')
                {
                    var readOnlySpan = charSpan.Slice(i + 2, 4);
                    if (ushort.TryParse(readOnlySpan, NumberStyles.HexNumber, null, out var value))
                    {
                        stringBuilder.Append((char)value);
                        i += 5;
                        continue;
                    }
                }

                stringBuilder.Append(c);
            }

            return stringBuilder.ToString();
        }

        private void BtnConfirm_OnClick(object? sender, RoutedEventArgs e)
        {
            var selectedPath = _viewModel.SelectedInstallationPath;
            if (string.IsNullOrEmpty(selectedPath))
                return;

            _configService.Config.LaunchOptions.GamePath = selectedPath;
            _configService.Save();
            Close();
        }

        private async void BtnBrowse_OnClick(object? sender, RoutedEventArgs e)
        {
            var selectedPath = (await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select GenshinImpact.exe or YuanShen.exe",
                FileTypeFilter =
                [
                    new FilePickerFileType("Executable Files (*.exe)") { Patterns = ["GenshinImpact.exe;YuanShen.exe"] }
                ],
                AllowMultiple = true
            })).FirstOrDefault()?.TryGetLocalPath();
            if (selectedPath == null) return;

            var fileName = Path.GetFileNameWithoutExtension(selectedPath);

            if (fileName != "GenshinImpact" && fileName != "YuanShen")
            {
                var alertWindow = App.DefaultServices.GetRequiredService<AlertWindow>();
                alertWindow.Text =
                    $"""
                     Please select the game exe
                     GenshinImpact.exe or YuanShen.exe
                     """;
                await alertWindow.ShowDialog(this);
                return;
            }

            var directory = Path.GetDirectoryName(selectedPath);
            var unityPlayer = Path.Combine(directory, "UnityPlayer.dll");
            if (!File.Exists(unityPlayer))
            {
                var alertWindow = App.DefaultServices.GetRequiredService<AlertWindow>();
                alertWindow.Text = "That's not the right place";
                await alertWindow.ShowDialog(this);
                return;
            }

            _configService.Config.LaunchOptions.GamePath = selectedPath;
            _configService.Save();
            Close();
        }
    }
}