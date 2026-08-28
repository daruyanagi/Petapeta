using Petapeta.Services;
using Xunit;

namespace Petapeta.Tests;

public class ImageContentTests
{
    private static byte[] WithHeader(byte[] header)
    {
        var data = new byte[16];
        header.CopyTo(data, 0);
        return data;
    }

    [Fact]
    public void Sniff_Png() =>
        Assert.Equal(".png", ImageContent.SniffImageExtension(
            WithHeader(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })));

    [Fact]
    public void Sniff_Jpeg() =>
        Assert.Equal(".jpg", ImageContent.SniffImageExtension(
            WithHeader(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })));

    [Fact]
    public void Sniff_Gif() =>
        Assert.Equal(".gif", ImageContent.SniffImageExtension(
            WithHeader("GIF89a"u8.ToArray())));

    [Fact]
    public void Sniff_Bmp() =>
        Assert.Equal(".bmp", ImageContent.SniffImageExtension(
            WithHeader("BM"u8.ToArray())));

    [Fact]
    public void Sniff_Webp()
    {
        var data = new byte[16];
        "RIFF"u8.CopyTo(data);
        "WEBP"u8.CopyTo(data.AsSpan(8));
        Assert.Equal(".webp", ImageContent.SniffImageExtension(data));
    }

    [Fact]
    public void Sniff_Riff_WithoutWebp_IsUnknown()
    {
        var data = new byte[16];
        "RIFF"u8.CopyTo(data);
        "WAVE"u8.CopyTo(data.AsSpan(8));
        Assert.Null(ImageContent.SniffImageExtension(data));
    }

    [Fact]
    public void Sniff_TooShort_IsUnknown() =>
        Assert.Null(ImageContent.SniffImageExtension(new byte[] { 0x89, 0x50 }));

    [Fact]
    public void Sniff_Unknown_IsNull() =>
        Assert.Null(ImageContent.SniffImageExtension(new byte[16]));

    // ── ExtractImageUrlFromHtml ─────────────────────────────────────────

    [Fact]
    public void Html_AbsoluteSrc()
    {
        var url = ImageContent.ExtractImageUrlFromHtml(
            "<html><img src=\"https://example.com/a.svg\" alt=\"x\"></html>");
        Assert.Equal("https://example.com/a.svg", url);
    }

    [Fact]
    public void Html_RelativeSrc_ResolvedWithSourceUrl()
    {
        var cfHtml = "Version:0.9\r\nSourceURL:https://blog.example.com/post/1\r\n"
                   + "<html><img src=\"/_astro/logo.svg\"></html>";
        Assert.Equal("https://blog.example.com/_astro/logo.svg",
            ImageContent.ExtractImageUrlFromHtml(cfHtml));
    }

    [Fact]
    public void Html_RelativeSrc_WithoutSourceUrl_IsNull() =>
        Assert.Null(ImageContent.ExtractImageUrlFromHtml("<img src=\"/logo.svg\">"));

    [Theory]
    [InlineData("<img src='https://example.com/a.png'>")]
    [InlineData("<img src=https://example.com/a.png>")]
    [InlineData("<IMG SRC=\"https://example.com/a.png\">")]
    public void Html_QuoteStylesAndCase(string html) =>
        Assert.Equal("https://example.com/a.png", ImageContent.ExtractImageUrlFromHtml(html));

    [Fact]
    public void Html_EntityDecoded() =>
        Assert.Equal("https://example.com/a.png?w=1&h=2",
            ImageContent.ExtractImageUrlFromHtml("<img src=\"https://example.com/a.png?w=1&amp;h=2\">"));

    [Fact]
    public void Html_NonHttpScheme_IsNull() =>
        Assert.Null(ImageContent.ExtractImageUrlFromHtml("<img src=\"file:///c:/x.png\">"));

    [Fact]
    public void Html_DataUri_IsNull() =>
        Assert.Null(ImageContent.ExtractImageUrlFromHtml("<img src=\"data:image/png;base64,AAAA\">"));

    [Fact]
    public void Html_NoImg_IsNull() =>
        Assert.Null(ImageContent.ExtractImageUrlFromHtml("<html><p>text only</p></html>"));

    // ── TryParseHttpUrl ─────────────────────────────────────────────────

    [Theory]
    [InlineData("https://example.com/a.png")]
    [InlineData("http://example.com/a.png")]
    [InlineData("  https://example.com/a.png  ")]
    public void Url_Valid(string text) =>
        Assert.NotNull(ImageContent.TryParseHttpUrl(text));

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://example.com/a b.png")]
    [InlineData("https://example.com/a\r\nsecond line")]
    [InlineData("ftp://example.com/a.png")]
    [InlineData("file:///c:/a.png")]
    [InlineData("/relative/path.png")]
    public void Url_Invalid(string text) =>
        Assert.Null(ImageContent.TryParseHttpUrl(text));

    [Fact]
    public void Url_TooLong_IsNull() =>
        Assert.Null(ImageContent.TryParseHttpUrl("https://example.com/" + new string('a', 2048)));
}
