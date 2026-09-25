using System;
using System.Collections.Generic;
using System.IO;
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

    public static ImportDocument Load(string path) => Parse(File.ReadAllText(path));

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
