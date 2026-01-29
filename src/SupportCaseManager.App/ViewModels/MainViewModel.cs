using System.ComponentModel;
using System.Runtime.CompilerServices;
using SupportCaseManager.Core.Config;
using SupportCaseManager.Core.Logging;
using SupportCaseManager.Core.Repository;

namespace SupportCaseManager.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ConfigStore _config;
    private readonly CaseRepository _repository;
    private readonly IAppLogger _logger;
    private UserSettings _settings;
    private string _basePath = string.Empty;
    private bool _darkMode = true;
    private string _statusMessage = "準備完了";

    public MainViewModel(ConfigStore config, CaseRepository repository, IAppLogger logger)
    {
        _config = config;
        _repository = repository;
        _logger = logger;
        _settings = _config.Load();
        _basePath = _settings.BasePath;
        _darkMode = _settings.DarkMode;

        if (!string.IsNullOrWhiteSpace(_basePath))
        {
            _repository.SetBasePath(_basePath);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ConfigStore Config => _config;

    public CaseRepository Repository => _repository;

    public IAppLogger Logger => _logger;

    public UserSettings Settings => _settings;

    public string BasePath
    {
        get => _basePath;
        set
        {
            if (value == _basePath)
            {
                return;
            }

            _basePath = value;
            OnPropertyChanged();
        }
    }

    public bool DarkMode
    {
        get => _darkMode;
        set
        {
            if (value == _darkMode)
            {
                return;
            }

            _darkMode = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (value == _statusMessage)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public void PersistSettings()
    {
        _settings.BasePath = _basePath;
        _settings.DarkMode = _darkMode;
        _config.Save(_settings);
    }

    public void RefreshBasePath()
    {
        if (string.IsNullOrWhiteSpace(_basePath))
        {
            StatusMessage = "ベースフォルダが未設定です。";
            return;
        }

        try
        {
            _repository.SetBasePath(_basePath);
            _settings.BasePath = _basePath;
            _config.Save(_settings);
            StatusMessage = "ベースフォルダを更新しました。";
        }
        catch (Exception ex)
        {
            _logger.Error("ベースフォルダの更新に失敗しました。", ex);
            StatusMessage = "ベースフォルダの更新に失敗しました。";
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
