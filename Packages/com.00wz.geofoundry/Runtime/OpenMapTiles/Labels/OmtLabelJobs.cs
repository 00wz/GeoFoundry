using GeoFoundry.Core.Mvt;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace GeoFoundry.OpenMapTiles
{
    /// <summary>Which text a label request shows.</summary>
    internal enum OmtLabelText : byte
    {
        /// <summary>The preferred name (see <see cref="OmtTextSettings.NameKeys"/>).</summary>
        Name = 0,
        /// <summary>The value of <see cref="OmtLabelRequestJob.FieldKey"/> (e.g. <c>housenumber</c>, <c>ref</c>).</summary>
        Field = 1,
        /// <summary>The name, a line break and the field with its suffix (e.g. a peak name and its elevation).</summary>
        NameAndField = 2,
        /// <summary>No text: icon-only labels (oneway arrows).</summary>
        None = 3,
    }

    /// <summary>
    /// Collects the glyph keys (style + code point) of all request texts, so the main thread can make sure they are
    /// in the font atlases before layout.
    /// </summary>
    [BurstCompile]
    internal struct OmtCollectGlyphsJob : IJob
    {
        [ReadOnly] public NativeArray<OmtLabelRequest> Requests;
        [ReadOnly] public NativeArray<OmtLabelRuleData> Rules;
        public NativeHashSet<int> Keys;

        public void Execute()
        {
            var cps = new NativeList<int>(64, Allocator.Temp);
            for (int r = 0; r < Requests.Length; r++)
            {
                var req = Requests[r];
                var rule = Rules[req.Rule];
                Keys.Add(OmtText.GlyphKey(rule.Font, ' '));
                cps.Clear();
                OmtText.Decode(req.Text, rule.Transform, ref cps);
                for (int i = 0; i < cps.Length; i++)
                    if (cps[i] != '\n') Keys.Add(OmtText.GlyphKey(rule.Font, cps[i]));
            }
            cps.Dispose();
        }
    }

    internal static class OmtIcons
    {
        /// <summary>
        /// Resolves an icon name template (<c>{class}</c> and <c>{subclass}</c> are replaced by the feature's values)
        /// to an icon id, or -1 if the template is empty or the icon does not exist.
        /// </summary>
        public static int Resolve(in FixedString64Bytes template, in FixedString128Bytes cls, in FixedString128Bytes sub,
            NativeHashMap<FixedString64Bytes, int> ids)
        {
            if (template.IsEmpty || !ids.IsCreated) return -1;
            FixedString64Bytes name = default;
            FixedString32Bytes tClass = "{class}", tSub = "{subclass}";
            var t = template;
            int i = 0;
            while (i < t.Length)
            {
                if (t[i] == '{')
                {
                    if (StartsWith(t, i, tSub))
                    {
                        if (name.Length + sub.Length > name.Capacity) return -1;
                        name.Append(sub);
                        i += tSub.Length;
                        continue;
                    }
                    if (StartsWith(t, i, tClass))
                    {
                        if (name.Length + cls.Length > name.Capacity) return -1;
                        name.Append(cls);
                        i += tClass.Length;
                        continue;
                    }
                }
                name.Add(t[i]);
                i++;
            }
            return ids.TryGetValue(name, out int id) ? id : -1;
        }

        private static bool StartsWith(in FixedString64Bytes s, int offset, in FixedString32Bytes prefix)
        {
            if (offset + prefix.Length > s.Length) return false;
            for (int k = 0; k < prefix.Length; k++)
                if (s[offset + k] != prefix[k]) return false;
            return true;
        }
    }

    /// <summary>
    /// General label request generator shared by the label layers: matches Point (and/or LineString) features against
    /// label rules by <see cref="ClassKey"/>/<see cref="SubclassKey"/>, composes the text (<see cref="TextKind"/>),
    /// resolves the icon, applies the rank filter and the skip modifiers, and writes one request per feature.
    /// </summary>
    [BurstCompile]
    internal struct OmtLabelRequestJob : IJob
    {
        [ReadOnly] public NativeArray<MvtGeomType> GeomTypes;
        public OmtProps Props;
        [ReadOnly] public NativeArray<OmtLabelRuleData> Rules;
        [ReadOnly] public NativeArray<FixedString64Bytes> NameKeys;
        [ReadOnly] public NativeHashMap<FixedString64Bytes, int> IconIds;
        [ReadOnly] public NativeArray<OmtModifier> Modifiers;

        public FixedString64Bytes ClassKey;
        public FixedString64Bytes SubclassKey;
        public bool Points;
        public bool Lines;
        /// <summary>Added to the matched rule index (several passes share one rule array).</summary>
        public int RuleOffset;
        /// <summary>When set, only features where this attribute is truthy are labeled.</summary>
        public FixedString64Bytes RequireKey;
        /// <summary>When set, features where this attribute is negative get <see cref="OmtLabelFlags.Reverse"/>.</summary>
        public FixedString64Bytes ReverseKey;

        public OmtLabelText TextKind;
        public FixedString64Bytes FieldKey;
        /// <summary>Used when <see cref="FieldKey"/> is empty on the feature (e.g. <c>route_1_ref</c> for shields).</summary>
        public FixedString64Bytes FallbackFieldKey;
        public FixedString32Bytes FieldSuffix;
        /// <summary>When <see cref="AltConditionKey"/> is truthy, <see cref="AltFieldKey"/> + <see cref="AltSuffix"/>
        /// replace the field (elevation in feet where feet are customary).</summary>
        public FixedString64Bytes AltConditionKey;
        public FixedString64Bytes AltFieldKey;
        public FixedString32Bytes AltSuffix;

        public FixedString64Bytes RankKey;
        /// <summary>Features with a rank above this are skipped; 0 disables the filter.</summary>
        public int MaxRank;
        public OmtIconMode IconMode;
        /// <summary>Icon template tried when the rule's icon does not exist (e.g. <c>{class}</c>).</summary>
        public FixedString64Bytes FallbackIcon;
        public float Spacing;
        public OmtLabelFlags Flags;

        public NativeList<OmtLabelRequest> Output;

        public void Execute()
        {
            for (int fi = 0; fi < GeomTypes.Length; fi++)
            {
                var geom = GeomTypes[fi];
                OmtLabelKind kind;
                if (geom == MvtGeomType.Point && Points) kind = OmtLabelKind.Point;
                else if (geom == MvtGeomType.LineString && Lines) kind = OmtLabelKind.Line;
                else continue;

                if (!RequireKey.IsEmpty && !Props.IsTrue(fi, RequireKey)) continue;

                bool skip = false;
                for (int m = 0; m < Modifiers.Length && !skip; m++)
                    if (Modifiers[m].Action == OmtModifierAction.Skip && Modifiers[m].Applies(ref Props, fi)) skip = true;
                if (skip) continue;

                FixedString128Bytes cls = default, sub = default;
                bool hasClass = !ClassKey.IsEmpty && Props.TryGetText(fi, ClassKey, out cls);
                bool hasSub = !SubclassKey.IsEmpty && Props.TryGetText(fi, SubclassKey, out sub);
                int ri = OmtLabelRules.Match(Rules, cls, hasClass, sub, hasSub);
                if (ri < 0 || !Rules[ri].Visible) continue;

                double rank = RankKey.IsEmpty ? 0 : Props.GetNumber(fi, RankKey, 100);
                if (MaxRank > 0 && rank > MaxRank) continue;

                if (!ComposeText(fi, out var text)) continue;

                var req = OmtLabelRequest.Create(fi, kind, ri + RuleOffset, text, (float)rank);
                req.Spacing = Spacing;
                req.Flags = Flags;
                if (!ReverseKey.IsEmpty && Props.GetNumber(fi, ReverseKey, 0) < 0) req.Flags |= OmtLabelFlags.Reverse;
                var rule = Rules[ri];
                if (IconMode != OmtIconMode.None)
                {
                    int icon = OmtIcons.Resolve(rule.Icon, cls, sub, IconIds);
                    if (icon < 0 && !rule.Icon.IsEmpty) icon = OmtIcons.Resolve(FallbackIcon, cls, sub, IconIds);
                    if (icon >= 0)
                    {
                        req.Icon = icon;
                        req.IconMode = IconMode;
                    }
                    else if (IconMode == OmtIconMode.Shield)
                    {
                        continue; // a shield without its background is not drawn
                    }
                }
                if (req.Text.Length == 0 && req.Icon < 0) continue;
                Output.Add(req);
            }
        }

        private bool ComposeText(int fi, out FixedString128Bytes text)
        {
            text = default;
            if (TextKind == OmtLabelText.None) return true;
            bool hasName = false;
            if (TextKind != OmtLabelText.Field) hasName = Props.SelectText(fi, NameKeys, out text);
            if (TextKind == OmtLabelText.Name) return hasName;

            FixedString128Bytes field = default;
            FixedString32Bytes suffix = FieldSuffix;
            bool hasField;
            if (!AltConditionKey.IsEmpty && Props.IsTrue(fi, AltConditionKey) && Props.TryGetNonEmpty(fi, AltFieldKey, out field))
            {
                hasField = true;
                suffix = AltSuffix;
            }
            else
            {
                hasField = Props.TryGetNonEmpty(fi, FieldKey, out field) ||
                           (!FallbackFieldKey.IsEmpty && Props.TryGetNonEmpty(fi, FallbackFieldKey, out field));
            }

            if (TextKind == OmtLabelText.Field)
            {
                if (!hasField) return false;
                text = field;
                if (field.Length + suffix.Length <= field.Capacity) text.Append(suffix);
                return true;
            }

            // NameAndField
            if (hasField)
            {
                if (hasName && text.Length + 1 <= text.Capacity) text.Append('\n');
                if (text.Length + field.Length + suffix.Length <= text.Capacity)
                {
                    text.Append(field);
                    text.Append(suffix);
                }
            }
            return hasName || hasField;
        }
    }
}
