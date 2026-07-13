using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Petapeta.Services;

namespace Petapeta.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ClipboardMonitorService _service = App.Monitor;
    private bool _suppressStartupChange;

    public SettingsViewModel()
    {
        StagingPath = AppPaths.StagingPath;
        _ = LoadStartupStateAsync();
    }

    /// <summary>マスター(監視)がオフのとき、対象トグルは操作不可にする。</summary>
    public bool IsMonitoringEnabled => _service.IsEnabled;

    [ObservableProperty]
    public partial string StagingPath { get; set; }

    [ObservableProperty]
    public partial bool IsStartMinimizedEnabled { get; set; } = SettingsService.StartMinimized;

    [ObservableProperty]
    public partial bool IsImageEnabled { get; set; } = SettingsService.ImageEnabled;

    [ObservableProperty]
    public partial bool IsTextEnabled { get; set; } = SettingsService.TextEnabled;

    [ObservableProperty]
    public partial double RetentionDays { get; set; } = SettingsService.RetentionDays;

    [ObservableProperty]
    public partial double MaxFiles { get; set; } = SettingsService.MaxFiles;

    [ObservableProperty]
    public partial int ThemeIndex { get; set; } = SettingsService.Theme switch
    {
        "Light" => 1,
        "Dark" => 2,
        _ => 0,
    };

    [ObservableProperty]
    public partial int LanguageIndex { get; set; } = SettingsService.Language switch
    {
        "ja" => 1,
        "en-US" => 2,
        _ => 0,
    };

    [ObservableProperty]
    public partial bool ShowRestartHint { get; set; }

    [ObservableProperty]
    public partial bool IsSoundEnabled { get; set; } = SettingsService.SoundEnabled;

    [ObservableProperty]
    public partial int SoundIndex { get; set; } =
        Math.Max(0, Array.IndexOf(SoundService.SoundTokens, SettingsService.SoundEvent));

    [ObservableProperty]
    public partial bool IsStartupEnabled { get; set; }

    partial void OnIsImageEnabledChanged(bool value)
    {
        SettingsService.ImageEnabled = value;
        _service.ImageEnabled = value;
        _service.Note(R.Get(value ? "LogImageOn" : "LogImageOff"));
    }

    partial void OnIsTextEnabledChanged(bool value)
    {
        SettingsService.TextEnabled = value;
        _service.TextEnabled = value;
        _service.Note(R.Get(value ? "LogTextOn" : "LogTextOff"));
    }

    partial void OnRetentionDaysChanged(double value)
    {
        if (!double.IsNaN(value) && value >= 1)
        {
            SettingsService.RetentionDays = (int)value;
        }
    }

    partial void OnMaxFilesChanged(double value)
    {
        if (!double.IsNaN(value) && value >= 1)
        {
            SettingsService.MaxFiles = (int)value;
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        var theme = value switch { 1 => "Light", 2 => "Dark", _ => "System" };
        SettingsService.Theme = theme;
        App.ApplyTheme(theme);
    }

    partial void OnLanguageIndexChanged(int value)
    {
        var language = value switch { 1 => "ja", 2 => "en-US", _ => "" };
        SettingsService.Language = language;
        Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        // 既に描画済みの UI には効かないため、再起動を促す
        ShowRestartHint = true;
    }

    partial void OnIsSoundEnabledChanged(bool value)
    {
        SettingsService.SoundEnabled = value;
    }

    partial void OnSoundIndexChanged(int value)
    {
        if (value >= 0 && value < SoundService.SoundTokens.Length)
        {
            SettingsService.SoundEvent = SoundService.SoundTokens[value];
            SoundService.Play(SettingsService.SoundEvent);
        }
    }

    [RelayCommand]
    private void TestSound() => SoundService.Play(SettingsService.SoundEvent);

    partial void OnIsStartupEnabledChanged(bool value)
    {
        if (_suppressStartupChange)
        {
            return;
        }
        _ = ApplyStartupAsync(value);
    }

    partial void OnIsStartMinimizedEnabledChanged(bool value)
    {
        SettingsService.StartMinimized = value;
    }

    [RelayCommand]
    private void CleanupNow() => _service.RunCleanup();

    [RelayCommand]
    private void OpenStagingFolder()
    {
        System.Diagnostics.Process.Start("explorer.exe", AppPaths.EnsureStaging());
    }

    private Task LoadStartupStateAsync()
    {
        try
        {
            _suppressStartupChange = true;
            IsStartupEnabled = StartupRegistration.IsEnabled();
            _suppressStartupChange = false;
        }
        catch (Exception ex)
        {
            _service.Note(R.F("LogStartupQueryFailed", ex.Message));
        }
        return Task.CompletedTask;
    }

    private Task ApplyStartupAsync(bool enable)
    {
        try
        {
            // shell:startup へのショートカットで管理(パッケージ有無を問わず共通。#1)
            StartupRegistration.SetEnabled(enable);
            _service.Note(R.Get(enable ? "LogStartupOn" : "LogStartupOff"));
        }
        catch (Exception ex)
        {
            _service.Note(R.F("LogStartupChangeFailed", ex.Message));
        }
        return Task.CompletedTask;
    }
}
