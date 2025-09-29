using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class WebGLViewerBuild
{
    [MenuItem("Build/Build WebGL Viewer")]
    public static void BuildViewer()
    {
        // Projede "MatchViewer.unity" sahnesini bul
        var scenePath = AssetDatabase.FindAssets("t:Scene MatchViewer")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith("MatchViewer.unity"));

        if (string.IsNullOrEmpty(scenePath))
        {
            EditorUtility.DisplayDialog("Build", "MatchViewer.unity bulunamad�.", "OK");
            return;
        }

        // Mevcut sahne listesini sakla
        var original = EditorBuildSettings.scenes;

        try
        {
            // WebGL i�in sadece viewer sahnesini ge�ici sahne listesi olarak ayarla
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };

            // Haf�za / semboller
            PlayerSettings.WebGL.memorySize = 768; // 512�1024 aras�
            var group = BuildTargetGroup.WebGL;
            var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
            if (!defines.Contains("REPLAY_VIEWER"))
                PlayerSettings.SetScriptingDefineSymbolsForGroup(group, (defines + ";REPLAY_VIEWER").Trim(';'));

            // Build
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                target = BuildTarget.WebGL,
                locationPathName = "WebGLBuild/match-viewer"
            };

            BuildReport report = BuildPipeline.BuildPlayer(opts);
            EditorUtility.DisplayDialog("Build", $"Sonu�: {report.summary.result}\n{report.summary.outputPath}", "OK");
        }
        finally
        {
            // Orijinal sahne listesine geri d�n
            EditorBuildSettings.scenes = original;
        }
    }
}
