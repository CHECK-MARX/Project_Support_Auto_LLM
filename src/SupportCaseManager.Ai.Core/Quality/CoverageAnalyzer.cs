using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Core.Quality;

public static partial class CoverageAnalyzer
{
    public const string Command = "Command";
    public const string Options = "Options";
    public const string Authentication = "Authentication";
    public const string Connection = "Connection";
    public const string UploadProcedure = "UploadProcedure";
    public const string ValidateVerification = "ValidateVerification";
    public const string Troubleshooting = "Troubleshooting";
    public const string StreamOverview = "StreamOverview";
    public const string Purpose = "Purpose";
    public const string StreamCreation = "StreamCreation";
    public const string QacAssociation = "QacAssociation";
    public const string Configuration = "Configuration";
    public const string Verification = "Verification";
    public const string ProjectAssociation = "ProjectAssociation";
    public const string UploadCommand = "UploadCommand";
    public const string CommandOptions = "CommandOptions";
    public const string BuildName = "BuildName";
    public const string IncrementalBuild = "IncrementalBuild";
    public const string Overview = "Overview";
    public const string PriorCaseSupplement = "PriorCaseSupplement";
    public const string AnalysisProcedure = "AnalysisProcedure";
    public const string AnalysisCommand = "AnalysisCommand";
    public const string AnalysisVerification = "AnalysisVerification";

    private static readonly string[] UploadRequirements =
    [
        Command, Options, Authentication, Connection, UploadProcedure,
        ValidateVerification, Troubleshooting,
    ];

    private static readonly string[] StreamRequirements =
    [
        StreamOverview, Purpose, StreamCreation, QacAssociation, Configuration, Verification,
    ];

    private static readonly string[] CoverageSelectionUploadRequirements =
    [
        Authentication, Connection, ProjectAssociation, UploadCommand, CommandOptions,
        BuildName, ValidateVerification, IncrementalBuild, Troubleshooting,
    ];

    private static readonly string[] CoverageSelectionStreamRequirements =
    [
        Overview, Purpose, StreamCreation, Configuration, QacAssociation, Verification,
    ];

    private static readonly string[] CoverageSelectionAnalysisRequirements =
    [
        AnalysisProcedure, AnalysisCommand, AnalysisVerification,
    ];

    public static IReadOnlyList<string> Required(string question, TopicEntityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var upload = profile.Operations.Contains("Upload", StringComparer.Ordinal) ||
            profile.Features.Contains("Build upload", StringComparer.OrdinalIgnoreCase) ||
            (ContainsAny(question, "Validate", "Validateへ") && ContainsAny(question, "upload", "アップロード"));
        if (upload)
        {
            return UploadRequirements;
        }

        if (profile.Features.Contains("Stream", StringComparer.OrdinalIgnoreCase))
        {
            return StreamRequirements;
        }

        return [];
    }

    public static IReadOnlySet<string> Observe(string? text)
    {
        var value = text ?? string.Empty;
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var hasValidate = ContainsAny(value, "validate");
        var hasQac = ContainsAny(value, "qac", "qacli", "perforce qac");
        var hasStream = ContainsAny(value, "stream", "ストリーム");

        if (CommandRegex().IsMatch(value)) observed.Add(Command);
        if (OptionRegex().IsMatch(value) || ContainsAny(value, "オプション", "option", "parameter"))
        {
            observed.Add(Options);
            observed.Add("Option");
        }
        if (ContainsAny(value, "qacli auth", "認証", "ログイン", "credential", "token", "authentication")) observed.Add(Authentication);
        if (ContainsAny(value, "validate connect", "接続", "connection", "connect", "server url", "サーバーurl"))
        {
            observed.Add(Connection);
            observed.Add("Association");
        }
        if (hasValidate && ContainsAny(value, "validate build", "validate ibuild", "アップロード", "upload", "build"))
        {
            observed.Add(UploadProcedure);
            observed.Add("Execution");
        }
        if (hasValidate && ContainsAny(value, "確認", "verify", "verification", "portal", "build一覧", "表示"))
        {
            observed.Add(ValidateVerification);
            observed.Add("Verification");
        }
        if (ContainsAny(value, "失敗", "エラー", "原因", "対処", "ログ", "troubleshoot", "failure", "failed", "error", "check")) observed.Add(Troubleshooting);

        if (hasStream && ContainsAny(value, "概要", "とは", "overview", "definition")) observed.Add(StreamOverview);
        if (hasStream && ContainsAny(value, "用途", "目的", "利用", "purpose", "use case")) observed.Add(Purpose);
        if (hasStream && ContainsAny(value, "作成", "create", "creation", "new stream")) observed.Add(StreamCreation);
        if (hasStream && hasQac && ContainsAny(value, "関連付け", "紐付け", "associate", "association", "link")) observed.Add(QacAssociation);
        if (hasStream && ContainsAny(value, "設定", "構成", "configure", "configuration", "setup")) observed.Add(Configuration);
        if (hasStream && ContainsAny(value, "確認", "検証", "verify", "verification", "check")) observed.Add(Verification);
        return observed;
    }

    public static IReadOnlySet<string> Observe(IEnumerable<AnswerQualityEvidence> evidence)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            observed.UnionWith(Observe(item.Text));
        }
        return observed;
    }

    public static IReadOnlyList<string> RequiredForCoverageSelection(
        string question,
        TopicEntityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var upload = profile.Operations.Contains("Upload", StringComparer.Ordinal) ||
            profile.Features.Contains("Build upload", StringComparer.OrdinalIgnoreCase) ||
            (ContainsAny(question, "Validate", "Validateへ") &&
             ContainsAny(question, "upload", "アップロード", "build", "登録"));
        if (upload)
        {
            return CoverageSelectionUploadRequirements;
        }

        if (profile.Features.Contains("Stream", StringComparer.OrdinalIgnoreCase) ||
            ContainsAny(question, "Validate Stream", "Validateストリーム", "ストリーム"))
        {
            var required = new List<string>();
            var asksForOverview = ContainsAny(
                question,
                "overview", "purpose", "what is", "function",
                "概要", "目的", "どのような機能", "機能について", "とは");
            var asksForConfiguration = ContainsAny(
                question,
                "configuration", "configure", "setup", "setting", "how to",
                "設定", "構成", "方法", "手順");
            var asksForCreation = ContainsAny(question, "create", "creation", "作成", "生成");
            var asksForAssociation = ContainsAny(
                question,
                "association", "associate", "link", "mapping",
                "関連付け", "紐付け", "連携");
            var asksForVerification = ContainsAny(
                question,
                "verification", "verify", "check", "confirm",
                "確認", "検証");

            if (asksForOverview)
            {
                required.Add(Overview);
                required.Add(Purpose);
            }
            if (asksForConfiguration)
            {
                required.Add(Configuration);
            }
            if (asksForCreation)
            {
                required.Add(StreamCreation);
            }
            if (asksForAssociation)
            {
                required.Add(QacAssociation);
            }
            if (asksForVerification)
            {
                required.Add(Verification);
            }

            return required.Count > 0
                ? required.Distinct(StringComparer.Ordinal).ToList()
                : CoverageSelectionStreamRequirements;
        }

        if (profile.Operations.Contains("Analysis", StringComparer.Ordinal) &&
            profile.Intents.Contains("HowTo", StringComparer.Ordinal))
        {
            return CoverageSelectionAnalysisRequirements;
        }

        return [];
    }

    public static IReadOnlySet<string> ObserveForCoverageSelection(string? text)
    {
        var value = text ?? string.Empty;
        var legacy = Observe(value);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var hasValidate = ContainsAny(value, "validate");
        var hasStream = ContainsAny(value, "stream", "ストリーム");

        if (legacy.Contains(Authentication)) observed.Add(Authentication);
        if (legacy.Contains(Connection)) observed.Add(Connection);
        if (hasValidate && ContainsAny(
            value,
            "project association", "associate project", "project mapping",
            "プロジェクト関連付け", "プロジェクトを関連付け", "プロジェクト紐付け"))
        {
            observed.Add(ProjectAssociation);
        }
        if (legacy.Contains(Command) || legacy.Contains(UploadProcedure)) observed.Add(UploadCommand);
        if (legacy.Contains(Options)) observed.Add(CommandOptions);
        if (ContainsAny(value, "--build-name", "build name", "build-name", "ビルド名")) observed.Add(BuildName);
        if (legacy.Contains(ValidateVerification)) observed.Add(ValidateVerification);
        if (ContainsAny(value, "validate ibuild", "incremental build", "incremental-build", "増分ビルド", "差分ビルド"))
        {
            observed.Add(IncrementalBuild);
        }
        if (legacy.Contains(Troubleshooting)) observed.Add(Troubleshooting);

        var describesStreamFunction = hasStream && ContainsAny(
            value,
            "overview", "definition", "purpose", "function", "tracking", "track",
            "概要", "目的", "機能", "とは", "履歴", "追跡");
        if (describesStreamFunction || legacy.Contains(StreamOverview)) observed.Add(Overview);
        if (describesStreamFunction || legacy.Contains(Purpose)) observed.Add(Purpose);
        if (hasStream && legacy.Contains(StreamCreation)) observed.Add(StreamCreation);
        if (hasStream && (legacy.Contains(Configuration) || ContainsAny(
            value,
            "configuration", "configure", "setup", "setting", "create",
            "設定", "構成", "手順", "作成", "生成")))
        {
            observed.Add(Configuration);
        }
        if (hasStream && legacy.Contains(QacAssociation)) observed.Add(QacAssociation);
        if (hasStream && legacy.Contains(Verification)) observed.Add(Verification);

        var hasAnalysisOperation = ContainsAny(
            value,
            "qacli analyze", "qaclianalyze", "プロジェクトを解析", "解析を実行", "解析の実行",
            "解析する", "analyze project", "run analysis", "execute analysis");
        if (hasAnalysisOperation)
        {
            observed.Add(AnalysisProcedure);
            if (ContainsAny(value, "qacli analyze", "qaclianalyze", "command", "コマンド", "--", "-P", "-cf"))
            {
                observed.Add(AnalysisCommand);
            }

            if (ContainsAny(value, "結果", "確認", "レポート", "status", "result", "report", "verify", "check"))
            {
                observed.Add(AnalysisVerification);
            }
        }
        return observed;
    }

    private static bool ContainsAny(string? value, params string[] terms) =>
        terms.Any(term => (value ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<![A-Za-z0-9_])qacli(?:\s+[A-Za-z0-9_.+/-]+){1,4}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])--[A-Za-z0-9][A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex OptionRegex();
}
