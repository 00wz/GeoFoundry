using UnityEngine;

namespace GeoFoundry.Samples.Innsbruck
{
    /// <summary>Visible data credits for the example map.</summary>
    public sealed class InnsbruckAttribution : MonoBehaviour
    {
        GUIStyle _creditsStyle;
        void OnGUI()
        {
            _creditsStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
            const float width = 490, height = 90;
            GUILayout.BeginArea(new Rect(8, Mathf.Max(8, Screen.height - height - 8), Mathf.Min(width, Screen.width - 16), height), GUI.skin.box);
            GUILayout.Label("Innsbruck • GeoFoundry sample");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("© OpenMapTiles")) Application.OpenURL("https://openmaptiles.org/");
            if (GUILayout.Button("© OpenStreetMap contributors")) Application.OpenURL("https://www.openstreetmap.org/");
            GUILayout.EndHorizontal();
            GUILayout.Label("Terrain: Mapzen · © offene Daten Österreichs · Copernicus · USGS · NOAA", _creditsStyle);
            GUILayout.EndArea();
        }
    }
}
