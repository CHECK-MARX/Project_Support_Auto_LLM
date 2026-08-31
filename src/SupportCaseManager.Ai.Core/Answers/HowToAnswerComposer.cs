using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Core.Answers;

public static partial class HowToAnswerComposer
{
    public enum CliCommandIntegrity
    {
        Complete,
        Incomplete,
        Ambiguous,
        Rejected,
    }

    public sealed record CliCommandProvenance(
        string CommandText,
        string RawCommandText,
        string NormalizedCommandText,
        string SourceEvidenceId,
        string SourceType,
        string DocumentTitle,
        int? PageNumber,
        string? SectionTitle,
        int Start,
        int End,
        CliCommandIntegrity Integrity);

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
        // Commands are extracted independently from each locator. Never join a prefix
        // from one source with options or paths from another source.
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

    public static bool TryComposeAnalysisCli(AnswerDraftRequest request, out string reply)
    {
        ArgumentNullException.ThrowIfNull(request);
        reply = string.Empty;
        if (!IsAnalysisCliQuestion(request) || request.Sources.Count == 0)
        {
            return false;
        }

        var sources = request.Sources
            .Where(static source => !IsPastCase(source.SourceType) && !string.IsNullOrWhiteSpace(source.Text))
            .ToList();
        var commands = sources
            .SelectMany(static source => ExtractAnalysisCommands(source.Text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        if (commands.Count == 0)
        {
            return false;
        }

        var preparation = FindBestEvidenceSentence(
            sources,
            ["コンパイル環境", "ソースファイル", "コンパイラ設定", "インクルードパス", "マクロ定義", "source file", "compiler", "include path", "macro"],
            requireAnalysisContext: true,
            excludedTerms: ["Validate", "アップロード", "ライセンス", "license"]);
        var purpose = FindBestEvidenceSentence(
            sources,
            ["解析を実行", "解析結果", "プロジェクトを解析", "analyze the project", "analysis result"],
            requireAnalysisContext: true,
            excludedTerms: ["Validate", "アップロード", "ライセンス", "license"]);
        var optionEvidence = FindOptionEvidence(sources, commands);
        var verification = FindVerificationInstructions(sources);
        var builder = new StringBuilder();
        builder.AppendLine("お問い合わせいただいたQACの解析CLIについて、選択された根拠から確認できる範囲をご案内します。");
        builder.AppendLine();
        AppendSection(builder, "【結論】", ["選択された根拠に記載された完全なコマンドだけを使用して解析を実行してください。"]);
        AppendSection(builder, "【実行目的】", purpose);
        AppendSection(builder, "【前提条件】", preparation);
        AppendSection(builder, "【CLIコマンド】", commands.Select((command, index) => $"{index + 1}. `{command}` を実行します。").ToList());
        AppendSection(builder, "【オプション】", optionEvidence);
        AppendSection(builder, "【実行後の確認】", verification);
        AppendSection(builder, "【注意事項】", ["根拠に記載のないオプション、値、バージョン、実行結果は補っていません。対象環境のマニュアルで最終確認してください。"]);
        AppendReferences(builder, sources);
        reply = builder.ToString().Trim();
        return true;
    }

    public static IReadOnlyList<CliCommandProvenance> ExtractAnalysisCommandProvenance(SearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalized = NormalizeCompactAnalysisCommand(source.Text ?? string.Empty);
        return ExtractAnalysisCommandRecords(normalized, source).ToArray();
    }

    private static bool IsAnalysisCliQuestion(AnswerDraftRequest request)
    {
        var profile = TopicEntityAnalyzer.Extract(
            request.InquiryText,
            SupportTopicCatalog.Create(request.Case.ProductName));
        return profile.Operations.Contains("Analysis", StringComparer.Ordinal) &&
            (profile.Intents.Contains("Command", StringComparer.Ordinal) ||
             ContainsAny(request.InquiryText, "CLI", "qacli", "コマンド", "オプション"));
    }

    private static IReadOnlyList<string> FindOptionEvidence(
        IReadOnlyList<SearchSource> sources,
        IReadOnlyList<string> commands)
    {
        var optionTokens = commands
            .SelectMany(command => Regex.Matches(command, @"(?<![A-Za-z0-9_])-{1,2}[A-Za-z][A-Za-z0-9_-]*", RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(match => match.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (optionTokens.Count == 0)
        {
            return [];
        }

        return sources
            .SelectMany(static source => SplitSentences(source.Text))
            .Where(sentence => optionTokens.Any(token => sentence.Contains(token, StringComparison.OrdinalIgnoreCase)) &&
                !ContainsAny(sentence, "Validate", "アップロード", "ライセンス", "license"))
            .Select(sentence => $"・{sentence}")
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();
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
        var matchingSource = sources.FirstOrDefault(static source =>
            AnalysisDialogProgressRegex().IsMatch(source.Text ?? string.Empty));
        if (matchingSource is not null)
        {
            return
            [
                "1. 解析中ダイアログにプロセスが表示されることを確認します。",
            ];
        }

        matchingSource = sources.FirstOrDefault(static source =>
            (source.Text ?? string.Empty).Contains("解析ダイアログ", StringComparison.OrdinalIgnoreCase));
        if (matchingSource is not null)
        {
            return
            [
                "1. 解析ダイアログで解析の状態を確認します。",
            ];
        }

        matchingSource = sources.FirstOrDefault(static source =>
            ContainsAny(source.Text ?? string.Empty, "［問題］パネル", "[問題]パネル", "問題パネル", "Problems panel"));
        if (matchingSource is not null)
        {
            return
            [
                "1. ［問題］パネルで解析後の診断結果を確認します。",
            ];
        }

        matchingSource = sources.FirstOrDefault(static source =>
            (source.Text ?? string.Empty).Contains("Progress(", StringComparison.OrdinalIgnoreCase) &&
            (source.Text ?? string.Empty).Contains("done", StringComparison.OrdinalIgnoreCase));
        if (matchingSource is not null)
        {
            return
            [
                "1. CLIの出力で `Progress(...): ... done` と表示され、処理が完了したことを確認します。",
                    matchingSource.Text.Contains("Successes and failures", StringComparison.OrdinalIgnoreCase)
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
        // Keep line/paragraph boundaries. A compact option or an attached prose token
        // makes the whole candidate ambiguous; it must not be repaired heuristically.
        var normalized = NormalizeCompactAnalysisCommand(value ?? string.Empty);
        foreach (var record in ExtractAnalysisCommandRecords(normalized, null))
        {
            if (record.Integrity == CliCommandIntegrity.Complete)
            {
                yield return record.CommandText;
            }
        }
    }

    private static IEnumerable<CliCommandProvenance> ExtractAnalysisCommandRecords(
        string normalized,
        SearchSource? source)
    {
        foreach (Match match in AnalysisCommandRegex().Matches(normalized))
        {
            var command = NormalizeAnalysisCommand(match.Value);
            var trailing = normalized[(match.Index + match.Length)..];
            var integrity = string.IsNullOrWhiteSpace(command)
                ? CliCommandIntegrity.Rejected
                : !IsCompleteAnalysisCommand(command)
                    ? CliCommandIntegrity.Incomplete
                    : HasAmbiguousTrailingToken(trailing)
                        ? CliCommandIntegrity.Ambiguous
                        : CliCommandIntegrity.Complete;
            yield return new CliCommandProvenance(
                command,
                match.Value,
                command,
                source?.SourceId ?? string.Empty,
                source?.SourceType ?? string.Empty,
                source?.DocumentTitle ?? source?.Title ?? string.Empty,
                source?.PageNumber,
                source?.SectionTitle,
                match.Index,
                match.Index + match.Length,
                integrity);
        }
    }

    private static bool HasAmbiguousTrailingToken(string value)
    {
        if (value.Length == 0 || char.IsWhiteSpace(value[0]))
        {
            return false;
        }

        return value[0] is not ('.' or ',' or '、' or '。' or ';' or '；' or ')' or '）' or ']' or '］');
    }

    private static bool IsCompleteAnalysisCommand(string command)
    {
        if (Regex.IsMatch(command, @"^qacli\s+analyze\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        // -P without a path is a fragment, even when the same locator contains
        // another option. Do not emit it or let a later stage complete it.
        return !Regex.IsMatch(
            command,
            @"(?:^|\s)-P(?:\s*$|\s+--?[A-Za-z]|\s+-[A-Za-z])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizeAnalysisCommand(string value)
    {
        return NormalizeWhitespace(value).TrimEnd('。', ',', '、', ';', '；');
    }

    private static string NormalizeCompactAnalysisCommand(string value)
    {
        return value;
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

    [GeneratedRegex(@"(?<![A-Za-z0-9_])qacli[ \t]+analyze(?:[ \t]+(?:--?[A-Za-z][A-Za-z0-9_-]*(?:<[^>\r\n]+>)?|<[^>\r\n]+>|[A-Za-z0-9_./:-]+))*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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
