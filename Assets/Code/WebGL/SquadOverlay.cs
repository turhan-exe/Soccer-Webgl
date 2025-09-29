using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FStudio.WebGL
{
    /// <summary>
    /// Lightweight on-screen overlay to display React-provided team names and lineups.
    /// Works across scenes; created on first use and updated via MatchBridge.
    /// </summary>
    public class SquadOverlay : MonoBehaviour
    {
        private static SquadOverlay _instance;
        private Canvas _canvas;
        private RectTransform _root;
        private TextMeshProUGUI _homeText;
        private TextMeshProUGUI _awayText;

        public static void Show(FStudio.Database.TeamEntry home, FStudio.Database.TeamEntry away)
        {
            Ensure();
            if (_instance == null) return;

            _instance.gameObject.SetActive(true);

            _instance._homeText.text = BuildText("HOME", home);
            _instance._awayText.text = BuildText("AWAY", away);
        }

        public static void Hide()
        {
            if (_instance != null) _instance.gameObject.SetActive(false);
        }

        private static string BuildText(string title, FStudio.Database.TeamEntry team)
        {
            if (team == null) return title + "\n-";
            var sb = new System.Text.StringBuilder();
            sb.Append(title).Append(": ").Append(team.TeamName ?? "").Append('\n');

            var players = team.Players ?? new FStudio.Database.PlayerEntry[0];
            for (int i = 0; i < players.Length; i++)
            {
                var p = players[i];
                if (p == null) continue;
                sb.Append(i + 1).Append(". ").Append(p.Name ?? "Player").Append('\n');
            }
            return sb.ToString();
        }

        private static void Ensure()
        {
            if (_instance != null) return;

            var go = new GameObject("SquadOverlay");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<SquadOverlay>();
            _instance.BuildUI();
            _instance.gameObject.SetActive(false);
        }

        private void BuildUI()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            gameObject.AddComponent<GraphicRaycaster>();

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 1f; // Prefer height

            _root = gameObject.AddComponent<RectTransform>();

            // Create a semi-transparent background strip at the top
            var bgObj = new GameObject("BG");
            bgObj.transform.SetParent(transform, false);
            var bgRt = bgObj.AddComponent<RectTransform>();
            bgRt.anchorMin = new Vector2(0, 1);
            bgRt.anchorMax = new Vector2(1, 1);
            bgRt.pivot = new Vector2(0.5f, 1);
            bgRt.anchoredPosition = Vector2.zero;
            bgRt.sizeDelta = new Vector2(0, 260);
            var bgImg = bgObj.AddComponent<Image>();
            bgImg.color = new Color(0, 0, 0, 0.35f);

            // Home text (left)
            _homeText = CreateText("HomeText", new Vector2(0, 1), new Vector2(0.5f, 1), new Vector2(0, 1), new Vector2(0, -10));
            _homeText.alignment = TextAlignmentOptions.TopLeft;

            // Away text (right)
            _awayText = CreateText("AwayText", new Vector2(0.5f, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(0, -10));
            _awayText.alignment = TextAlignmentOptions.TopRight;
        }

        private TextMeshProUGUI CreateText(string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 offset)
        {
            var t = new GameObject(name);
            t.transform.SetParent(transform, false);
            var rt = t.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = offset;
            rt.sizeDelta = new Vector2(-40, 240);

            var tmp = t.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = 28f;
            tmp.enableWordWrapping = true;
            tmp.richText = true;
            tmp.color = Color.white;
            if (TMPro.TMP_Settings.instance != null && TMP_Settings.defaultFontAsset != null)
            {
                tmp.font = TMP_Settings.defaultFontAsset;
            }
            tmp.text = "";
            return tmp;
        }
    }
}

