using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
            ParamScale.Byte => def.Min + value / 255.0 * (def.Max - def.Min),
            _ => value,
        };
        double lo = Math.Min(def.Min, def.Max), hi = Math.Max(def.Min, def.Max);
        clamped = w < lo - 1e-6 || w > hi + 1e-6 || double.IsNaN(w);
        if (double.IsNaN(w)) w = DefaultWeight(def);
        return (float)Math.Clamp(w, lo, hi);
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

        var perType = new Dictionary<WearableKind, int>();
        var wearables = spec.Wearables ?? new List<WearableSpec>();
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

            var composed = ComposeWearable(w, kind.Value, avatarScale, catalog, where, plan, spec);
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

    private static ComposedWearable ComposeWearable(WearableSpec w, WearableKind kind, ParamScale avatarScale, ParamCatalog catalog, string where, AvatarPlan plan, AvatarSpec spec)
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
            prms[def.Id] = DefaultWeight(def);

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

    /// <summary>The viewer's terse float form: at most three decimals, no trailing zeros, never "-0".</summary>
    public static string Terse(float value)
    {
        var s = Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    /// <summary>
    /// The appearance's VisualParams, encoded the way the viewer sends them (<see cref="VisualParamEncoder"/>): from
    /// the worn wearables' stored values, avatar_lad.xml defaults for everything else.
    /// </summary>
    public static byte[] EncodeVisualParams(AvatarLad lad, IEnumerable<(WearableKind Kind, IReadOnlyDictionary<int, float> Params)> worn)
        => VisualParamEncoder.Encode(lad, worn, null).Bytes;
}
