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
    public partial bool IsLogToFileEnabled { get; set; } = SettingsService.LogToFile;

    public string LogsPath => AppPaths.LogsPath;

    [ObservableProperty]
    public partial bool IsImageEnabled { get; set; } = SettingsService.ImageEnabled;

    [ObservableProperty]
    public partial bool IsTextEnabled { get; set; } = SettingsService.TextEnabled;

    [ObservableProperty]
    public partial bool IsUrlImageEnabled { get; set; } = SettingsService.UrlImageEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageFormatDescription))]
    public partial int ImageFormatIndex { get; set; } = SettingsService.ImageSaveFormat switch
    {
        "Original" => 0,
        "Jpeg" => 1,
        _ => 2,
    };

    /// <summary>保存形式カードの説明。選択と矛盾しないよう選択肢ごとに切り替える。</summary>
    public string ImageFormatDescription => R.Get(ImageFormatIndex switch
    {
        0 => "ImageFormatDescOriginal",
        1 => "ImageFormatDescJpeg",
        _ => "ImageFormatDescPng",
    });

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

    /// <summary>MSIX(Store)版は更新をストアに任せるため、確認トグル自体を出さない。</summary>
    public Microsoft.UI.Xaml.Visibility UpdateCheckVisible =>
        PackageContext.IsPackaged ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    [ObservableProperty]
    public partial bool IsUpdateCheckEnabled { get; set; } = SettingsService.UpdateCheckEnabled;

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

    partial void OnIsUrlImageEnabledChanged(bool value)
    {
        SettingsService.UrlImageEnabled = value;
        _service.Note(R.Get(value ? "LogUrlImageOn" : "LogUrlImageOff"));
    }

    partial void OnImageFormatIndexChanged(int value)
    {
        SettingsService.ImageSaveFormat = value switch { 0 => "Original", 1 => "Jpeg", _ => "Png" };
        // ComboBoxItem の表示文字列(x:Uid の Content)をそのままログに使う
        var label = R.Get(value switch
        {
            0 => "ImageFormatOriginal/Content",
            1 => "ImageFormatJpeg/Content",
            _ => "ImageFormatPng/Content",
        });
        _service.Note(R.F("LogImageFormat", label));
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

    partial void OnIsLogToFileEnabledChanged(bool value)
    {
        SettingsService.LogToFile = value;
    }

    partial void OnIsUpdateCheckEnabledChanged(bool value)
    {
        SettingsService.UpdateCheckEnabled = value;
        _service.Note(R.Get(value ? "LogUpdateCheckOn" : "LogUpdateCheckOff"));
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            AppPaths.OpenFolder(AppPaths.LogsPath);
        }
        catch (Exception ex)
        {
            _service.Note(R.F("LogError", $"{ex.Message} — {ex.GetType().Name}"));
        }
    }

    [RelayCommand]
    private void CleanupNow() => _service.RunCleanup();

    [RelayCommand]
    private void OpenStagingFolder()
    {
        try
        {
            AppPaths.OpenFolder(AppPaths.StagingPath);
        }
        catch (Exception ex)
        {
            _service.Note(R.F("LogError", $"{ex.Message} — {ex.GetType().Name}"));
        }
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
