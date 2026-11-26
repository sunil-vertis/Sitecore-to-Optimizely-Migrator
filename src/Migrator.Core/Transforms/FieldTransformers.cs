using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Migrator.Core.Models;

namespace Migrator.Core.Transforms;

public sealed class StringTransformer : IFieldTransformer
{
    public string Kind => "string";

    public Task<object?> TransformAsync(
        SitecoreFieldValue source,
        FieldTransformContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(source.Value);
}

public sealed class BooleanTransformer : IFieldTransformer
{
    public string Kind => "boolean";

    public Task<object?> TransformAsync(
        SitecoreFieldValue source,
        FieldTransformContext context,
        CancellationToken cancellationToken = default)
    {
        var v = source.Value?.Trim();
        if (string.IsNullOrEmpty(v))
            return Task.FromResult<object?>(null);

        var result = v is "1" or "true" or "True" or "yes" or "Yes";
        return Task.FromResult<object?>(result);
    }
}

public sealed class NumberTransformer : IFieldTransformer
{
    public string Kind => "number";

    public Task<object?> TransformAsync(
        SitecoreFieldValue source,
        FieldTransformContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.Value))
            return Task.FromResult<object?>(null);

        if (decimal.TryParse(source.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return Task.FromResult<object?>(d);

        throw new InvalidOperationException($"Cannot parse number from '{source.Value}' for field '{source.Name}'.");
    }
}

public sealed class DateTimeTransformer : IFieldTransformer
{
    public string Kind => "dateTime";

    public Task<object?> TransformAsync(
        SitecoreFieldValue source,
        FieldTransformContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.Value))
            return Task.FromResult<object?>(null);

        // Sitecore ISO: yyyyMMddTHHmmssZ
        if (DateTime.TryParseExact(
                source.Value,
                ["yyyyMMddTHHmmssZ", "yyyyMMddTHHmmss", "o", "s"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            return Task.FromResult<object?>(dt.ToString("o"));
        }

        if (DateTimeOffset.TryParse(source.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            return Task.FromResult<object?>(dto.ToString("o"));

        throw new InvalidOperationException($"Cannot parse date from '{source.Value}' for field '{source.Name}'.");
    }
}

public sealed class RichTextTransformer : IFieldTransformer
{
    public string Kind => "richText";

    public Task<object?> TransformAsync(
        SitecoreFieldValue source,
        FieldTransformContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.Value))
            return Task.FromResult<object?>(null);

        // Store HTML as-is; Opti rich text ingestion can be refined later.
        // Property shape used by CMS SaaS REST: { "value": { "html": "..." } } or plain html depending on type.
        return Task.FromResult<object?>(new Dictionary<string, object?>
        {
            ["html"] = source.Value
        });
    }
}

public sealed class LinkTransformer : IFieldTransformer
{
    public string Kind => "link";

    public async Task<object?> TransformAsync(
        SitecoreFieldValue source,
        FieldTransformContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.Value))
            return null;

        // Plain URL (not XML)
        var trimmed = source.Value.Trim();
        if (!trimmed.StartsWith('<'))
        {
            return BuildLink(url: trimmed, text: trimmed);
        }

        try
        {
            var xml = XElement.Parse(trimmed);

            // Sitecore Image field: <image mediaid="{guid}" src="..." alt="..." />
            // Opti maps MainImage/Thumbnail/ConfiguratorImage as type link — never send contentKey: null.
            var mediaIdAttr = xml.Attribute("mediaid")?.Value ?? xml.Attribute("mediaId")?.Value;
            if (!string.IsNullOrWhiteSpace(mediaIdAttr) ||
                string.Equals(xml.Name.LocalName, "image", StringComparison.OrdinalIgnoreCase))
            {
                return await BuildImageLinkAsync(xml, mediaIdAttr, context, cancellationToken);
            }

            var url = xml.Attribute("url")?.Value
                      ?? xml.Attribute("href")?.Value
                      ?? xml.Attribute("anchor")?.Value
                      ?? string.Empty;
            var text = xml.Attribute("text")?.Value
                       ?? xml.Attribute("alt")?.Value
                       ?? string.Empty;
            var target = xml.Attribute("target")?.Value ?? string.Empty;
            var title = xml.Attribute("title")?.Value ?? string.Empty;
            var linkType = xml.Attribute("linktype")?.Value ?? string.Empty;

            string? contentKey = null;
            Guid? linkedId = null;
            var idAttr = xml.Attribute("id")?.Value ?? mediaIdAttr;
            if (Guid.TryParse(idAttr?.Trim('{', '}'), out var parsedId))
                linkedId = parsedId;

            if (linkedId is not null &&
                (string.Equals(linkType, "internal", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(linkType, "media", StringComparison.OrdinalIgnoreCase)))
            {
                contentKey = context.ResolveOptimizelyKey(linkedId.Value);
                if (string.IsNullOrEmpty(contentKey) &&
                    string.Equals(linkType, "media", StringComparison.OrdinalIgnoreCase))
                {
                    contentKey = await context.ResolveOrUploadMediaAsync(linkedId.Value);
                }
            }

            return BuildLink(url, text, target, title, contentKey);
        }
        catch
        {
            // Unparseable — omit rather than send an invalid Link shape that fails Opti create.
            return null;
        }
    }

    private static async Task<object?> BuildImageLinkAsync(
        XElement xml,
        string? mediaIdAttr,
        FieldTransformContext context,
        CancellationToken cancellationToken)
    {
        var url = xml.Attribute("src")?.Value
                  ?? xml.Attribute("url")?.Value
                  ?? string.Empty;
        var text = xml.Attribute("alt")?.Value
                   ?? xml.Attribute("text")?.Value
                   ?? string.Empty;
        var title = xml.Attribute("title")?.Value ?? text;

        string? contentKey = null;
        if (Guid.TryParse(mediaIdAttr?.Trim('{', '}'), out var mediaId))
        {
            contentKey = context.ResolveOptimizelyKey(mediaId);
            if (string.IsNullOrEmpty(contentKey))
                contentKey = await context.ResolveOrUploadMediaAsync(mediaId);
        }

        // Prefer a resolvable Opti media key; otherwise keep a usable URL; else skip the field.
        if (string.IsNullOrWhiteSpace(contentKey) && string.IsNullOrWhiteSpace(url))
            return null;

        return BuildLink(url, text, title: title, contentKey: contentKey);
    }

    private static Dictionary<string, object?>? BuildLink(
        string? url = null,
        string? text = null,
        string? target = null,
        string? title = null,
        string? contentKey = null)
    {
        var link = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // CMS SaaS rejects Link payloads that include contentKey: null ("Could not read value as Link").
        if (!string.IsNullOrWhiteSpace(contentKey))
            link["contentKey"] = contentKey;

        if (!string.IsNullOrWhiteSpace(url))
            link["url"] = url;

        if (!string.IsNullOrWhiteSpace(text))
            link["text"] = text;

        if (!string.IsNullOrWhiteSpace(title))
            link["title"] = title;

        if (!string.IsNullOrWhiteSpace(target))
            link["target"] = target;

        return link.Count == 0 ? null : link;
    }
}

public sealed class ContentReferenceTransformer : IFieldTransformer
{
    public string Kind => "contentReference";

    public Task<object?> TransformAsync(
        SitecoreFieldValue source,
        FieldTransformContext context,
        CancellationToken cancellationToken = default)
    {
        var id = ParseFirstGuid(source.Value);
        if (id is null)
            return Task.FromResult<object?>(null);

        var key = context.ResolveOptimizelyKey(id.Value);
        return Task.FromResult<object?>(key);
    }

    internal static Guid? ParseFirstGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = Regex.Match(value, @"\{?[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}?");
        if (!match.Success)
            return null;

        return Guid.TryParse(match.Value.Trim('{', '}'), out var g) ? g : null;
    }
}

public sealed class ContentReferenceListTransformer : IFieldTransformer
{
    public string Kind => "contentReferenceList";

    public Task<object?> TransformAsync(
        SitecoreFieldValue source,
        FieldTransformContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.Value))
            return Task.FromResult<object?>(null);

        var keys = new List<string>();
        foreach (Match match in Regex.Matches(source.Value, @"\{?[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}?"))
        {
            if (!Guid.TryParse(match.Value.Trim('{', '}'), out var id))
                continue;

            var key = context.ResolveOptimizelyKey(id);
            if (!string.IsNullOrEmpty(key))
                keys.Add(key);
        }

        return Task.FromResult<object?>(keys.Count == 0 ? null : keys);
    }
}

public sealed class ContentAreaTransformer : IFieldTransformer
{
    public string Kind => "contentArea";

    public Task<object?> TransformAsync(
        SitecoreFieldValue source,
        FieldTransformContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.Value))
            return Task.FromResult<object?>(null);

        var keys = new List<string>();

        // Sitecore content area XML often contains <datasource>{guid}</datasource> or item IDs in JSON.
        foreach (Match match in Regex.Matches(source.Value, @"\{?[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}?"))
        {
            if (!Guid.TryParse(match.Value.Trim('{', '}'), out var id))
                continue;

            var key = context.ResolveOptimizelyKey(id);
            if (!string.IsNullOrEmpty(key) && !keys.Contains(key))
                keys.Add(key);
        }

        return Task.FromResult<object?>(keys.Count == 0 ? null : keys);
    }
}

public sealed class MediaTransformer : IFieldTransformer
{
    public string Kind => "media";

    public async Task<object?> TransformAsync(
        SitecoreFieldValue source,
        FieldTransformContext context,
        CancellationToken cancellationToken = default)
    {
        var id = ContentReferenceTransformer.ParseFirstGuid(source.Value);
        if (id is null)
            return null;

        var existing = context.ResolveOptimizelyKey(id.Value);
        if (!string.IsNullOrEmpty(existing))
            return existing;

        return await context.ResolveOrUploadMediaAsync(id.Value);
    }
}

public static class FieldTransformerRegistry
{
    private static readonly Dictionary<string, IFieldTransformer> Transformers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["string"] = new StringTransformer(),
            ["richText"] = new RichTextTransformer(),
            ["boolean"] = new BooleanTransformer(),
            ["number"] = new NumberTransformer(),
            ["dateTime"] = new DateTimeTransformer(),
            ["link"] = new LinkTransformer(),
            ["contentReference"] = new ContentReferenceTransformer(),
            ["contentReferenceList"] = new ContentReferenceListTransformer(),
            ["contentArea"] = new ContentAreaTransformer(),
            ["media"] = new MediaTransformer()
        };

    public static bool IsKnownKind(string kind) => Transformers.ContainsKey(kind);

    public static IReadOnlyCollection<string> KnownKinds => Transformers.Keys;

    public static IFieldTransformer Get(string kind)
    {
        if (!Transformers.TryGetValue(kind, out var transformer))
            throw new InvalidOperationException($"No transformer registered for kind '{kind}'.");
        return transformer;
    }
}
