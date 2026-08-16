using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace SSHTunnel4Win.Services;

public partial class AppSettings : ObservableObject
{
    private const string REGKEY = @"Software\TypoStudio\SSHTunnel";
    private const string RUN_KEY = "SSHTunnel";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SSHTunnel", "settings.json");

    [ObservableProperty] private bool _launchAtLogin;
    [ObservableProperty] private bool _openManagerOnLaunch;
    [ObservableProperty] private bool _autoCheckForUpdates = true;

    public AppSettings()
    {
        Load();
    }

    partial void OnLaunchAtLoginChanged(bool value)
    {
        Save();
        UpdateRegistryAutoStart(value);
    }

    partial void OnOpenManagerOnLaunchChanged(bool value) => Save();
    partial void OnAutoCheckForUpdatesChanged(bool value) => Save();

    private void Load()
    {

        SettingsData? data = null;

        if (File.Exists(FilePath))
        {
            try
            {
                var json = File.ReadAllText(FilePath);
                data = JsonSerializer.Deserialize<SettingsData>(json);
                if (data == null) return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }
 
        }
        else
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(REGKEY);
                if (key?.GetValue("SettingsBackup") is string json)
                {
                    data = JsonSerializer.Deserialize<SettingsData>(json);
                }
            }
            catch { }
        }

        if (data != null)
        {
#pragma warning disable MVVMTK0034
            _launchAtLogin = data.LaunchAtLogin;
            _openManagerOnLaunch = data.OpenManagerOnLaunch;
            _autoCheckForUpdates = data.AutoCheckForUpdates;
#pragma warning restore MVVMTK0034
            Save();

            UpdateRegistryAutoStart(LaunchAtLogin);
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(REGKEY);
            if (key?.GetValue("SettingsBackup") == null) Save();
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var data = new SettingsData
            {
                LaunchAtLogin = LaunchAtLogin,
                OpenManagerOnLaunch = OpenManagerOnLaunch,
                AutoCheckForUpdates = AutoCheckForUpdates
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(REGKEY);
                key.SetValue("SettingsBackup", json);
            }
            catch { throw; }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }

    private static void UpdateRegistryAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (enable)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                {
                    key.SetValue(RUN_KEY, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(RUN_KEY, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to update registry: {ex.Message}");
        }
    }

    private class SettingsData
    {
        public bool LaunchAtLogin { get; set; }
        public bool OpenManagerOnLaunch { get; set; }
        public bool AutoCheckForUpdates { get; set; } = true;
    }
}
