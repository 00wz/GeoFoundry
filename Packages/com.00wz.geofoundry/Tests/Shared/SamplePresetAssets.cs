#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace GeoFoundry.Tests
{
    /// <summary>Imports only the shipped template's presets with fresh GUIDs; never loads a user's sample copy.</summary>
    public sealed class SamplePresetAssets : IDisposable
    {
        public const string Template = "Packages/com.00wz.geofoundry/Samples~/Innsbruck/Presets/OpenMapTiles";
        public string Root { get; }

        public SamplePresetAssets()
        {
            Root = "Assets/__GeoFoundryTestPresets_" + Guid.NewGuid().ToString("N");
            try
            {
                var guids = new Dictionary<string, string>();
                foreach (string meta in Directory.GetFiles(Template, "*.meta", SearchOption.AllDirectories))
                    guids.Add(Regex.Match(File.ReadAllText(meta), @"(?m)^guid: (\w+)").Groups[1].Value,
                        Guid.NewGuid().ToString("N"));
                foreach (string file in Directory.GetFiles(Template, "*", SearchOption.AllDirectories))
                {
                    string target = Root + file.Substring(Template.Length);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    string text = Regex.Replace(File.ReadAllText(file), @"guid: ([a-f0-9]{32})",
                        m => "guid: " + (guids.TryGetValue(m.Groups[1].Value, out var guid) ? guid : m.Groups[1].Value));
                    File.WriteAllText(target, text);
                }
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (!Root.StartsWith("Assets/__GeoFoundryTestPresets_", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid test asset directory.");
            if (!Directory.Exists(Root)) return;
            // Deleting files alone can leave loaded ScriptableObjects alive through handler references.
            // Unload them first so OnDisable releases glyph/decluttering caches allocated by PlayMode tests.
            foreach (string guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { Root }))
            {
                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null) Resources.UnloadAsset(asset);
            }
            AssetDatabase.DeleteAsset(Root);
        }
    }
}
#endif
