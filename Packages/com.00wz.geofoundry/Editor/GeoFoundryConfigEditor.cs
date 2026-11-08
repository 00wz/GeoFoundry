using GeoFoundry.Bootstrap;
using UnityEditor;
using UnityEngine;

namespace GeoFoundry.Editor
{
    /// <summary>Shows the settings for the selected source family, preserving the other family's values.</summary>
    [CustomEditor(typeof(GeoFoundryConfig)), CanEditMultipleObjects]
    internal sealed class GeoFoundryConfigEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            var mode = serializedObject.FindProperty(nameof(GeoFoundryConfig.SourceMode));
            EditorGUILayout.PropertyField(mode);
            if (mode.hasMultipleDifferentValues || mode.intValue == (int)TileSourceMode.LocalPmTiles)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(GeoFoundryConfig.VectorArchivePath)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(GeoFoundryConfig.DemArchivePath)));
                EditorGUILayout.HelpBox("Archive paths are relative to StreamingAssets, or absolute paths/HTTP(S) URLs. " +
                    "Supply your own MVT and Terrarium PNG archives. Web hosting must support HTTP Range (206). " +
                    "Leave the DEM path empty for flat terrain.", MessageType.Info);
            }
            if (mode.hasMultipleDifferentValues || mode.intValue == (int)TileSourceMode.RemoteHttp)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(GeoFoundryConfig.VectorTilesEndpointTemplate)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(GeoFoundryConfig.DemTilesEndpointTemplate)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(GeoFoundryConfig.MaxConcurrentHttpRequests)));
            }
            DrawPropertiesExcluding(serializedObject, "m_Script", nameof(GeoFoundryConfig.SourceMode),
                nameof(GeoFoundryConfig.VectorArchivePath), nameof(GeoFoundryConfig.DemArchivePath),
                nameof(GeoFoundryConfig.VectorTilesEndpointTemplate), nameof(GeoFoundryConfig.DemTilesEndpointTemplate),
                nameof(GeoFoundryConfig.MaxConcurrentHttpRequests));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
