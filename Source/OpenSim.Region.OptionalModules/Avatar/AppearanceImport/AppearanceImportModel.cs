using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenSim.Region.OptionalModules.Avatar.AppearanceImport;

/// <summary>
/// The document <c>appearance import</c> reads and <c>appearance export</c> writes. See
/// <c>Docs/feature/AppearanceImport.md</c> for the full schema; the short form:
/// <code>
/// {
///   "paramScale": "value",            // value | slider | byte  (how "params" numbers are read)
///   "defaultPassword": "...",         // used when an account is created and the avatar gives none
///   "avatars": [ { "firstName": "...", "lastName": "...", "wearables": [ ... ], "attachments": [ ... ] } ]
/// }
/// </code>
/// A file may also hold a bare array of avatars, or a single avatar object.
/// </summary>
public sealed class ImportDocument
{
    public int? Version { get; set; }

    /// <summary>Default for every avatar and wearable that does not set its own. See <see cref="ParamScale"/>.</summary>
    public string ParamScale { get; set; }

    /// <summary>Password for accounts this import creates, when an avatar's own <c>account.password</c> is empty.</summary>
    public string DefaultPassword { get; set; }

    public List<AvatarSpec> Avatars { get; set; } = new();
}

public sealed class AvatarSpec
{
    public string FirstName { get; set; }
    public string LastName { get; set; }

    /// <summary>Optional. Used as the principal id when the account is created; ignored when it already exists.</summary>
    public string Uuid { get; set; }

    public AccountSpec Account { get; set; }

    /// <summary>Name of the inventory folder (under Clothing) the imported items go into. Default "Imported Outfit".</summary>
    public string OutfitName { get; set; }

    public string ParamScale { get; set; }

    /// <summary>
    /// True (default): the Current Outfit Folder is emptied and the avatar wears exactly this document — including
    /// its attachments. False: wearables are still replaced (a body part set must be complete), but existing
    /// attachments and their COF links are kept.
    /// </summary>
    public bool? ReplaceOutfit { get; set; }

    public List<WearableSpec> Wearables { get; set; } = new();
    public List<AttachmentSpec> Attachments { get; set; } = new();

    /// <summary>
    /// Optional: the avatar's VisualParams blob as a viewer sends it and the avatar service stores it — one byte per
    /// transmitted parameter (avatar_lad.xml group 0 and 3, id order; 253 with the current table), as a
    /// comma-separated string ("31,20,69,…") or a number array. Its bytes become the parameters of the shape, skin,
    /// hair and eyes (generated when the document does not list them) and of the topmost listed wearable of every
    /// other type. A value in a wearable's own <c>params</c> wins over the blob.
    /// </summary>
    public JsonElement? VisualParams { get; set; }

    [JsonIgnore]
    public string DisplayName => $"{FirstName} {LastName}";
}

public sealed class AccountSpec
{
    /// <summary>Create the account when it does not exist. Without this, a missing account is an error.</summary>
    public bool Create { get; set; }
    public string Password { get; set; }
    public string Email { get; set; }
}

public sealed class WearableSpec
{
    /// <summary>shape, skin, hair, eyes, shirt, pants, shoes, socks, jacket, gloves, undershirt, underpants, skirt, alpha, tattoo, physics, universal — or the number.</summary>
    public string Type { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }

    /// <summary>
    /// Wear an existing wearable asset instead of generating one. <see cref="Params"/> and <see cref="Textures"/>
    /// must then be empty.
    /// </summary>
    public string AssetId { get; set; }

    public string ParamScale { get; set; }

    /// <summary>Visual parameter values, keyed by avatar_lad.xml id ("33") or name ("Height").</summary>
    public Dictionary<string, double> Params { get; set; } = new();

    /// <summary>
    /// Texture slot (name such as "HeadBodypaint"/"head_bodypaint", or the number) → texture asset UUID, or a path
    /// to an image file relative to the import document (png/jpg/tga/bmp/webp are converted to JPEG2000;
    /// .j2c/.jp2/.j2k are uploaded as-is).
    /// </summary>
    public Dictionary<string, string> Textures { get; set; } = new();
}

public sealed class AttachmentSpec
{
    /// <summary>Attachment point: a name from OpenMetaverse.AttachmentPoint ("Skull", "Chest", "HUDCenter2"…) or its number.</summary>
    public string Point { get; set; }

    /// <summary>The object asset to attach. It must already exist in the asset service.</summary>
    public string AssetId { get; set; }

    public string Name { get; set; }
}

/// <summary>How the numbers in a <c>params</c> block are read.</summary>
public enum ParamScale
{
    /// <summary>The parameter's own weight, in its avatar_lad.xml [value_min, value_max] range — what an LLWearable asset stores.</summary>
    Value,
    /// <summary>0..100, the viewer's appearance-editor slider: 0 is value_min, 100 is value_max.</summary>
    Slider,
    /// <summary>0..255, a VisualParams byte: 0 is value_min, 255 is value_max.</summary>
    Byte,
}

public static class ImportDocumentReader
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Read a document: an object with an <c>avatars</c> array, a bare array of avatars, or a single avatar object.
    /// </summary>
    /// <exception cref="FormatException">The text is not JSON, or not one of the three shapes.</exception>
    public static ImportDocument Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new FormatException($"not valid JSON: {e.Message}", e);
        }

        using (doc)
        {
            try
            {
                var root = doc.RootElement;
                switch (root.ValueKind)
                {
                    case JsonValueKind.Array:
                        return new ImportDocument { Avatars = root.Deserialize<List<AvatarSpec>>(Options) ?? new() };

                    case JsonValueKind.Object when HasProperty(root, "avatars"):
                        var d = root.Deserialize<ImportDocument>(Options) ?? new ImportDocument();
                        d.Avatars ??= new();
                        return d;

                    case JsonValueKind.Object:
                        var single = root.Deserialize<AvatarSpec>(Options);
                        return new ImportDocument { Avatars = single is null ? new() : new List<AvatarSpec> { single } };

                    default:
                        throw new FormatException("the document must be an object or an array");
                }
            }
            catch (JsonException e)
            {
                throw new FormatException($"unexpected content: {e.Message}", e);
            }
        }
    }

    /// <summary>A .csv file is read with <see cref="ParseCsv"/>, anything else as a JSON document.</summary>
    public static ImportDocument Load(string path)
        => string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase)
            ? ParseCsv(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path))
            : Parse(File.ReadAllText(path));

    /// <summary>
    /// A VisualParams CSV: one avatar per non-empty line, in either of two layouts.
    /// <list type="bullet">
    /// <item><c>First,Last,31,20,69,…</c> — the name in the first two fields, the VisualParams bytes after it.</item>
    /// <item><c>31,20,69,…</c> — bytes only; the avatar is named by the file ("First_Last.csv", "First Last.csv"
    /// or "First.Last.csv"). Such a file must hold one line.</item>
    /// </list>
    /// A header line (a first field that is neither a number nor followed by numbers) and lines starting with '#'
    /// are skipped. The accounts are expected to exist; the byte count is checked by the planner (253, or 218 for
    /// a pre-physics avatar).
    /// </summary>
    /// <exception cref="FormatException">A line has no usable name, or a bytes-only file has more than one line.</exception>
    public static ImportDocument ParseCsv(string text, string fileName)
    {
        var doc = new ImportDocument { Version = 1 };
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var bytesOnly = new List<string>();
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n].Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var fields = line.Split(new[] { ',', ';', '\t' }).Select(f => f.Trim().Trim('"').Trim()).ToList();

            if (IsNumber(fields[0]))
            {
                bytesOnly.Add(string.Join(",", fields.Where(f => f.Length > 0)));
                continue;
            }
            if (fields.Count >= 3 && !IsNumber(fields[1]) && IsNumber(fields[2]))
            {
                doc.Avatars.Add(Avatar(fields[0], fields[1], string.Join(",", fields.Skip(2).Where(f => f.Length > 0)), n + 1));
                continue;
            }
            if (fields.Count >= 2 && IsNumber(fields[1]) && fields[0].Contains(' '))
            {
                // "First Last",31,20,…
                var parts = fields[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    doc.Avatars.Add(Avatar(parts[0], parts[1], string.Join(",", fields.Skip(1).Where(f => f.Length > 0)), n + 1));
                    continue;
                }
            }
            if (doc.Avatars.Count == 0 && bytesOnly.Count == 0) continue;   // a header line
            throw new FormatException($"line {n + 1}: expected First,Last,<values> or only values");
        }

        if (bytesOnly.Count > 0)
        {
            if (bytesOnly.Count > 1 || doc.Avatars.Count > 0)
                throw new FormatException("a file whose lines hold only values must hold exactly one line; put First,Last in front of each line to import several avatars from one file");
            var name = AvatarNameFromFileName(fileName);
            if (name is null)
                throw new FormatException($"the file holds only values, so the avatar is named by the file, but '{fileName}' is not 'First Last', 'First_Last' or '<prefix> - First Last'");
            doc.Avatars.Add(Avatar(name.Value.First, name.Value.Last, bytesOnly[0], 1));
        }
        return doc;
    }

    /// <summary>
    /// The avatar a values-only CSV is named after, from its file name (without extension): "First Last",
    /// "First_Last", "First.Last", or "&lt;prefix&gt; - First Last" (e.g. "data - 000heart000 Resident"), where the
    /// name is the text after the last " - ". Null when that text is not exactly two names.
    /// </summary>
    public static (string First, string Last)? AvatarNameFromFileName(string fileName)
    {
        var text = fileName ?? string.Empty;
        var dash = text.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0) text = text[(dash + 3)..];
        var parts = text.Split(new[] { '_', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : null;
    }

    private static AvatarSpec Avatar(string first, string last, string values, int line) => new()
    {
        FirstName = first,
        LastName = last,
        VisualParams = JsonDocument.Parse(JsonSerializer.Serialize(values)).RootElement.Clone(),
    };

    private static bool IsNumber(string s)
        => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    public static string Serialize(ImportDocument document) => JsonSerializer.Serialize(document, Options);

    private static bool HasProperty(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static ParamScale? ParseScale(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Trim().ToLowerInvariant() switch
        {
            "value" or "weight" or "raw" => ParamScale.Value,
            "slider" or "percent" or "ui" => ParamScale.Slider,
            "byte" or "u8" => ParamScale.Byte,
            _ => throw new FormatException($"paramScale '{s}' is not one of value, slider, byte"),
        };
    }
}
