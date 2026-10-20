using UnityEditor;
using UnityEngine;

namespace GeoFoundry.Editor.Build
{
    /// <summary>
    /// Menu commands that set <c>PlayerSettings.insecureHttpOption</c>, needed for local tile servers
    /// (martin/titiler) that usually serve plain http://.
    ///
    /// Since Unity 2022 the default is <c>NotAllowed</c>; without an explicit permission
    /// <see cref="UnityEngine.Networking.UnityWebRequest"/> refuses to send requests and logs
    /// "Non-secure network connections disabled in Player Settings".
    ///
    /// Options:
    /// - <c>NotAllowed</c> (default) — HTTP is blocked everywhere.
    /// - <c>DevelopmentOnly</c> — HTTP is allowed only in Development Builds and blocked in Release.
    /// - <c>AlwaysAllowed</c> — HTTP is allowed everywhere. Suitable for intranet tile servers, not for
    ///   public builds (security warning).
    /// </summary>
    internal static class HttpConfigurationHelper
    {
        private const string EnableMenuPath = "Tools/GeoFoundry/Allow HTTP (Development Builds)";
        private const string EnableAlwaysMenuPath = "Tools/GeoFoundry/Allow HTTP (Always — intranet only!)";
        private const string DisableMenuPath = "Tools/GeoFoundry/Disable HTTP (back to HTTPS-only)";

        [MenuItem(EnableMenuPath, priority = 200)]
        private static void EnableHttpDev()
        {
            PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;
            Debug.Log("[GeoFoundry] PlayerSettings.insecureHttpOption = DevelopmentOnly — HTTP is allowed in Development Builds and Editor Play Mode.");
        }

        [MenuItem(EnableAlwaysMenuPath, priority = 201)]
        private static void EnableHttpAlways()
        {
            if (!EditorUtility.DisplayDialog(
                "GeoFoundry — Allow HTTP",
                "Allow HTTP requests in ALL builds (including Release)?\n\n" +
                "Use this only in intranet environments (internal martin/titiler).\n" +
                "For public builds, use an HTTPS proxy.",
                "Allow", "Cancel"))
                return;

            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            Debug.Log("[GeoFoundry] PlayerSettings.insecureHttpOption = AlwaysAllowed — HTTP is allowed in all builds.");
        }

        [MenuItem(DisableMenuPath, priority = 202)]
        private static void DisableHttp()
        {
            PlayerSettings.insecureHttpOption = InsecureHttpOption.NotAllowed;
            Debug.Log("[GeoFoundry] PlayerSettings.insecureHttpOption = NotAllowed — HTTPS-only.");
        }
    }
}
