using System.Collections.Generic;
using System.Text;

namespace SupportCaseManager.Core.Compatibility;

public static class EncodingPolicy
{
    public const string LineEnding = "\r\n";

    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static IReadOnlyList<Encoding> NoteReadEncodings { get; }

    static EncodingPolicy()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        NoteReadEncodings = new List<Encoding>
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
            Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        };
    }

    public static string DecodeNoteText(byte[] data)
    {
        foreach (var encoding in NoteReadEncodings)
        {
            try
            {
                var text = encoding.GetString(data);
                return TrimUtf8Bom(text);
            }
            catch (DecoderFallbackException)
            {
                // try next encoding
            }
        }

        return Utf8NoBom.GetString(data);
    }

    private static string TrimUtf8Bom(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return text[0] == '\uFEFF' ? text[1..] : text;
    }
}
