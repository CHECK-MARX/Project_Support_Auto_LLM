using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed partial class AiOfficialDocumentIndexBuilder : IAiOfficialDocumentIndexBuilder
{
    public const string IndexFileName = "official-docs-index.json";

    private const int ChunkMaxLength = 2600;
    private const int ChunkOverlapLength = 150;
    private const int MinimumUsefulTextLength = 80;
    private const int DefaultMaxDepth = 2;
    private const int DefaultMaxPages = 100;
    private const int DefaultRequestDelayMs = 300;
    private const int DefaultFetchTimeoutSeconds = 30;

    private static readonly string[] SkippedExtensions =
    [
        ".pdf",
        ".doc",
        ".docx",
        ".zip",
        ".js",
        ".css",
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".svg",
        ".ico",
    ];

    private static readonly string[] ImportantPageKeywords =
    [
        "release notes",
        "release-notes",
        "hotfix",
        "hotfixes",
        "hf",
        "engine pack",
        "engine-pack",
        "version",
        "versions",
        "sast",
        "cxsast",
        "最新",
        "最新バージョン",
        "リリース",
        "ホットフィックス",
        "エンジンパック",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly Func<HttpClient> httpClientFactory;
    private readonly Func<DateTimeOffset> nowProvider;

    public AiOfficialDocumentIndexBuilder(
        HttpMessageHandler? httpMessageHandler = null,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
        httpClientFactory = httpMessageHandler is null
            ? static () => new HttpClient()
            : () => new HttpClient(httpMessageHandler, disposeHandler: false);
    }

    public async Task<AiOfficialDocumentIndexBuildResult> BuildAsync(
        ProductKnowledgeSettings product,
        string indexFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (string.IsNullOrWhiteSpace(indexFolder))
        {
            throw new ArgumentException("AI index folder is required.", nameof(indexFolder));
        }

        var sourceUrls = product.DocumentUrls
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .Select(static url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(indexFolder, product.ProductName);
        Directory.CreateDirectory(productIndexFolder);
        var indexFilePath = Path.Combine(productIndexFolder, IndexFileName);
        var now = nowProvider();
        var warnings = new List<string>();
        var documents = new List<AiIndexedOfficialDocument>();
        var retrievedUrls = new List<string>();
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        var successCount = 0;
        var failureCount = 0;
        var skippedCount = 0;
        var discoveredUrls = new List<string>();
        var importantPageUrls = new List<string>();
        var failedUrls = new List<string>();

        using var client = httpClientFactory();
        client.Timeout = Timeout.InfiniteTimeSpan;

        var crawl = await CrawlAsync(client, sourceUrls, warnings, cancellationToken);
        successCount = crawl.SuccessCount;
        failureCount = crawl.FailureCount;
        skippedCount = crawl.SkippedCount;
        retrievedUrls = crawl.RetrievedUrls.ToList();
        discoveredUrls = crawl.DiscoveredUrls.ToList();
        importantPageUrls = crawl.ImportantPageUrls.ToList();
        failedUrls = crawl.FailedUrls.ToList();

        foreach (var page in crawl.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var extracted = ExtractHtmlText(page.Html);
                if (extracted.Text.Length < MinimumUsefulTextLength)
                {
                    warnings.Add($"Official URL text is short and may be unusable: {page.Url}. Length={extracted.Text.Length}");
                }

                var contentHash = BuildHash(extracted.Text);
                if (!seenHashes.Add(contentHash))
                {
                    warnings.Add($"Skipped duplicated official URL body: {page.Url}");
                    continue;
                }

                var chunkIndex = 0;
                foreach (var chunk in SplitIntoChunks(extracted.Text))
                {
                    var chunkHash = BuildHash($"{page.Url}|{chunkIndex}|{chunk}");
                    documents.Add(new AiIndexedOfficialDocument
                    {
                        Id = $"official:{chunkHash[..24]}",
                        ProductName = product.ProductName,
                        Url = page.Url,
                        Title = string.IsNullOrWhiteSpace(extracted.Title) ? page.Uri.Host : extracted.Title,
                        SectionTitle = extracted.SectionTitle,
                        Text = chunk,
                        RetrievedAt = now,
                        ContentHash = chunkHash,
                    });
                    chunkIndex++;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                failureCount++;
                failedUrls.Add($"{page.Url}: {ex.GetType().Name}: {ex.Message}");
                warnings.Add($"Failed to index official URL: {page.Url}. {ex.GetType().Name}: {ex.Message}");
            }
        }

        var document = new AiOfficialDocumentIndexDocument
        {
            ProductName = product.ProductName,
            BuiltAt = now,
            Documents = documents,
            SourceUrls = sourceUrls,
            DiscoveredUrls = discoveredUrls,
            RetrievedUrls = retrievedUrls,
            SeedUrlCount = sourceUrls.Count,
            DiscoveredUrlCount = discoveredUrls.Count,
            FetchSuccessCount = successCount,
            FetchFailureCount = failureCount,
            SkippedUrlCount = skippedCount,
            MaxDepth = DefaultMaxDepth,
            MaxPages = DefaultMaxPages,
            RequestDelayMs = DefaultRequestDelayMs,
            FetchTimeoutSeconds = DefaultFetchTimeoutSeconds,
            ImportantPageUrls = importantPageUrls,
            FailedUrls = failedUrls,
            Warnings = warnings,
        };

        await using (var stream = File.Create(indexFilePath))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        }

        var candidateFacts = new OfficialDocumentFactExtractor().Extract(document);
        var factCatalog = FactCatalogStore.BuildCatalog(product.ProductName, candidateFacts, now);
        await FactCatalogStore.SaveAsync(productIndexFolder, factCatalog, cancellationToken);

        if (documents.Count == 0 && sourceUrls.Count > 0)
        {
            warnings.Add("ERROR: No chunks were indexed from registered official URLs. Check HTML extraction and URL accessibility.");
        }

        return new AiOfficialDocumentIndexBuildResult
        {
            ProductName = product.ProductName,
            IndexFilePath = indexFilePath,
            SourceUrlCount = sourceUrls.Count,
            DiscoveredUrlCount = discoveredUrls.Count,
            FetchSuccessCount = successCount,
            FetchFailureCount = failureCount,
            SkippedUrlCount = skippedCount,
            IndexedChunkCount = documents.Count,
            MaxDepth = DefaultMaxDepth,
            MaxPages = DefaultMaxPages,
            RequestDelayMs = DefaultRequestDelayMs,
            FetchTimeoutSeconds = DefaultFetchTimeoutSeconds,
            SourceUrls = sourceUrls,
            RetrievedUrls = retrievedUrls,
            DiscoveredUrls = discoveredUrls,
            ImportantPageUrls = importantPageUrls,
            FailedUrls = failedUrls,
            Warnings = warnings,
        };
    }

    private static async Task<CrawlResult> CrawlAsync(
        HttpClient client,
        IReadOnlyList<string> seedUrls,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var queue = new List<CrawlCandidate>();
        var queuedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discoveredUrls = new List<string>();
        var retrievedUrls = new List<string>();
        var importantPageUrls = new List<string>();
        var failedUrls = new List<string>();
        var pages = new List<CrawledPage>();
        var skippedCount = 0;
        var failureCount = 0;

        foreach (var seed in seedUrls)
        {
            if (!TryNormalizeHttpUrl(seed, out var seedUri, out var normalizedSeed, out var skipReason))
            {
                skippedCount++;
                warnings.Add($"Skipped seed URL: {seed}. {skipReason}");
                continue;
            }

            if (ShouldSkipUrl(seedUri, out skipReason))
            {
                skippedCount++;
                warnings.Add($"Skipped seed URL: {seed}. {skipReason}");
                continue;
            }

            if (queuedUrls.Add(normalizedSeed))
            {
                queue.Add(new CrawlCandidate(seedUri, normalizedSeed, seedUri.Host, Depth: 0, Priority: 100));
                discoveredUrls.Add(normalizedSeed);
            }
        }

        while (queue.Count > 0 && pages.Count < DefaultMaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            queue.Sort(static (left, right) =>
            {
                var priority = right.Priority.CompareTo(left.Priority);
                if (priority != 0)
                {
                    return priority;
                }

                var depth = left.Depth.CompareTo(right.Depth);
                return depth != 0
                    ? depth
                    : string.Compare(left.NormalizedUrl, right.NormalizedUrl, StringComparison.OrdinalIgnoreCase);
            });

            var candidate = queue[0];
            queue.RemoveAt(0);
            if (!visitedUrls.Add(candidate.NormalizedUrl))
            {
                continue;
            }

            if (ShouldSkipUrl(candidate.Uri, out var skipReason))
            {
                skippedCount++;
                warnings.Add($"Skipped URL: {candidate.NormalizedUrl}. {skipReason}");
                continue;
            }

            var fetched = await FetchPageAsync(client, candidate.Uri, cancellationToken);
            if (!fetched.IsSuccess)
            {
                if (fetched.IsSkipped)
                {
                    skippedCount++;
                }
                else
                {
                    failureCount++;
                    failedUrls.Add($"{candidate.NormalizedUrl}: {fetched.Message}");
                }

                warnings.Add($"{fetched.Message}: {candidate.NormalizedUrl}");
                continue;
            }

            retrievedUrls.Add(candidate.NormalizedUrl);
            pages.Add(new CrawledPage(candidate.Uri, candidate.NormalizedUrl, fetched.Html));

            if (candidate.Priority > 0 && candidate.Priority < 100)
            {
                importantPageUrls.Add(candidate.NormalizedUrl);
            }

            if (candidate.Depth < DefaultMaxDepth)
            {
                foreach (var link in ExtractSameHostLinks(fetched.Html, candidate.Uri, candidate.RootHost))
                {
                    if (visitedUrls.Contains(link.NormalizedUrl) || queuedUrls.Contains(link.NormalizedUrl))
                    {
                        continue;
                    }

                    if (discoveredUrls.Count >= DefaultMaxPages * 4)
                    {
                        skippedCount++;
                        continue;
                    }

                    queuedUrls.Add(link.NormalizedUrl);
                    discoveredUrls.Add(link.NormalizedUrl);
                    if (link.Priority > 0)
                    {
                        importantPageUrls.Add(link.NormalizedUrl);
                    }

                    queue.Add(new CrawlCandidate(
                        link.Uri,
                        link.NormalizedUrl,
                        candidate.RootHost,
                        candidate.Depth + 1,
                        link.Priority));
                }
            }

            if (queue.Count > 0 && pages.Count < DefaultMaxPages)
            {
                await Task.Delay(DefaultRequestDelayMs, cancellationToken);
            }
        }

        if (queue.Count > 0 && pages.Count >= DefaultMaxPages)
        {
            skippedCount += queue.Count;
            warnings.Add($"MaxPages reached. Remaining URLs were skipped. MaxPages={DefaultMaxPages}; Remaining={queue.Count}");
        }

        return new CrawlResult(
            pages,
            discoveredUrls.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            retrievedUrls,
            importantPageUrls.Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList(),
            failedUrls,
            SuccessCount: retrievedUrls.Count,
            FailureCount: failureCount,
            SkippedCount: skippedCount);
    }

    private static async Task<FetchPageResult> FetchPageAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(DefaultFetchTimeoutSeconds));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("SupportCaseManagerAiAssistant/1.0");
            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return FetchPageResult.Failure($"Failed to fetch official URL. Status={(int)response.StatusCode}");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(mediaType) &&
                !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase))
            {
                return FetchPageResult.Skipped($"Skipped non-HTML/text URL. ContentType={mediaType}");
            }

            var html = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return FetchPageResult.Success(html);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FetchPageResult.Failure($"Timed out after {DefaultFetchTimeoutSeconds} seconds");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return FetchPageResult.Failure($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IReadOnlyList<DiscoveredLink> ExtractSameHostLinks(string html, Uri baseUri, string rootHost)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        var links = new List<DiscoveredLink>();
        foreach (Match match in AnchorRegex().Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value.Trim());
            var text = HtmlDecode(match.Groups["text"].Value);
            if (string.IsNullOrWhiteSpace(href) ||
                href.StartsWith("#", StringComparison.Ordinal) ||
                href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(baseUri, href, out var resolved) ||
                !string.Equals(resolved.Host, rootHost, StringComparison.OrdinalIgnoreCase) ||
                !TryNormalizeHttpUrl(resolved.ToString(), out var normalizedUri, out var normalizedUrl, out _) ||
                ShouldSkipUrl(normalizedUri, out _))
            {
                continue;
            }

            links.Add(new DiscoveredLink(
                normalizedUri,
                normalizedUrl,
                CalculatePriority(normalizedUrl, text)));
        }

        return links
            .GroupBy(static link => link.NormalizedUrl, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(link => link.Priority).First())
            .OrderByDescending(static link => link.Priority)
            .ThenBy(static link => link.NormalizedUrl, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryNormalizeHttpUrl(
        string url,
        out Uri uri,
        out string normalizedUrl,
        out string reason)
    {
        uri = null!;
        normalizedUrl = string.Empty;
        reason = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            reason = "Only absolute http/https URLs are supported.";
            return false;
        }

        var builder = new UriBuilder(parsed)
        {
            Fragment = string.Empty,
        };
        uri = builder.Uri;
        normalizedUrl = uri.ToString();
        return true;
    }

    private static bool ShouldSkipUrl(Uri uri, out string reason)
    {
        var path = uri.AbsolutePath;
        if (LooksLikeLoginUrl(path))
        {
            reason = "Login/auth pages are not crawled.";
            return true;
        }

        var extension = Path.GetExtension(path);
        if (!string.IsNullOrWhiteSpace(extension) &&
            SkippedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            reason = $"Unsupported file type: {extension}";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool LooksLikeLoginUrl(string path)
    {
        return path.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("signin", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("logout", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("auth", StringComparison.OrdinalIgnoreCase);
    }

    private static int CalculatePriority(string url, string linkText)
    {
        var haystack = $"{url} {linkText}".ToLowerInvariant();
        var score = 0;
        foreach (var keyword in ImportantPageKeywords)
        {
            if (haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }
        }

        return score;
    }

    private static HtmlExtractResult ExtractHtmlText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new HtmlExtractResult(string.Empty, string.Empty, string.Empty);
        }

        var withoutIgnored = IgnoredHtmlBlockRegex().Replace(html, " ");
        var title = HtmlDecode(ReadFirstGroup(withoutIgnored, TitleRegex(), "title"));
        if (string.IsNullOrWhiteSpace(title))
        {
            title = HtmlDecode(ReadFirstGroup(withoutIgnored, OgTitleRegex(), "title"));
        }

        var contentHtml = ExtractPrimaryContentHtml(withoutIgnored);
        var heading = HtmlDecode(ReadFirstGroup(contentHtml, HeadingRegex(), "heading"));
        if (string.IsNullOrWhiteSpace(heading))
        {
            heading = HtmlDecode(ReadFirstGroup(withoutIgnored, HeadingRegex(), "heading"));
        }

        var text = HtmlTagRegex().Replace(contentHtml, " ");
        text = WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ").Trim();

        if (text.Length < MinimumUsefulTextLength)
        {
            var fallbackText = HtmlTagRegex().Replace(withoutIgnored, " ");
            fallbackText = WebUtility.HtmlDecode(fallbackText);
            fallbackText = WhitespaceRegex().Replace(fallbackText, " ").Trim();
            if (fallbackText.Length > text.Length)
            {
                text = fallbackText;
            }
        }

        text = AppendHotfixSummary(text, contentHtml);

        return new HtmlExtractResult(title, heading, text);
    }

    private static string ExtractPrimaryContentHtml(string html)
    {
        foreach (var regex in PrimaryContentRegexes())
        {
            var match = regex.Match(html);
            if (match.Success && match.Groups["content"].Value.Length >= MinimumUsefulTextLength / 2)
            {
                return match.Groups["content"].Value;
            }
        }

        return html;
    }

    private static IEnumerable<Regex> PrimaryContentRegexes()
    {
        yield return PrimaryContentRegex("article");
        yield return PrimaryContentRegex("main");
        yield return ClassContentRegex("topic-content");
        yield return ClassContentRegex("content");
        yield return ClassContentRegex("article-content");
    }

    private static Regex PrimaryContentRegex(string tagName)
    {
        return new Regex(
            $@"<{tagName}\b[^>]*>(?<content>.*?)</{tagName}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    private static Regex ClassContentRegex(string className)
    {
        return new Regex(
            $@"<div\b[^>]*class=""[^""]*\b{Regex.Escape(className)}\b[^""]*""[^>]*>(?<content>.*?)</div>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    private static string AppendHotfixSummary(string text, string contentHtml)
    {
        var hotfixMatch = HotfixEntryRegex().Match(contentHtml);
        if (!hotfixMatch.Success)
        {
            return text;
        }

        var hotfixId = hotfixMatch.Groups["hotfix"].Value.Trim();
        if (string.IsNullOrWhiteSpace(hotfixId) || text.Contains(hotfixId, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return $"{text} Latest Hotfix: {hotfixId}".Trim();
    }

    private static string ReadFirstGroup(string input, Regex regex, string groupName)
    {
        var match = regex.Match(input);
        return match.Success ? match.Groups[groupName].Value.Trim() : string.Empty;
    }

    private static string HtmlDecode(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : WebUtility.HtmlDecode(HtmlTagRegex().Replace(value, " ")).Trim();
    }

    private static IEnumerable<string> SplitIntoChunks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(ChunkMaxLength, text.Length - start);
            var chunk = text.Substring(start, length).Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                yield return chunk;
            }

            if (start + length >= text.Length)
            {
                break;
            }

            start += Math.Max(1, ChunkMaxLength - ChunkOverlapLength);
        }
    }

    private static string BuildHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [GeneratedRegex(@"<(script|style|noscript|nav|footer|header)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex IgnoredHtmlBlockRegex();

    [GeneratedRegex(@"<title\b[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<meta\b[^>]*property=""og:title""[^>]*content=""(?<title>.*?)""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex OgTitleRegex();

    [GeneratedRegex(@"<h[1-3]\b[^>]*>(?<heading>.*?)</h[1-3]>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"<a\b[^>]*href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"\b(?<hotfix>HF\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HotfixEntryRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private sealed record CrawlCandidate(Uri Uri, string NormalizedUrl, string RootHost, int Depth, int Priority);

    private sealed record DiscoveredLink(Uri Uri, string NormalizedUrl, int Priority);

    private sealed record CrawledPage(Uri Uri, string Url, string Html);

    private sealed record CrawlResult(
        IReadOnlyList<CrawledPage> Pages,
        IReadOnlyList<string> DiscoveredUrls,
        IReadOnlyList<string> RetrievedUrls,
        IReadOnlyList<string> ImportantPageUrls,
        IReadOnlyList<string> FailedUrls,
        int SuccessCount,
        int FailureCount,
        int SkippedCount);

    private sealed record FetchPageResult(bool IsSuccess, bool IsSkipped, string Html, string Message)
    {
        public static FetchPageResult Success(string html) => new(true, false, html, string.Empty);

        public static FetchPageResult Failure(string message) => new(false, false, string.Empty, message);

        public static FetchPageResult Skipped(string message) => new(false, true, string.Empty, message);
    }

    private sealed record HtmlExtractResult(string Title, string SectionTitle, string Text);
}
