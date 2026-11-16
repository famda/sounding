using Microsoft.Extensions.FileProviders;
using ModelContextProtocol.Protocol;

namespace Sounding.Dashboard.Mcp;

public static class SoundingIcon
{
    // The dashboard's favicon as a data URI: the server has no public URL of its own to point clients at.
    public static IList<Icon>? From(IFileProvider webRoot)
    {
        var file = webRoot.GetFileInfo("favicon.svg");
        if (!file.Exists) return null;

        using var stream = file.CreateReadStream();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return [new Icon { Source = "data:image/svg+xml;base64," + Convert.ToBase64String(memory.GetBuffer(), 0, (int)memory.Length), MimeType = "image/svg+xml", Sizes = ["any"] }];
    }
}
