using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Core.Answers;

public static partial class HowToAnswerComposer
{
    private static readonly string[] RequiredHeadings =
    [
        "【事前準備】", "【GUIでの手順】", "【CLIでの手順】",
        "【解析結果の確認】", "【注意点】", "【参照先】",
    ];

    public static bool IsAnalysisHowTo(AnswerDraftRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = TopicEntityAnalyzer.Extract(
            request.InquiryText,
            SupportTopicCatalog.Create(request.Case.ProductName));
        return profile.Operations.Contains("Analysis", StringComparer.Ordinal) &&
            profile.Intents.Contains("HowTo", StringComparer.Ordinal);
    }

    public static bool HasRequiredStructure(string? value) =>
        RequiredHeadings.All(heading => (value ?? string.Empty).Contains(heading, StringComparison.Ordinal));

    public static bool TryComposeAnalysis(AnswerDraftRequest request, out string reply)
    {
        ArgumentNullException.ThrowIfNull(request);
        reply = string.Empty;
        if (!IsAnalysisHowTo(request) || request.Sources.Count == 0)
        {
            return false;
        }

        var sources = request.Sources
            .Where(static source => !string.IsNullOrWhiteSpace(source.Text))
            .ToList();
        if (sources.Count == 0)
        {
            return false;
        }

        var preparation = FindBestEvidenceSentence(
            sources,
            ["コンパイル環境", "ソースファイル", "コンパイラ設定", "インクルードパス", "マクロ定義", "source file", "compiler", "include path", "macro"],
            requireAnalysisContext: true,
            excludedTerms: ["Validate", "アップロード", "ライセンス", "license", "gcc-", "実行されません", "解析が失敗", "解析に失敗"]);
        var projectSetup = FindBestEvidenceSentence(
            sources,
            ["QACプロジェクトを作成", "QACプロジェクトを開", "プロジェクトを設定", "project file", "open the project", "create a project"],
            requireAnalysisContext: false,
            excludedTerms: ["Validateプロジェクト", "アップロード", "connect", "--push"]);
        var gui = FindGuiInstruction(sources);
        var commands = sources
            .SelectMany(static source => ExtractAnalysisCommands(source.Text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        var verification = FindVerificationInstructions(sources);
        if (verification.Count == 0)
        {
            verification = FindBestEvidenceSentence(
            sources,
            ["結果を確認", "解析結果を確認", "解析結果を表示", "解析ダイアログ", "解析中", "解析完了", "問題パネル", "メッセージを確認", "レポートを確認", "check the analysis result", "view the analysis result", "analysis status", "Progress(", "Successes and failures"],
            requireAnalysisContext: true,
            excludedTerms: ["Validate", "アップロード", "ライセンス", "license", "obfuscated"]);
        }

        var builder = new StringBuilder();
        builder.AppendLine("お問い合わせいただいた、QACでプロジェクトを解析する手順についてご案内します。");
        builder.AppendLine();
        AppendSection(builder, "【事前準備】", preparation, projectSetup);
        AppendSection(builder, "【GUIでの手順】", gui);
        AppendSection(
            builder,
            "【CLIでの手順】",
            commands.Count == 0
                ? []
                : commands.Select((command, index) => $"{index + 1}. `{command}` を実行します。").ToList());
        AppendSection(builder, "【解析結果の確認】", verification);
        AppendSection(
            builder,
            "【注意点】",
            ["対象バージョンやビルド環境で手順が異なる場合があります。選択された根拠に記載された範囲を超える画面項目やオプションは、該当バージョンのマニュアルで確認してください。"]);
        AppendReferences(builder, sources);
        reply = builder.ToString().Trim();
        return true;
    }

    private static void AppendSection(
        StringBuilder builder,
        string heading,
        params IReadOnlyList<string>[] groups)
    {
        builder.AppendLine(heading);
        var lines = groups
            .SelectMany(static group => group)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();
        if (lines.Count == 0)
        {
            builder.AppendLine("選択された根拠から確認できません。");
        }
        else
        {
            foreach (var line in lines)
            {
                builder.AppendLine(line);
            }
        }

        builder.AppendLine();
    }

    private static void AppendReferences(StringBuilder builder, IReadOnlyList<SearchSource> sources)
    {
        builder.AppendLine("【参照先】");
        foreach (var source in sources
            .Where(static source => !IsPastCase(source.SourceType))
            .DistinctBy(static source => string.Join('|', source.DocumentId, source.DocumentTitle, source.PageNumber, source.SectionTitle))
            .Take(5))
        {
            var title = FirstNonEmpty(source.DocumentTitle, source.Title, source.DocumentId) ?? "参照文書";
            builder.AppendLine($"・『{title}』");
            if (source.PageNumber is > 0)
            {
                builder.AppendLine($"  Page {source.PageNumber}");
            }
            if (!string.IsNullOrWhiteSpace(source.SectionTitle))
            {
                builder.AppendLine($"  「{source.SectionTitle.Trim()}」項");
            }
            if (!string.IsNullOrWhiteSpace(source.Url))
            {
                builder.AppendLine($"  URL: {source.Url}");
            }
        }
    }

    private static bool IsPastCase(string sourceType) =>
        sourceType.Equals("PastCase", StringComparison.OrdinalIgnoreCase) ||
        sourceType.Equals("PastCaseNote", StringComparison.OrdinalIgnoreCase) ||
        sourceType.Equals("PastAnswer", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> FindBestEvidenceSentence(
        IReadOnlyList<SearchSource> sources,
        IReadOnlyList<string> terms,
        bool requireAnalysisContext,
        IReadOnlyList<string> excludedTerms)
    {
        var match = sources
            .SelectMany((source, sourceIndex) => SplitSentences(source.Text)
                .Select(sentence => new
                {
                    Sentence = sentence,
                    Score = ScoreEvidenceSentence(
                        sentence,
                        terms,
                        excludedTerms,
                        requireAnalysisContext,
                        source.SourceType,
                        sourceIndex),
                }))
            .Where(static item => item.Score > 0)
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Sentence.Length)
            .FirstOrDefault();
        return match is null ? [] : [$"・{match.Sentence}"];
    }

    private static IReadOnlyList<string> FindGuiInstruction(IReadOnlyList<SearchSource> sources)
    {
        var qaguiProjectAnalysis = sources
            .SelectMany((source, sourceIndex) => QaguiProjectAnalysisRegex().Matches(source.Text ?? string.Empty)
                .Cast<Match>()
                .Select(match => new
                {
                    Instruction = NormalizeGuiInstruction(match.Value),
                    SourceIndex = sourceIndex,
                }))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Instruction))
            .OrderBy(static item => item.SourceIndex)
            .ThenBy(static item => item.Instruction.Length)
            .FirstOrDefault();
        if (qaguiProjectAnalysis is not null)
        {
            return [$"1. {qaguiProjectAnalysis.Instruction}"];
        }

        var menuPath = sources
            .SelectMany((source, sourceIndex) => AnalysisGuiMenuRegex().Matches(source.Text ?? string.Empty)
                .Cast<Match>()
                .Select(match => new
                {
                    Instruction = NormalizeGuiInstruction(match.Value),
                    SourceIndex = sourceIndex,
                }))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Instruction))
            .OrderBy(static item => item.SourceIndex)
            .ThenBy(static item => item.Instruction.Length)
            .FirstOrDefault();
        if (menuPath is not null)
        {
            return [$"1. {menuPath.Instruction}"];
        }

        var match = sources
            .SelectMany((source, sourceIndex) => SplitSentences(source.Text)
                .Select(sentence => new
                {
                    Sentence = sentence,
                    Score = ScoreGuiSentence(sentence, source.SourceType, sourceIndex),
                }))
            .Where(static item => item.Score > 0)
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Sentence.Length)
            .FirstOrDefault();
        return match is null ? [] : [$"1. {match.Sentence}"];
    }

    private static IReadOnlyList<string> FindVerificationInstructions(IReadOnlyList<SearchSource> sources)
    {
        var text = string.Join('\n', sources.Select(static source => source.Text));
        if (AnalysisDialogProgressRegex().IsMatch(text))
        {
            return
            [
                "1. 解析中ダイアログにプロセスが表示されることを確認します。",
            ];
        }

        if (text.Contains("解析ダイアログ", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "1. 解析ダイアログで解析の状態を確認します。",
            ];
        }

        if (ContainsAny(text, "［問題］パネル", "[問題]パネル", "問題パネル", "Problems panel"))
        {
            return
            [
                "1. ［問題］パネルで解析後の診断結果を確認します。",
            ];
        }

        if (text.Contains("Progress(", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("done", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "1. CLIの出力で `Progress(...): ... done` と表示され、処理が完了したことを確認します。",
                text.Contains("Successes and failures", StringComparison.OrdinalIgnoreCase)
                    ? "2. 続けて `Successes and failures` の集計を確認します。"
                    : string.Empty,
            ];
        }

        return [];
    }

    private static int ScoreEvidenceSentence(
        string sentence,
        IReadOnlyList<string> terms,
        IReadOnlyList<string> excludedTerms,
        bool requireAnalysisContext,
        string sourceType,
        int sourceIndex)
    {
        if (!terms.Any(term => sentence.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
            excludedTerms.Any(term => sentence.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        var hasAnalysis = ContainsAny(sentence, "解析", "analyze", "analysis");
        if (requireAnalysisContext && !hasAnalysis)
        {
            return 0;
        }

        var score = 10 + terms.Count(term => sentence.Contains(term, StringComparison.OrdinalIgnoreCase)) * 4;
        score += hasAnalysis ? 8 : 0;
        score += sourceType.Equals("OfficialDoc", StringComparison.OrdinalIgnoreCase) ? 3 :
            sourceType.Equals("Manual", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        score -= sourceIndex;
        score -= sentence.Length > 320 ? 8 : 0;
        return score;
    }

    private static int ScoreGuiSentence(string sentence, string sourceType, int sourceIndex)
    {
        if (!ContainsAny(sentence, "解析", "Analyze", "Analysis") ||
            ContainsAny(sentence, "Validate", "アップロード", "ライセンス", "license", "環境変数"))
        {
            return 0;
        }

        var actionCount = new[]
        {
            "クリック", "選択", "メニュー", "ダイアログ", "QAGUI", "QA·GUI", "GUI", "IDEから", "Analyze Project", "Run Analysis",
        }.Count(term => sentence.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (actionCount == 0)
        {
            return 0;
        }

        var score = actionCount * 8;
        score += ContainsAny(sentence, "実行", "開始", "確認", "run") ? 6 : 0;
        score += sourceType.Equals("OfficialDoc", StringComparison.OrdinalIgnoreCase) ? 3 :
            sourceType.Equals("Manual", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        score -= sourceIndex;
        score -= sentence.Length > 320 ? 8 : 0;
        return score;
    }

    private static IEnumerable<string> ExtractAnalysisCommands(string value)
    {
        var normalized = NormalizeCompactAnalysisCommand(value ?? string.Empty);
        foreach (Match match in AnalysisCommandRegex().Matches(normalized))
        {
            var command = NormalizeAnalysisCommand(match.Value);
            if (!string.IsNullOrWhiteSpace(command))
            {
                yield return command;
            }
        }
    }

    private static string NormalizeAnalysisCommand(string value)
    {
        var command = NormalizeWhitespace(value).TrimEnd('.', '。', ',', '、', ';', '；');
        command = Regex.Replace(command, "^qaclianalyze", "qacli analyze", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        command = Regex.Replace(command, "(?<=analyze)-", " -", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        command = Regex.Replace(
            command,
            "(?<=[A-Za-z0-9>])(?=-(?:P|cf)(?![A-Za-z0-9_-]))",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        command = Regex.Replace(
            command,
            "(?<=>)(?=--?(?:P|C|cf|csga|raw-source|language-cct)(?![A-Za-z0-9_-]))",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var proseStart = JapaneseProseStartRegex().Match(command);
        if (proseStart.Success)
        {
            command = command[..proseStart.Index].TrimEnd();
        }
        return command;
    }

    private static string NormalizeCompactAnalysisCommand(string value)
    {
        var normalized = Regex.Replace(
            value,
            "qacli\\s*analyze",
            "qacli analyze",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return normalized;
    }

    private static string NormalizeGuiInstruction(string value)
    {
        var instruction = NormalizeWhitespace(value).TrimEnd('.', '。', ',', '、', ';', '；');
        return $"QAGUIで {instruction} を選択して解析を実行します。";
    }

    private static IEnumerable<string> SplitSentences(string value)
    {
        var normalized = NormalizeWhitespace(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        foreach (var sentence in SentenceBoundaryRegex().Split(normalized))
        {
            var trimmed = sentence.Trim();
            if (trimmed.Length is > 0 and <= 480)
            {
                yield return trimmed;
            }
        }
    }

    private static string NormalizeWhitespace(string? value) =>
        WhitespaceRegex().Replace(value ?? string.Empty, " ").Trim();

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])qacli\s*analyze(?:\s*(?:--?[A-Za-z][A-Za-z0-9_-]*(?:[ =]?(?:<[^>]+>|[^\s。；;]+))?|<[^>]+>)){0,8}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnalysisCommandRegex();

    [GeneratedRegex(@"(?<=[A-Za-z0-9>])(?=[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}])", RegexOptions.CultureInvariant)]
    private static partial Regex JapaneseProseStartRegex();

    [GeneratedRegex(@"(?:\[|［)解析(?:\([A-Za-z]\)|（[A-Za-z]）)?(?:\]|］)\s*[>＞]\s*(?:\[|［)[^\]］\r\n]{1,80}(?:\]|］)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnalysisGuiMenuRegex();

    [GeneratedRegex(@"(?:\[|［)解析(?:\([A-Za-z]\)|（[A-Za-z]）)?(?:\]|］)\s*[>＞]\s*プロジェクト全体のファイルベース解析", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QaguiProjectAnalysisRegex();

    [GeneratedRegex(@"解析中ダイアログボックスにプロセスが表示され", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnalysisDialogProgressRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?<=[。！？])\s*|(?<=[.!?])\s+(?=[A-Z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBoundaryRegex();
}
