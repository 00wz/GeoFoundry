using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace GeoFoundry.Editor.Build
{
    /// <summary>
    /// Build pre-processor for the Web target.
    /// Fails the build (<see cref="BuildFailedException"/>) when the Burst package is missing or older than
    /// the required version. Warns when <c>PlayerSettings.WebGL.threadsSupport</c> is off (Burst jobs then run
    /// on the main thread) or when HTTP requests are blocked.
    /// </summary>
    internal sealed class WebBuildValidator : IPreprocessBuildWithReport
    {
        // Minimum Burst version with official Web support (Unity 6.4).
        private const string MinBurstVersion = "1.8.26";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var target = report.summary.platform;
            if (target != BuildTarget.WebGL) return;

            ValidateThreadsSupport();
            ValidateBurstPackage();
            ValidateHttpInsecureOption();
        }

        private static void ValidateHttpInsecureOption()
        {
            // Warning only: the package user may have switched the endpoints to HTTPS.
            if (PlayerSettings.insecureHttpOption == InsecureHttpOption.NotAllowed)
            {
                Debug.LogWarning(
                    "[GeoFoundry] PlayerSettings.insecureHttpOption = NotAllowed — HTTP requests will be blocked. " +
                    "If your martin/titiler endpoints use HTTP, enable Tools → GeoFoundry → Allow HTTP (Development Builds), " +
                    "otherwise tiles will not load.");
            }
        }

        private static void ValidateThreadsSupport()
        {
#if UNITY_2023_1_OR_NEWER
            if (!PlayerSettings.WebGL.threadsSupport)
            {
                // Not a failure: a single-threaded build is valid (diagnostics, fallback, Brotli/workers
                // compatibility). Burst jobs still work on the main thread, just slower.
                Debug.LogWarning(
                    "[GeoFoundry] PlayerSettings.WebGL.threadsSupport is disabled. " +
                    "Burst jobs will run on the main thread (no parallelism). " +
                    "For production builds, enable 'Native C/C++ Multithreading' in Player Settings → Web.");
            }
#endif
        }

        private static void ValidateBurstPackage()
        {
            // GetAllRegisteredPackages is synchronous and sees indirect packages too
            // (Collections → Mathematics → Burst), unlike Client.List with includeIndirectDependencies: false.
            UnityEditor.PackageManager.PackageInfo[] packages;
            try
            {
                packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GeoFoundry] Failed to read the package list for the Burst version check: {ex.Message}");
                return;
            }

            if (packages == null || packages.Length == 0)
            {
                Debug.LogWarning("[GeoFoundry] PackageInfo.GetAllRegisteredPackages returned no packages — skipping the Burst check.");
                return;
            }

            var burst = packages.FirstOrDefault(p => p.name == "com.unity.burst");
            if (burst == null)
            {
                throw new BuildFailedException(
                    "[GeoFoundry] Package com.unity.burst is not installed. Add it to manifest.json " +
                    $"(>= {MinBurstVersion}); it is required for Web builds.");
            }

            if (CompareVersions(burst.version, MinBurstVersion) < 0)
            {
                throw new BuildFailedException(
                    $"[GeoFoundry] com.unity.burst version ({burst.version}) is below the minimum ({MinBurstVersion}). " +
                    "Burst and the Job System are officially supported on Web only in 1.8.26+ (Unity 6.4).");
            }
        }

        /// <summary>Compares SemVer strings ignoring pre-release suffixes. Returns a positive value if a &gt; b.</summary>
        private static int CompareVersions(string a, string b)
        {
            // Strip pre-release/build metadata: "1.8.26-preview.1" → "1.8.26".
            var aClean = a.Split('-', '+')[0];
            var bClean = b.Split('-', '+')[0];

            var aParts = aClean.Split('.');
            var bParts = bClean.Split('.');
            int len = System.Math.Max(aParts.Length, bParts.Length);
            for (int i = 0; i < len; i++)
            {
                int aNum = i < aParts.Length && int.TryParse(aParts[i], out var an) ? an : 0;
                int bNum = i < bParts.Length && int.TryParse(bParts[i], out var bn) ? bn : 0;
                if (aNum != bNum) return aNum.CompareTo(bNum);
            }
            return 0;
        }
    }
}
