using System.Globalization;
using SupportCaseManager.Core.Config;
using SupportCaseManager.Core.Logging;

namespace SupportCaseManager.App.Outlook;

public sealed class OutlookAcceptanceMonitor
{
    private readonly IOutlookAcceptanceGateway gateway;
    private readonly ConfigStore config;
    private readonly UserSettings settings;
    private readonly IAppLogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    public OutlookAcceptanceMonitor(ConfigStore config, UserSettings settings,
        IOutlookAcceptanceGateway? gateway = null, IAppLogger? logger = null)
    {
        this.config = config;
        this.settings = settings;
        this.logger = logger ?? NullLogger.Instance;
        this.gateway = gateway ?? new OutlookAcceptanceGateway(logger: this.logger);
    }

    public void Enable(DateTime nowUtc)
    {
        if (ReadUtc(settings.AcceptanceDetectionEnabledAtUtc) is not null) return;
        settings.AcceptanceDetectionEnabledAtUtc = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        config.Save(settings);
    }

    public async Task<IReadOnlyList<OutlookAcceptanceCandidate>> ScanAsync(DateTime nowUtc, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var enabled = ReadUtc(settings.AcceptanceDetectionEnabledAtUtc)
                ?? throw new InvalidOperationException("受付メール検知が有効化されていません。");
            var last = ReadUtc(settings.AcceptanceLastSuccessfulScanUtc);
            var since = enabled > nowUtc.AddDays(-30) ? enabled : nowUtc.AddDays(-30);
            if (last.HasValue && last.Value.AddMinutes(-30) > since) since = last.Value.AddMinutes(-30);
            logger.Info($"ACCEPT_SCAN_START={nowUtc:O}");
            logger.Info($"ACCEPT_SCAN_WINDOW={since:O}..{nowUtc:O}");
            var found = await OutlookCaseStatusService.RunSta(() => gateway.Scan(since, token), token);
            token.ThrowIfCancellationRequested();
            var pending = FilterUnprocessed(found);
            foreach (var candidate in pending)
                logger.Info($"ACCEPT_QUEUE_ENQUEUED={candidate.SupportId}");
            logger.Info($"ACCEPT_PENDING_COUNT={pending.Count}");
            return pending;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<OutlookAcceptanceCandidate>> DryRunAsync(DateTime sinceUtc, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return await OutlookCaseStatusService.RunSta(() => gateway.Scan(sinceUtc, token), token); }
        finally { gate.Release(); }
    }

    public void CompleteScan(DateTime scannedAtUtc)
    {
        settings.AcceptanceLastSuccessfulScanUtc = scannedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        config.Save(settings);
    }

    public Task<bool> RevalidateAsync(OutlookAcceptanceCandidate candidate, CancellationToken token) =>
        OutlookCaseStatusService.RunSta(() => gateway.Revalidate(candidate), token);

    public void MarkProcessed(OutlookAcceptanceCandidate candidate, bool rejected)
    {
        var keys = rejected ? settings.AcceptanceRejectedMailKeys : settings.AcceptanceProcessedMailKeys;
        if (!keys.Contains(candidate.MailKey, StringComparer.Ordinal)) keys.Add(candidate.MailKey);
        if (!rejected && !settings.AcceptanceProcessedBodyHashes.Contains(candidate.BodyHash, StringComparer.Ordinal))
            settings.AcceptanceProcessedBodyHashes.Add(candidate.BodyHash);
        config.Save(settings);
    }

    public (DateTime? EnabledAt, DateTime? LastScan, int Processed, int Rejected) Diagnostics =>
        (ReadUtc(settings.AcceptanceDetectionEnabledAtUtc), ReadUtc(settings.AcceptanceLastSuccessfulScanUtc),
            settings.AcceptanceProcessedMailKeys.Count, settings.AcceptanceRejectedMailKeys.Count);

    private IReadOnlyList<OutlookAcceptanceCandidate> FilterUnprocessed(IEnumerable<OutlookAcceptanceCandidate> found)
    {
        var handled = settings.AcceptanceProcessedMailKeys.Concat(settings.AcceptanceRejectedMailKeys)
            .ToHashSet(StringComparer.Ordinal);
        var bodies = settings.AcceptanceProcessedBodyHashes.ToHashSet(StringComparer.Ordinal);
        foreach (var item in found.Where(item => handled.Contains(item.MailKey) || bodies.Contains(item.BodyHash)))
            logger.Info($"ACCEPT_ALREADY_PROCESSED={item.SupportId}");
        return found.Where(item => !handled.Contains(item.MailKey) && !bodies.Contains(item.BodyHash))
            .GroupBy(static item => item.MailKey)
            .Select(static group => group.First()).ToArray();
    }

    private static DateTime? ReadUtc(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime() : null;
}
