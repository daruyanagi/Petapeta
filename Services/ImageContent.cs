using System;

namespace Petapeta.Services;

/// <summary>
/// クリップボード由来の画像データ・HTML・URL の解析(#23)。
/// テストプロジェクトからソースリンクで検証するため、WinRT / WinUI に依存させない。
/// </summary>
internal static class ImageContent
{
    /// <summary>先頭バイトから画像形式を判別する。判別できなければ null(PNG へ再エンコード)。</summary>
    internal static string? SniffImageExtension(byte[] data)
    {
        if (data.Length < 12)
        {
            return null;
        }
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return ".png";
        if (data[0] == 0xFF && data[1] == 0xD8) return ".jpg";
        if (data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'8') return ".gif";
        if (data[0] == (byte)'B' && data[1] == (byte)'M') return ".bmp";
        if (data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P') return ".webp";
        return null;
    }

    /// <summary>CF_HTML 文字列から最初の &lt;img src&gt; を絶対 http(s) URL として取り出す(#43)。</summary>
    internal static string? ExtractImageUrlFromHtml(string cfHtml)
    {
        // CF_HTML ヘッダーの SourceURL(コピー元ページ)を相対 URL の解決に使う
        Uri? baseUri = null;
        var source = System.Text.RegularExpressions.Regex.Match(
            cfHtml, @"^SourceURL:(\S+)", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (source.Success)
        {
            Uri.TryCreate(source.Groups[1].Value, UriKind.Absolute, out baseUri);
        }

        var img = System.Text.RegularExpressions.Regex.Match(
            cfHtml, @"<img\b[^>]*?\bsrc\s*=\s*(?:""([^""]+)""|'([^']+)'|([^\s>]+))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!img.Success)
        {
            return null;
        }

        var src = System.Net.WebUtility.HtmlDecode(
            img.Groups[1].Success ? img.Groups[1].Value
            : img.Groups[2].Success ? img.Groups[2].Value
            : img.Groups[3].Value);

        if (!Uri.TryCreate(src, UriKind.Absolute, out var uri)
            && (baseUri is null || !Uri.TryCreate(baseUri, src, out uri)))
        {
            return null;
        }

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.AbsoluteUri.Length <= 2048
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>
    /// 単一行・2048 文字以内の絶対 http(s) URL なら Uri を返す(#7/#18 の判定部)。
    /// それ以外(改行・空白入り、相対、他スキーム)は null。
    /// </summary>
    internal static Uri? TryParseHttpUrl(string text)
    {
        var candidate = text.Trim();
        if (candidate.Length > 2048
            || candidate.IndexOfAny(new[] { '\r', '\n', ' ' }) >= 0
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }
        return uri;
    }
}
