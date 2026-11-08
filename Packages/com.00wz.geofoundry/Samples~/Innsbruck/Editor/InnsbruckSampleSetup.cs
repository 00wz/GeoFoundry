using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace GeoFoundry.Samples.Innsbruck.Editor
{
    /// <summary>Installs data after sample import, without changing project settings or opening scenes.</summary>
    [InitializeOnLoad]
    public static class InnsbruckSampleSetup
    {
        public const string DataSubdirectory = "GeoFoundry/Innsbruck";
        static bool _queued;

        static InnsbruckSampleSetup()
        {
            // Register only: the AssetDatabase may not be ready during InitializeOnLoad.
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        internal static void QueueInstall()
        {
            if (_queued) return;
            _queued = true;
            EditorApplication.delayCall += InstallAfterImport;
        }

        static void InstallAfterImport()
        {
            _queued = false;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                QueueInstall();
                return;
            }
            try { EnsureInstalled(); }
            catch (Exception error) { Debug.LogWarning("[GeoFoundry] Innsbruck data setup: " + error.Message); }
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode) return;
            try { EnsureInstalled(); }
            catch (Exception error)
            {
                EditorApplication.isPlaying = false;
                Debug.LogError("[GeoFoundry] Cannot start Innsbruck: " + error.Message);
            }
        }

        public static void EnsureInstalled()
        {
            string root = FindRoot();
            if (root != null) InstallData(root);
        }

        public static string FindRoot()
        {
            var script = AssetDatabase.FindAssets("InnsbruckSampleSetup t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.StartsWith("Assets/", StringComparison.Ordinal) &&
                    AssetDatabase.LoadAssetAtPath<MonoScript>(p)?.GetClass() == typeof(InnsbruckSampleSetup));
            return script == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(script)).Replace('\\', '/');
        }

        /// <summary>Copies raw data to the actual StreamingAssets root. Never overwrites differing files.</summary>
        public static void InstallData(string sampleRoot)
        {
            string sourceRoot = Path.GetFullPath(Path.Combine(sampleRoot, "Data/Maps"));
            foreach (string archive in new[] { "vector.pmtiles", "dem.pmtiles" })
                if (!File.Exists(Path.Combine(sourceRoot, archive)))
                    throw new FileNotFoundException("Re-import the complete Innsbruck sample: missing " + archive);
            string destinationRoot = Path.Combine(Application.dataPath, "StreamingAssets", DataSubdirectory);
            var files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (string source in files)
            {
                string destination = Path.Combine(destinationRoot, source.Substring(sourceRoot.Length + 1));
                if (File.Exists(destination) && !EqualFiles(source, destination))
                    throw new IOException("Existing file differs: " + destination + ". Move it aside, or move your own data to another folder and update your config. No differing files were overwritten.");
            }
            bool changed = false;
            foreach (string source in files)
            {
                string destination = Path.Combine(destinationRoot, source.Substring(sourceRoot.Length + 1));
                if (File.Exists(destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(source, destination);
                changed = true;
            }
            // New raw assets receive their own GUIDs. Do not refresh again for an unchanged installation.
            if (changed) AssetDatabase.Refresh();
        }

        static bool EqualFiles(string first, string second)
        {
            if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
            using var hash = SHA256.Create();
            using var a = File.OpenRead(first);
            using var b = File.OpenRead(second);
            return hash.ComputeHash(a).SequenceEqual(hash.ComputeHash(b));
        }
    }

    internal sealed class InnsbruckSampleImport : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved,
            string[] movedFrom, bool didDomainReload)
        {
            bool IsSampleData(string p) => p.StartsWith("Assets/", StringComparison.Ordinal) &&
                (p.EndsWith("/InnsbruckSampleSetup.cs", StringComparison.Ordinal) || p.Contains("/Data/Maps/"));
            if (didDomainReload || imported.Any(IsSampleData) || moved.Any(IsSampleData))
                InnsbruckSampleSetup.QueueInstall();
        }
    }

    internal sealed class InnsbruckSampleBuild : IPreprocessBuildWithReport
    {
        public int callbackOrder => -100;
        public void OnPreprocessBuild(BuildReport report)
        {
            try { InnsbruckSampleSetup.EnsureInstalled(); }
            catch (Exception error) { throw new BuildFailedException("Innsbruck data setup: " + error.Message); }
        }
    }
}
