using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using OpenMetaverse;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.Region.OptionalModules.Avatar.AppearanceImport;

/// <summary>A texture a generated wearable references: an existing asset, or an image file still to be uploaded.</summary>
public sealed record TextureRef(UUID AssetId, string FilePath)
{
    public bool IsFile => FilePath is not null;
    public override string ToString() => IsFile ? FilePath : AssetId.ToString();
}

/// <summary>
/// One wearable of the plan. Either <see cref="ExistingAssetId"/> is set (wear that asset as-is) or
/// <see cref="Params"/>/<see cref="Textures"/> describe the LLWearable asset to generate.
/// </summary>
public sealed record ComposedWearable(
    WearableKind Kind,
    string Name,
    string Description,
    UUID ExistingAssetId,
    IReadOnlyDictionary<int, float> Params,
    IReadOnlyDictionary<TextureSlot, TextureRef> Textures)
{
    public bool UsesExistingAsset => !ExistingAssetId.IsZero();
    public bool IsBodyPart => WearableKinds.IsBodyPart(Kind);
}

public sealed record ComposedAttachment(int Point, UUID AssetId, string Name);

/// <summary>Everything the importer needs for one avatar, validated, with no service touched yet.</summary>
public sealed class AvatarPlan
{
    public AvatarSpec Spec { get; init; }
    public List<ComposedWearable> Wearables { get; } = new();
    public List<ComposedAttachment> Attachments { get; } = new();

    /// <summary>Body parts the document does not give; the importer wears the library default for each.</summary>
    public List<WearableKind> MissingBodyParts { get; } = new();

    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool Ok => Errors.Count == 0;

    /// <summary>The document's VisualParams blob, validated against the parameter table; null when it gave none.</summary>
    public byte[] VisualParams { get; set; }

    /// <summary>Parameter ids set by a wearable's own <c>params</c> (they win over <see cref="VisualParams"/>).</summary>
    public HashSet<int> ExplicitParamIds { get; } = new();
}

/// <summary>
/// avatar_lad.xml parameters indexed for lookup by id, by name ("Big_Brow") or by the viewer's editor label
/// ("Brow Size"), per wearable type. Names and labels match case-insensitively with spaces, underscores and
/// hyphens ignored. A name wins over a label, and the wearable type's own tweakable parameters win over any other.
/// </summary>
public sealed class ParamCatalog
{
    public AvatarLad Lad { get; }
    private readonly Dictionary<string, List<ParamDef>> m_byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ParamDef>> m_byLabel = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> m_labels = new();

    /// <param name="lad">The parameter table.</param>
    /// <param name="labels">Optional id → editor label (the <c>label</c> attribute, which <see cref="AvatarLad"/> does not keep).</param>
    public ParamCatalog(AvatarLad lad, IReadOnlyDictionary<int, string> labels = null)
    {
        Lad = lad ?? throw new ArgumentNullException(nameof(lad));
        foreach (var p in lad.Params.Values.OrderBy(p => p.Id))
        {
            Index(m_byName, p.Name, p);
            if (labels is not null && labels.TryGetValue(p.Id, out var label) && !string.IsNullOrWhiteSpace(label))
            {
                m_labels[p.Id] = label;
                Index(m_byLabel, label, p);
            }
        }
    }

    private static void Index(Dictionary<string, List<ParamDef>> map, string text, ParamDef p)
    {
        var key = Normalize(text);
        if (key.Length == 0) return;
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<ParamDef>();
        list.Add(p);
    }

    private static readonly System.Lazy<ParamCatalog> s_embedded = new(() => new ParamCatalog(AvatarLad.Embedded, ReadEmbeddedLabels()));

    /// <summary>The catalog over the avatar_lad.xml embedded in the baking library, labels included.</summary>
    public static ParamCatalog Embedded => s_embedded.Value;

    /// <summary>The <c>label</c> attribute of every <c>param</c> in the embedded avatar_lad.xml (first definition of an id wins, as in <see cref="AvatarLad"/>).</summary>
    public static IReadOnlyDictionary<int, string> ReadEmbeddedLabels()
    {
        var labels = new Dictionary<int, string>();
        using var stream = typeof(AvatarLad).Assembly.GetManifestResourceStream(AvatarLad.EmbeddedResourceName);
        if (stream is null) return labels;
        var doc = System.Xml.Linq.XDocument.Load(stream);
        foreach (var p in doc.Descendants("param"))
        {
            var idAttr = p.Attribute("id");
            var label = (string)p.Attribute("label");
            if (idAttr is null || string.IsNullOrWhiteSpace(label)) continue;
            if (int.TryParse(idAttr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && !labels.ContainsKey(id))
                labels[id] = label;
        }
        return labels;
    }

    public string LabelOf(int id) => m_labels.TryGetValue(id, out var l) ? l : null;

    public static string Normalize(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            if (c is not (' ' or '_' or '-')) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>The tweakable (group 0) parameters a wearable of this type stores, in id order.</summary>
    public IEnumerable<ParamDef> TweakablesOf(WearableKind kind)
    {
        var type = WearableKinds.TypeName(kind);
        return Lad.Params.Values
            .Where(p => p.Group == 0 && string.Equals(p.Wearable, type, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Id);
    }

    /// <summary>
    /// Resolve a params key for a wearable of <paramref name="kind"/>. A numeric key is an id. A name or label is
    /// looked up among the type's own parameters first, so a name used by several types resolves to the right one.
    /// <paramref name="ambiguous"/> is set when the key matched more than one of the type's tweakable parameters
    /// (avatar_lad.xml reuses a few names, e.g. "Torso Muscles"); the lowest id is returned — use the id to pick another.
    /// </summary>
    public ParamDef Resolve(string key, WearableKind kind, out bool ambiguous)
    {
        ambiguous = false;
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (int.TryParse(key.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return Lad.Params.TryGetValue(id, out var byId) ? byId : null;

        var norm = Normalize(key);
        var type = WearableKinds.TypeName(kind);
        foreach (var map in new[] { m_byName, m_byLabel })
        {
            if (!map.TryGetValue(norm, out var candidates)) continue;
            var own = candidates.Where(p => p.Group == 0 && string.Equals(p.Wearable, type, StringComparison.OrdinalIgnoreCase)).ToList();
            if (own.Count > 0)
            {
                ambiguous = own.Count > 1;
                return own[0];
            }
            var sameType = candidates.FirstOrDefault(p => string.Equals(p.Wearable, type, StringComparison.OrdinalIgnoreCase));
            if (sameType is not null) return sameType;
        }
        // Not one of this type's: report what it is, so the caller can say where it belongs.
        if (m_byName.TryGetValue(norm, out var any) || m_byLabel.TryGetValue(norm, out any))
            return any.FirstOrDefault(p => p.Group == 0) ?? any[0];
        return null;
    }

    public ParamDef Resolve(string key, WearableKind kind) => Resolve(key, kind, out _);
}

/// <summary>
/// Turns an <see cref="AvatarSpec"/> into an <see cref="AvatarPlan"/>, and a planned wearable into LLWearable text.
/// Pure: no scene, no services, so every rule is a unit test.
/// </summary>
public static class AppearancePlanner
{
    public const string DefaultOutfitName = "Imported Outfit";

    /// <summary><see cref="OpenSim.Framework.AvatarWearable"/> holds at most this many items of one type.</summary>
    public const int MaxPerType = 5;

    private static readonly WearableKind[] BodyParts = { WearableKind.Shape, WearableKind.Skin, WearableKind.Hair, WearableKind.Eyes };

    /// <summary>The texture slots a wearable of each type paints (the viewer's LLWearableType dictionary).</summary>
    public static IReadOnlyList<TextureSlot> SlotsFor(WearableKind kind) => kind switch
    {
        WearableKind.Skin => new[] { TextureSlot.HeadBodypaint, TextureSlot.UpperBodypaint, TextureSlot.LowerBodypaint },
        WearableKind.Hair => new[] { TextureSlot.Hair },
        WearableKind.Eyes => new[] { TextureSlot.EyesIris },
        WearableKind.Shirt => new[] { TextureSlot.UpperShirt },
        WearableKind.Pants => new[] { TextureSlot.LowerPants },
        WearableKind.Shoes => new[] { TextureSlot.LowerShoes },
        WearableKind.Socks => new[] { TextureSlot.LowerSocks },
        WearableKind.Jacket => new[] { TextureSlot.UpperJacket, TextureSlot.LowerJacket },
        WearableKind.Gloves => new[] { TextureSlot.UpperGloves },
        WearableKind.Undershirt => new[] { TextureSlot.UpperUndershirt },
        WearableKind.Underpants => new[] { TextureSlot.LowerUnderpants },
        WearableKind.Skirt => new[] { TextureSlot.Skirt },
        WearableKind.Alpha => new[] { TextureSlot.LowerAlpha, TextureSlot.UpperAlpha, TextureSlot.HeadAlpha, TextureSlot.EyesAlpha, TextureSlot.HairAlpha },
        WearableKind.Tattoo => new[] { TextureSlot.HeadTattoo, TextureSlot.UpperTattoo, TextureSlot.LowerTattoo },
        WearableKind.Universal => new[]
        {
            TextureSlot.HeadUniversalTattoo, TextureSlot.UpperUniversalTattoo, TextureSlot.LowerUniversalTattoo, TextureSlot.SkirtTattoo,
            TextureSlot.HairTattoo, TextureSlot.EyesTattoo, TextureSlot.LeftArmTattoo, TextureSlot.LeftLegTattoo,
            TextureSlot.Aux1Tattoo, TextureSlot.Aux2Tattoo, TextureSlot.Aux3Tattoo,
        },
        _ => Array.Empty<TextureSlot>(),
    };

    public static WearableKind? ParseKind(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return Enum.IsDefined(typeof(WearableKind), n) && n != (int)WearableKind.Invalid ? (WearableKind)n : null;
        if (string.Equals(s, "body", StringComparison.OrdinalIgnoreCase)) return WearableKind.Shape;
        return WearableKinds.FromName(s);
    }

    /// <summary>
    /// A texture slot key: the enum name in any case, with or without underscores ("head_bodypaint"), its number,
    /// or "texture"/"default" for a type that paints exactly one slot.
    /// </summary>
    public static TextureSlot? ParseSlot(string s, WearableKind kind)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return Enum.IsDefined(typeof(TextureSlot), n) && n >= 0 ? (TextureSlot)n : null;
        var slots = SlotsFor(kind);
        if (slots.Count == 1 && ParamCatalog.Normalize(s) is "texture" or "default" or "main")
            return slots[0];
        var norm = ParamCatalog.Normalize(s);
        foreach (TextureSlot t in Enum.GetValues(typeof(TextureSlot)))
            if (ParamCatalog.Normalize(t.ToString()) == norm) return t;
        return null;
    }

    /// <summary>Attachment point by OpenMetaverse.AttachmentPoint name or number; null when neither.</summary>
    public static int? ParseAttachPoint(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n is > 0 and < 128 ? n : null;
        var norm = ParamCatalog.Normalize(s);
        foreach (AttachmentPoint p in Enum.GetValues(typeof(AttachmentPoint)))
            if (p != AttachmentPoint.Default && ParamCatalog.Normalize(p.ToString()) == norm) return (int)p;
        return null;
    }

    /// <summary>
    /// A parameter's default weight as the viewer takes it: <c>value_default</c> (0 when absent) clamped into
    /// [value_min, value_max] (LLVisualParamInfo::parseXml), so e.g. "Out Shdw Opacity" (0.2..1) defaults to 0.2.
    /// </summary>
    public static float DefaultWeight(ParamDef def) => Math.Clamp(def.Default, Math.Min(def.Min, def.Max), Math.Max(def.Min, def.Max));

    /// <summary>Scale a params value into the parameter's own weight range; <paramref name="clamped"/> when it had to be limited.</summary>
    public static float ToWeight(double value, ParamScale scale, ParamDef def, out bool clamped)
    {
        double w = scale switch
        {
            ParamScale.Slider => def.Min + value / 100.0 * (def.Max - def.Min),
            ParamScale.Byte => def.Min + ByteFraction(value) * (def.Max - def.Min),
            _ => value,
        };
        double lo = Math.Min(def.Min, def.Max), hi = Math.Max(def.Min, def.Max);
        clamped = w < lo - 1e-6 || w > hi + 1e-6 || double.IsNaN(w);
        if (double.IsNaN(w)) w = DefaultWeight(def);
        return (float)Math.Clamp(w, lo, hi);
    }

    /// <summary>
    /// A VisualParams byte as a fraction of the range. The viewer encodes with truncation (F32_to_U8), so byte b
    /// stands for [b/255, (b+1)/255); the middle of that bucket survives the round trip through a wearable file and
    /// back to a byte, where its lower edge can come back one lower. 0 and 255 stay exactly on the ends.
    /// </summary>
    public static double ByteFraction(double b)
    {
        if (b <= 0) return b < 0 ? b / 255.0 : 0.0;
        if (b >= 255) return b / 255.0;
        return (Math.Floor(b) + 0.5) / 255.0;
    }

    /// <summary>
    /// Read a VisualParams blob: a comma/space/semicolon separated string or a JSON number array of bytes.
    /// Null for an absent value; an error for anything else, or for a length the parameter table does not have.
    /// </summary>
    public static byte[] ParseVisualParams(JsonElement? element, AvatarLad lad, out string error)
    {
        error = null;
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var values = new List<int>();
        var e = element.Value;
        if (e.ValueKind == JsonValueKind.String)
        {
            foreach (var tok in (e.GetString() ?? "").Split(new[] { ',', ' ', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { error = $"visualParams: '{tok}' is not a number"; return null; }
                values.Add(v);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var v)) { error = "visualParams: every entry must be a whole number"; return null; }
                values.Add(v);
            }
        }
        else { error = "visualParams must be a string of comma-separated bytes or an array of numbers"; return null; }

        if (values.Count == 0) return null;
        if (values.Any(v => v is < 0 or > 255)) { error = "visualParams: every value must be 0..255"; return null; }
        var expected = VisualParamEncoder.SendList(lad).Count;
        if (values.Count != expected)
        {
            error = $"visualParams has {values.Count} values but the parameter table transmits {expected}; "
                + "the bytes cannot be matched to parameters (a blob from an older viewer or another avatar_lad.xml)";
            return null;
        }
        return values.Select(v => (byte)v).ToArray();
    }

    public static AvatarPlan Plan(AvatarSpec spec, ImportDocument document, ParamCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(catalog);
        var plan = new AvatarPlan { Spec = spec };

        if (string.IsNullOrWhiteSpace(spec.FirstName) || string.IsNullOrWhiteSpace(spec.LastName))
            plan.Errors.Add("firstName and lastName are required");
        else if (spec.FirstName.Contains(' ') || spec.LastName.Contains(' '))
            plan.Errors.Add("firstName and lastName must not contain spaces");

        if (!string.IsNullOrWhiteSpace(spec.Uuid) && !UUID.TryParse(spec.Uuid, out _))
            plan.Errors.Add($"uuid '{spec.Uuid}' is not a UUID");

        ParamScale avatarScale;
        try { avatarScale = ImportDocumentReader.ParseScale(spec.ParamScale) ?? ImportDocumentReader.ParseScale(document?.ParamScale) ?? ParamScale.Value; }
        catch (FormatException e) { plan.Errors.Add(e.Message); avatarScale = ParamScale.Value; }

        // The VisualParams blob, if any, as id → byte.
        plan.VisualParams = ParseVisualParams(spec.VisualParams, catalog.Lad, out var vpError);
        if (vpError is not null) plan.Errors.Add(vpError);
        Dictionary<int, byte> blob = null;
        if (plan.VisualParams is not null)
        {
            var send = VisualParamEncoder.SendList(catalog.Lad);
            blob = new Dictionary<int, byte>(send.Count);
            for (var i = 0; i < send.Count; i++) blob[send[i].Id] = plan.VisualParams[i];
        }

        var wearables = new List<WearableSpec>(spec.Wearables ?? new List<WearableSpec>());
        if (blob is not null)
        {
            // A blob describes a whole body: generate any body part the document does not list.
            foreach (var bp in BodyParts)
                if (!wearables.Any(w => w is not null && ParseKind(w.Type) == bp))
                    wearables.Add(new WearableSpec { Type = WearableKinds.TypeName(bp) });
        }

        // The blob's values for a type belong to the topmost (last listed) wearable of that type — the one the
        // viewer took them from.
        var topmost = new Dictionary<WearableKind, int>();
        for (var i = 0; i < wearables.Count; i++)
            if (wearables[i] is not null && ParseKind(wearables[i].Type) is { } k && string.IsNullOrWhiteSpace(wearables[i].AssetId))
                topmost[k] = i;

        var perType = new Dictionary<WearableKind, int>();
        for (var i = 0; i < wearables.Count; i++)
        {
            var w = wearables[i];
            var where = $"wearables[{i}]";
            if (w is null) { plan.Errors.Add($"{where}: empty entry"); continue; }

            var kind = ParseKind(w.Type);
            if (kind is null) { plan.Errors.Add($"{where}: unknown type '{w.Type}'"); continue; }
            where = $"wearables[{i}] ({WearableKinds.TypeName(kind.Value)})";

            perType.TryGetValue(kind.Value, out var count);
            if (WearableKinds.IsBodyPart(kind.Value) && count >= 1) { plan.Errors.Add($"{where}: only one {WearableKinds.TypeName(kind.Value)} can be worn"); continue; }
            if (count >= MaxPerType) { plan.Warnings.Add($"{where}: more than {MaxPerType} of one type; ignored"); continue; }
            perType[kind.Value] = count + 1;

            var fromBlob = blob is not null && topmost.TryGetValue(kind.Value, out var top) && top == i ? blob : null;
            var composed = ComposeWearable(w, kind.Value, avatarScale, catalog, where, plan, spec, fromBlob);
            if (composed is not null) plan.Wearables.Add(composed);
        }

        foreach (var bp in BodyParts)
            if (!perType.ContainsKey(bp))
            {
                plan.MissingBodyParts.Add(bp);
                plan.Warnings.Add($"no {WearableKinds.TypeName(bp)} given; the library default is worn");
            }

        var attachments = spec.Attachments ?? new List<AttachmentSpec>();
        for (var i = 0; i < attachments.Count; i++)
        {
            var a = attachments[i];
            if (a is null) continue;
            var point = ParseAttachPoint(a.Point);
            if (point is null) { plan.Errors.Add($"attachments[{i}]: unknown attachment point '{a.Point}'"); continue; }
            if (!UUID.TryParse(a.AssetId ?? "", out var asset) || asset.IsZero()) { plan.Errors.Add($"attachments[{i}]: assetId '{a.AssetId}' is not a UUID"); continue; }
            plan.Attachments.Add(new ComposedAttachment(point.Value, asset, CleanLine(a.Name, $"Attachment {i + 1}")));
        }

        return plan;
    }

    private static ComposedWearable ComposeWearable(WearableSpec w, WearableKind kind, ParamScale avatarScale, ParamCatalog catalog, string where, AvatarPlan plan, AvatarSpec spec, IReadOnlyDictionary<int, byte> blob)
    {
        var name = CleanLine(w.Name, $"{spec.FirstName} {spec.LastName} {WearableKinds.TypeName(kind)}".Trim());
        var description = CleanLine(w.Description, "");
        var hasContent = (w.Params?.Count ?? 0) > 0 || (w.Textures?.Count ?? 0) > 0;

        if (!string.IsNullOrWhiteSpace(w.AssetId))
        {
            if (!UUID.TryParse(w.AssetId, out var existing) || existing.IsZero())
            {
                plan.Errors.Add($"{where}: assetId '{w.AssetId}' is not a UUID");
                return null;
            }
            if (hasContent)
                plan.Warnings.Add($"{where}: assetId is set, so params and textures are ignored");
            return new ComposedWearable(kind, name, description, existing, new Dictionary<int, float>(), new Dictionary<TextureSlot, TextureRef>());
        }

        ParamScale scale;
        try { scale = ImportDocumentReader.ParseScale(w.ParamScale) ?? avatarScale; }
        catch (FormatException e) { plan.Errors.Add($"{where}: {e.Message}"); scale = avatarScale; }

        // Start from avatar_lad.xml defaults for every tweakable parameter of the type, as a wearable saved by the
        // viewer would carry, then apply the document's values over them.
        var type = WearableKinds.TypeName(kind);
        var prms = new SortedDictionary<int, float>();
        foreach (var def in catalog.TweakablesOf(kind))
            prms[def.Id] = blob is not null && blob.TryGetValue(def.Id, out var b)
                ? ToWeight(b, ParamScale.Byte, def, out _)
                : DefaultWeight(def);

        if (w.Params is not null)
        {
            foreach (var (key, value) in w.Params)
            {
                var def = catalog.Resolve(key, kind, out var ambiguous);
                if (def is null) { plan.Warnings.Add($"{where}: unknown parameter '{key}'; ignored"); continue; }
                if (ambiguous)
                    plan.Warnings.Add($"{where}: parameter '{key}' names several {type} parameters; used id {def.Id} (key by id to choose another)");
                if (!string.Equals(def.Wearable, type, StringComparison.OrdinalIgnoreCase))
                {
                    plan.Warnings.Add($"{where}: parameter '{key}' ({def.Id} {def.Name}) belongs to {(string.IsNullOrEmpty(def.Wearable) ? "no wearable" : def.Wearable)}, not {type}; ignored");
                    continue;
                }
                prms[def.Id] = ToWeight(value, scale, def, out var clamped);
                plan.ExplicitParamIds.Add(def.Id);
                if (clamped)
                    plan.Warnings.Add($"{where}: parameter '{key}' = {value.ToString(CultureInfo.InvariantCulture)} ({scale}) is outside {def.Min.ToString(CultureInfo.InvariantCulture)}..{def.Max.ToString(CultureInfo.InvariantCulture)}; clamped");
            }
        }

        var textures = new SortedDictionary<TextureSlot, TextureRef>();
        var allowed = SlotsFor(kind);
        if (w.Textures is not null)
        {
            foreach (var (key, value) in w.Textures)
            {
                var slot = ParseSlot(key, kind);
                if (slot is null) { plan.Errors.Add($"{where}: unknown texture slot '{key}'"); continue; }
                if (!allowed.Contains(slot.Value))
                {
                    plan.Errors.Add($"{where}: a {type} does not paint slot {slot.Value}; it paints {(allowed.Count == 0 ? "no slots" : string.Join(", ", allowed))}");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(value)) continue;
                textures[slot.Value] = UUID.TryParse(value.Trim(), out var id) ? new TextureRef(id, null) : new TextureRef(UUID.Zero, value.Trim());
            }
        }

        return new ComposedWearable(kind, name, description, UUID.Zero, prms, textures);
    }

    /// <summary>A name or description that fits on one line of an LLWearable asset and in an inventory name.</summary>
    public static string CleanLine(string s, string fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        var one = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return one.Length > 63 ? one[..63] : one;
    }

    /// <summary>
    /// The LLWearable version 22 text for a generated wearable — the layout
    /// <c>OpenMetaverse.Assets.AssetWearable.Encode</c> writes and <see cref="WearableParser"/> and the viewer read.
    /// Full permissions: these are test avatars' own items.
    /// </summary>
    /// <param name="textureIds">The final asset id for every slot in <see cref="ComposedWearable.Textures"/> (uploaded files resolved).</param>
    public static string ToLLWearable(ComposedWearable w, IReadOnlyDictionary<TextureSlot, UUID> textureIds, UUID creator, UUID owner)
    {
        ArgumentNullException.ThrowIfNull(w);
        if (w.UsesExistingAsset) throw new InvalidOperationException("an existing asset is worn as-is, not regenerated");

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("LLWearable version 22\n");
        sb.Append(w.Name).Append('\n');
        sb.Append(w.Description ?? "").Append('\n');
        sb.Append("\tpermissions 0\n\t{\n");
        sb.Append("\t\tbase_mask\t7fffffff\n");
        sb.Append("\t\towner_mask\t7fffffff\n");
        sb.Append("\t\tgroup_mask\t00000000\n");
        sb.Append("\t\teveryone_mask\t00000000\n");
        sb.Append("\t\tnext_owner_mask\t7fffffff\n");
        sb.Append("\t\tcreator_id\t").Append(creator.ToString()).Append('\n');
        sb.Append("\t\towner_id\t").Append(owner.ToString()).Append('\n');
        sb.Append("\t\tlast_owner_id\t").Append(UUID.Zero.ToString()).Append('\n');
        sb.Append("\t\tgroup_id\t").Append(UUID.Zero.ToString()).Append('\n');
        sb.Append("\t}\n");
        sb.Append("\tsale_info\t0\n\t{\n\t\tsale_type\tnot\n\t\tsale_price\t10\n\t}\n");
        sb.Append("type ").Append(((int)w.Kind).ToString(inv)).Append('\n');

        sb.Append("parameters ").Append(w.Params.Count.ToString(inv)).Append('\n');
        foreach (var (id, value) in w.Params.OrderBy(kv => kv.Key))
            sb.Append(id.ToString(inv)).Append(' ').Append(Terse(value)).Append('\n');

        var slots = w.Textures.Keys.OrderBy(k => (int)k).ToList();
        var written = new List<(TextureSlot Slot, UUID Id)>();
        foreach (var slot in slots)
        {
            if (textureIds is null || !textureIds.TryGetValue(slot, out var id) || id.IsZero()) continue;
            written.Add((slot, id));
        }
        sb.Append("textures ").Append(written.Count.ToString(inv)).Append('\n');
        foreach (var (slot, id) in written)
            sb.Append(((int)slot).ToString(inv)).Append(' ').Append(id.ToString()).Append('\n');

        return sb.ToString();
    }

    /// <summary>
    /// A terse float: at most four decimals (enough to keep every VisualParams byte on its own step), no trailing
    /// zeros, never "-0".
    /// </summary>
    public static string Terse(float value)
    {
        var s = Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    /// <summary>
    /// The appearance's VisualParams, encoded the way the viewer sends them (<see cref="VisualParamEncoder"/>): from
    /// the worn wearables' stored values, avatar_lad.xml defaults for everything else.
    /// </summary>
    public static byte[] EncodeVisualParams(AvatarLad lad, IEnumerable<(WearableKind Kind, IReadOnlyDictionary<int, float> Params)> worn)
        => VisualParamEncoder.Encode(lad, worn, null).Bytes;

    /// <summary>
    /// The appearance's VisualParams for a plan: with no blob, <see cref="EncodeVisualParams(AvatarLad, IEnumerable{ValueTuple{WearableKind, IReadOnlyDictionary{int, float}}})"/>;
    /// with one, the document's bytes unchanged except where a wearable's own <c>params</c> set a value.
    /// </summary>
    public static byte[] EncodeVisualParams(AvatarLad lad, IEnumerable<(WearableKind Kind, IReadOnlyDictionary<int, float> Params)> worn, AvatarPlan plan)
    {
        var encoded = EncodeVisualParams(lad, worn);
        if (plan?.VisualParams is null || plan.VisualParams.Length != encoded.Length) return encoded;
        var send = VisualParamEncoder.SendList(lad);
        var result = (byte[])plan.VisualParams.Clone();
        for (var i = 0; i < send.Count; i++)
            if (plan.ExplicitParamIds.Contains(send[i].Id)) result[i] = encoded[i];
        return result;
    }
}
