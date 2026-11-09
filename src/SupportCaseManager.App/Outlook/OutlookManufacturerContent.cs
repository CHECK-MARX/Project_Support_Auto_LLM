using System.Text.RegularExpressions;

namespace SupportCaseManager.App.Outlook;

public enum OutlookManufacturerMessageKind { Unclassified, ProgressOnly, ActionRequested }

internal static class OutlookManufacturerContent
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex Escalation = new(
        @"^(?:Please be informed that\s+)?(?:(?:I|We) (?:am |are |have |have been )?(?:forwarding|escalating|forwarded|escalated)|(?:Your|The|This) (?:case|ticket|issue|request) (?:has been|was|is being) (?:forwarded|escalated))\s*(?:(?:the|this|your) (?:case|ticket|issue|request)\s+)?to (?:the |our )?(?<team>[a-z][a-z0-9 &/-]{0,50}?(?:team|department|engineers))(?: (?:in order to |to |for )(?:assist you further|investigate (?:the details|this issue)|further investigation|investigate (?:the details|this issue) and assist you further))?[.!]?$",
        Options);
    private static readonly Regex JapaneseEscalation = new(
        @"^(?:(?:本件|この件|お問い合わせ|本チケット)を)?(?<team>(?:担当|専門|開発|技術|上位)(?:部門|チーム)|[A-Za-z][A-Za-z0-9 -]{0,30}チーム)(?:へ|に)(?:引き継ぎました|引き継いでおります|エスカレーションしました|エスカレーションしております)[。！]?$", Options);

    internal static (OutlookManufacturerMessageKind Kind, string Summary) Classify(string body)
    {
        // Only an entirely understood authored message can lose the review alert.
        // Never decide from a quoted earlier message or from a truncated prefix.
        var authored = new List<string>();
        var includingSignature = new List<string>();
        var afterSignature = new List<string>();
        var signatureStarted = false;
        var length = 0;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (Regex.IsMatch(line, @"^(?:>|From\s*:|差出人\s*[:：]|-{2,}\s*(?:Original Message|Forwarded|元のメッセージ)|On .+wrote:|.+さんは書きました[:：])", Options)) break;
            length += line.Length;
            if (length > 12000) return (OutlookManufacturerMessageKind.Unclassified, string.Empty);
            if (line.Length == 0) continue;
            includingSignature.Add(line);
            if (Regex.IsMatch(line, @"^(?:Thanks and Regards|Best regards|Kind regards|Regards|Sincerely|敬具|以上、よろしくお願いいたします)[,。\s]*$", Options))
            { signatureStarted = true; continue; }
            if (signatureStarted) afterSignature.Add(line);
            else authored.Add(line);
        }
        // Query strings in documentation/tracking URLs are not questions. Keep
        // the surrounding prose so real requests accompanying a URL still alert.
        var text = Regex.Replace(string.Join('\n', includingSignature), @"<https?://[^>]+>|https?://[^\s<>]+", "", Options);
        if (Regex.IsMatch(text, @"[?？]|\b(?:please|kindly)\s+(?:provide|send|share|confirm|check|try|run|upload|review|install|update|reply|respond)|\b(?:could|can|would) you\b|\bwe (?:need|require)\b|\byou (?:need to|must|should)\b|(?:ご提供|ご送付|ご確認|ご対応|ご回答|ご返信).*(?:ください|下さい|お願い)|(?:ログ|情報|資料).*(?:送付|提供).*(?:ください|お願い)", Options))
            return (OutlookManufacturerMessageKind.ActionRequested, "追加依頼あり・要確認");
        if (afterSignature.Any(line => Regex.IsMatch(line, @"^(?:P\.?S\.?\s*[:：]|追伸|追記|追加|なお|Note\s*[:：])", Options)))
            return (OutlookManufacturerMessageKind.Unclassified, string.Empty);

        string? team = null;
        var investigating = false;
        foreach (var line in authored)
        {
            if (Regex.IsMatch(line, @"^Click here to (?:see your request|view your case)(?:\s*<https?://[^>]+>|\s+https?://\S+)?[.\s]*$", Options)) continue;
            foreach (var sentence in Regex.Split(line, @"(?<=[.!?。！？])\s+|(?<=[。！？])"))
            {
                var value = sentence.Trim();
                if (value.Length == 0) continue;
                var escalation = Escalation.Match(value);
                if (!escalation.Success) escalation = JapaneseEscalation.Match(value);
                if (escalation.Success) { team = escalation.Groups["team"].Value; continue; }
                if (Regex.IsMatch(value, @"^(?:We are (?:still )?(?:investigating|looking into) (?:this|the|your) (?:issue|case|request)|現在[、,]?(?:引き続き)?調査中です)[.。]?$", Options))
                { investigating = true; continue; }
                if (Regex.IsMatch(value, @"^(?:(?:Hi|Hello|Dear) [\p{L} .'-]{1,60},?|Thank you for contacting [\p{L} -]{1,60} support team[.!]?|(?:Thank you for|We appreciate) your patience[.!]?|We appreciate your patience and (?:are )?happy to assist you further[.!]?|We will (?:keep you updated|update you (?:once|as soon as) we (?:have|receive) (?:an update|more information))[.!]?|進展があり次第ご連絡(?:します|いたします)[。]?|お待ちいただきありがとうございます[。]?)$", Options)) continue;
                // Unknown content can be a technical answer, a condition, a request,
                // or a deadline. Keep the ordinary review-required classification.
                return (OutlookManufacturerMessageKind.Unclassified, string.Empty);
            }
        }
        return team is not null
            ? (OutlookManufacturerMessageKind.ProgressOnly, $"メーカー内引継ぎ: {team}（進捗連絡・回答待ち）")
            : investigating ? (OutlookManufacturerMessageKind.ProgressOnly, "メーカー調査中（進捗連絡・回答待ち）")
            : (OutlookManufacturerMessageKind.Unclassified, string.Empty);
    }
}
