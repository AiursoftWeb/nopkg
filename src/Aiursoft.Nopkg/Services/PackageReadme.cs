using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Aiursoft.Nopkg.Services;

public static class PackageReadme
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    public static string Render(string markdown, string? repository)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            var url = link.Url;
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith('#')) continue;
            if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            {
                if (absolute.Scheme != "https" && absolute.Scheme != "http" && !(absolute.Scheme == "mailto" && !link.IsImage)) link.Url = "#";
                continue;
            }
            if (url.StartsWith("//", StringComparison.Ordinal)) { link.Url = "#"; continue; }
            if (Uri.TryCreate(repository?.TrimEnd('/'), UriKind.Absolute, out var source) &&
                source.Scheme == "https" && source.Host == "github.com" && source.AbsolutePath.Trim('/').Split('/').Length == 2)
            {
                var project = source.AbsolutePath.Trim('/');
                if (project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
                var prefix = link.IsImage ? $"https://raw.githubusercontent.com/{project}/HEAD/" : $"https://github.com/{project}/blob/HEAD/";
                link.Url = new Uri(new Uri(prefix), url).AbsoluteUri;
            }
            else link.Url = "#";
        }
        return document.ToHtml(Pipeline);
    }

    public static string? WebUrl(string? value)
    {
        if (value?.StartsWith("git+https://", StringComparison.Ordinal) == true) value = value[4..];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || !string.IsNullOrEmpty(url.UserInfo) ||
            (url.Scheme != "https" && url.Scheme != "http")) return null;
        return url.AbsoluteUri;
    }
}
