using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Search;

public static class OfficialDocDiagnosticsBuilder
{
    public static string Build(
        ProductKnowledgeSettings? product,
        string aiIndexFolder,
        InquiryFocus? inquiryFocus,
        IReadOnlyList<SearchSource> searchResults,
        IReadOnlyList<SearchSource> selectedSources,
        IReadOnlyList<SearchSource> willSendSources)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("OfficialDoc診断");
        builder.AppendLine($"選択製品: {ValueOrUnset(product?.ProductName)}");
        builder.AppendLine($"DocumentUrls登録数: {product?.DocumentUrls.Count ?? 0}");

        var indexPath = product is null || string.IsNullOrWhiteSpace(aiIndexFolder)
            ? "(未設定)"
            : Path.Combine(
                ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, product.ProductName),
                AiOfficialDocumentIndexBuilder.IndexFileName);
        builder.AppendLine($"OfficialDoc index path: {indexPath}");
        var indexExists = indexPath != "(未設定)" && File.Exists(indexPath);
        builder.AppendLine($"OfficialDoc index exists: {indexExists.ToString().ToLowerInvariant()}");

        var indexDocument = indexExists ? ReadIndexDocument(indexPath) : null;
        var chunkCount = indexDocument?.Documents.Count ?? 0;
        builder.AppendLine($"OfficialDoc chunks: {chunkCount}");
        builder.AppendLine($"Seed URL count: {indexDocument?.SeedUrlCount ?? product?.DocumentUrls.Count ?? 0}");
        builder.AppendLine($"Discovered URL count: {indexDocument?.DiscoveredUrlCount ?? 0}");
        builder.AppendLine($"OfficialDoc pages fetched: {indexDocument?.FetchSuccessCount ?? 0}");
        builder.AppendLine($"OfficialDoc fetch failures: {indexDocument?.FetchFailureCount ?? 0}");
        builder.AppendLine($"OfficialDoc skipped URLs: {indexDocument?.SkippedUrlCount ?? 0}");
        builder.AppendLine($"MaxDepth: {indexDocument?.MaxDepth ?? 0}");
        builder.AppendLine($"MaxPages: {indexDocument?.MaxPages ?? 0}");
        builder.AppendLine($"Last crawl time: {(indexDocument is null ? "(未設定)" : indexDocument.BuiltAt.ToString("O"))}");

        var officialSearchResults = searchResults
            .Where(static source => string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var officialSelected = selectedSources
            .Where(static source => string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var officialWillSend = willSendSources
            .Where(static source => string.Equals(source.SourceType, "OfficialDoc", StringComparison.OrdinalIgnoreCase))
            .ToList();

        builder.AppendLine($"OfficialDoc search results: {officialSearchResults.Count}");
        builder.AppendLine($"OfficialDoc selected: {officialSelected.Count}");
        builder.AppendLine($"OfficialDoc will send: {officialWillSend.Count}");
        builder.AppendLine($"FreshnessSensitive: {(inquiryFocus?.IsFreshnessSensitive == true).ToString().ToLowerInvariant()}");
        builder.AppendLine($"Freshness reason: {ValueOrUnset(inquiryFocus?.FreshnessReason)}");
        builder.AppendLine($"Detected target version: {FormatList(inquiryFocus?.TargetVersions)}");
        builder.AppendLine($"OfficialDoc version matches: {CountVersionMatches(officialSearchResults, inquiryFocus)}");
        var importantPageUrls = indexDocument?.ImportantPageUrls ?? [];
        if (importantPageUrls.Count > 0)
        {
            builder.AppendLine("Important pages:");
            foreach (var url in importantPageUrls.Take(10))
            {
                builder.AppendLine($"- {url}");
            }
        }

        var failedUrls = indexDocument?.FailedUrls ?? [];
        if (failedUrls.Count > 0)
        {
            builder.AppendLine("Failed URLs:");
            foreach (var url in failedUrls.Take(10))
            {
                builder.AppendLine($"- {url}");
            }
        }

        if (officialWillSend.Count == 0)
        {
            builder.AppendLine("OfficialDocが使われていません。");
            builder.AppendLine("理由:");
            AppendUnusedReasons(builder, product, indexExists, chunkCount, officialSearchResults, inquiryFocus);
        }

        return builder.ToString();
    }

    private static void AppendUnusedReasons(
        System.Text.StringBuilder builder,
        ProductKnowledgeSettings? product,
        bool indexExists,
        int chunkCount,
        IReadOnlyList<SearchSource> officialSearchResults,
        InquiryFocus? inquiryFocus)
    {
        if (product is null)
        {
            builder.AppendLine("- 製品別設定が未選択です");
        }

        if (product?.DocumentUrls.Count is null or 0)
        {
            builder.AppendLine("- DocumentUrls が未登録です");
        }

        if (!indexExists)
        {
            builder.AppendLine("- official-docs-index.json が存在しません");
            builder.AppendLine("- 公式URLインデックスが未作成です");
        }
        else if (chunkCount == 0)
        {
            builder.AppendLine("- 公式URLインデックスにチャンクがありません");
            builder.AppendLine("- HTML本文抽出に失敗した可能性があります");
        }

        if (indexExists && chunkCount > 0 && officialSearchResults.Count == 0)
        {
            builder.AppendLine("- 検索スコアが閾値未満です");
        }

        if (inquiryFocus?.TargetVersions.Count > 0 &&
            CountVersionMatches(officialSearchResults, inquiryFocus) == 0)
        {
            builder.AppendLine("- 対象バージョンの公式根拠が見つかりません");
        }

        if (officialSearchResults.Count > 0 && inquiryFocus?.IsFreshnessSensitive == true)
        {
            builder.AppendLine("- OfficialDocは検索結果にありますが、自動選択またはLLM送信対象外です");
        }
    }

    private static AiOfficialDocumentIndexDocument? ReadIndexDocument(string indexFilePath)
    {
        try
        {
            using var stream = File.OpenRead(indexFilePath);
            return System.Text.Json.JsonSerializer.Deserialize<AiOfficialDocumentIndexDocument>(
                stream,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static string ValueOrUnset(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(未設定)" : value.Trim();
    }

    private static string FormatList(IReadOnlyList<string>? values)
    {
        return values is null || values.Count == 0
            ? "(未検出)"
            : string.Join(", ", values);
    }

    private static int CountVersionMatches(IReadOnlyList<SearchSource> officialSearchResults, InquiryFocus? inquiryFocus)
    {
        if (inquiryFocus?.TargetVersions.Count is null or 0)
        {
            return 0;
        }

        return officialSearchResults.Count(source =>
            inquiryFocus.TargetVersions.Any(version =>
                source.Title.Contains(version, StringComparison.OrdinalIgnoreCase) ||
                source.Text.Contains(version, StringComparison.OrdinalIgnoreCase) ||
                (source.Url?.Contains(version, StringComparison.OrdinalIgnoreCase) == true) ||
                source.MatchedTerms.Any(term => string.Equals(term, version, StringComparison.OrdinalIgnoreCase))));
    }
}
