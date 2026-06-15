using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed class SearchSourceViewModel : ObservableObject
{
    private const int ExcerptMaxLength = 320;

    private bool isSelected;
    private bool wasUsedInLastDraft;
    private bool willBeSentToLlm;
    private bool isExcludedByLimit;
    private bool isExcludedByScore;
    private bool isManuallySelected;
    private bool isManuallyExcluded;

    public SearchSourceViewModel(SearchSource source, bool isSelected)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        this.isSelected = isSelected;
    }

    public SearchSource Source { get; }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (SetProperty(ref isSelected, value))
            {
                isManuallySelected = value;
                isManuallyExcluded = !value;
                OnPropertyChanged(nameof(IsManuallySelected));
                OnPropertyChanged(nameof(IsManuallyExcluded));
                OnPropertyChanged(nameof(SendStatusText));
                OnPropertyChanged(nameof(SelectionReasonText));
            }
        }
    }

    public bool IsManuallySelected => isManuallySelected;

    public bool IsManuallyExcluded => isManuallyExcluded;

    public void SetSelectedProgrammatically(bool value)
    {
        if (SetProperty(ref isSelected, value, nameof(IsSelected)))
        {
            OnPropertyChanged(nameof(SendStatusText));
            OnPropertyChanged(nameof(SelectionReasonText));
        }
    }

    public void RestoreSelectionState(bool selected, bool manuallySelected, bool manuallyExcluded)
    {
        isSelected = selected;
        isManuallySelected = manuallySelected;
        isManuallyExcluded = manuallyExcluded;
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsManuallySelected));
        OnPropertyChanged(nameof(IsManuallyExcluded));
        OnPropertyChanged(nameof(SendStatusText));
        OnPropertyChanged(nameof(SelectionReasonText));
    }

    public bool WasUsedInLastDraft
    {
        get => wasUsedInLastDraft;
        set
        {
            if (SetProperty(ref wasUsedInLastDraft, value))
            {
                OnPropertyChanged(nameof(UsedInLastDraftText));
            }
        }
    }

    public bool WillBeSentToLlm
    {
        get => willBeSentToLlm;
        set
        {
            if (SetProperty(ref willBeSentToLlm, value))
            {
                OnPropertyChanged(nameof(SendStatusText));
                OnPropertyChanged(nameof(SelectionReasonText));
            }
        }
    }

    public bool IsExcludedByLimit
    {
        get => isExcludedByLimit;
        set
        {
            if (SetProperty(ref isExcludedByLimit, value))
            {
                OnPropertyChanged(nameof(SendStatusText));
                OnPropertyChanged(nameof(SelectionReasonText));
            }
        }
    }

    public bool IsExcludedByScore
    {
        get => isExcludedByScore;
        set
        {
            if (SetProperty(ref isExcludedByScore, value))
            {
                OnPropertyChanged(nameof(SendStatusText));
                OnPropertyChanged(nameof(SelectionReasonText));
            }
        }
    }

    public string SourceId => Source.SourceId ?? string.Empty;

    public string SourceType => Source.SourceType ?? string.Empty;

    public string Title => Source.Title ?? string.Empty;

    public string Text => Source.Text ?? string.Empty;

    public string? FilePath => Source.FilePath;

    public string? Url => Source.Url;

    public DateTimeOffset? RetrievedAt => Source.RetrievedAt;

    public string RetrievedAtText => RetrievedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "-";

    public string? SupportNumber => Source.SupportNumber;

    public string? ProductName => Source.ProductName;

    public double? Score => Source.Score;

    public string ScoreText => Score?.ToString("0.000") ?? "-";

    public string NoteKind => SourceType.StartsWith("PastCaseNote", StringComparison.OrdinalIgnoreCase)
        ? "PastCaseNote"
        : SourceType;

    public string UsedInLastDraftText => WasUsedInLastDraft ? "Yes" : "No";

    public string MatchedTermsText => Source.MatchedTerms.Count == 0
        ? "-"
        : string.Join(", ", Source.MatchedTerms);

    public string ScoreBreakdown => string.IsNullOrWhiteSpace(Source.ScoreBreakdown)
        ? "-"
        : Source.ScoreBreakdown;

    public string QueryCoverage => string.IsNullOrWhiteSpace(Source.QueryCoverage)
        ? "-"
        : Source.QueryCoverage;

    public string SelectionReasonText
    {
        get
        {
            if (WillBeSentToLlm)
            {
                return IsManuallySelected ? "Manually selected; will send" : "Score is above auto-select threshold; will send";
            }

            if (IsExcludedByLimit)
            {
                return "Selected but excluded by MaxEvidenceItems limit.";
            }

            if (IsManuallyExcluded)
            {
                return "Manually excluded.";
            }

            if (IsExcludedByScore)
            {
                return "Excluded because score is below the auto-select/display threshold.";
            }

            return IsSelected ? "Selected." : "Not selected.";
        }
    }

    public string SendStatusText
    {
        get
        {
            if (!IsSelected)
            {
                return IsManuallyExcluded ? "Manually excluded" : IsExcludedByScore ? "Excluded by score" : "Not selected";
            }

            if (WillBeSentToLlm)
            {
                return "Will send";
            }

            if (IsExcludedByLimit)
            {
                return "Excluded by limit";
            }

            return IsExcludedByScore ? "Excluded by score" : "Selected";
        }
    }

    public string Excerpt
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Text))
            {
                return string.Empty;
            }

            var normalized = string.Join(
                " ",
                Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return normalized.Length <= ExcerptMaxLength
                ? normalized
                : normalized[..ExcerptMaxLength] + "...";
        }
    }
}
