using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.IO;
using FStudio.Events;
using FStudio.MatchEngine;
using FStudio.MatchEngine.Events;
using FStudio.UI.MatchThemes.MatchEvents;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared.Responses;
using UnityEngine;
using FStudio;
using FStudio.Database;
using FStudio.MatchEngine.Enums;
using FStudio.Data;
using FStudio.UI.Utilities;

namespace FStudio.WebGL
{
    /// <summary>
    /// Bridge between Web (React) and Unity WebGL build.
    /// - Receives match JSON and starts the match
    /// - Dispatches match results back to JS via a WebGL plugin function
    ///
    /// Auto-instantiated at runtime and kept across scenes.
    /// </summary>
    public class MatchBridge : MonoBehaviour
    {
        public static MatchBridge Instance { get; private set; }

        [Serializable]
        private class BridgeTeamDefinition
        {
            public string name;
            public string[] players;   // 11 names in order (GK -> FW)
            public string formation;   // optional, e.g., "FourThreeThree"

            // Optional quick kit switch override for this team
            public bool? altKit;

            // Optional runtime kit definitions
            public BridgeKitDefinition homeKit;
            public BridgeKitDefinition awayKit;

            // Optional extended player objects coming from React as "playersData"
            // We don't attempt to strongly type attributes here; LoadSquadsFromJSON path
            // already handles rich per-player objects. This is only to allow ShowTeamsFromJSON
            // to consume the same payload shape if provided.
            public Newtonsoft.Json.Linq.JObject[] playersData;
        }

        [Serializable]
        private class BridgeKitDefinition
        {
            // Accept hex ("#RRGGBB[AA]") or numbers "r,g,b[,a]" where r,g,b in 0..255 or 0..1
            public string color1;
            public string color2;
            public string textColor;

            public string gkColor1;
            public string gkColor2;
            public string gkTextColor;
        }

        [Serializable]
        private class BridgeTeamsRequest
        {
            public BridgeTeamDefinition home;
            public BridgeTeamDefinition away;

            public bool autoStart = false;

            public string aiLevel;       // optional
            public string userTeam;      // optional
            public string dayTime;       // optional

            // Optional: if autoStart==false, control Team Selection screen behavior
            public bool? openMenu;       // default true
            public bool? select;         // default true
        }

        [Serializable]
        private class BridgeMatchRequest
        {
            public string matchId;
            public string homeTeamKey;   // e.g., "Istanbul" (Resources/Database/Istanbul.asset)
            public string awayTeamKey;   // e.g., "London"
            public bool autoStart = true;

            public string aiLevel;       // e.g., "Legendary"
            public string userTeam;      // "None", "Home", "Away"
            public string dayTime;       // e.g., "Night"

            // Optional quick kit switches: false => Home kit, true => Away kit
            public bool? homeAltKit;
            public bool? awayAltKit;
        }

        [Serializable]
        private class BridgeMatchResult
        {
            public string matchId;
            public string homeTeam;
            public string awayTeam;
            public int homeGoals;
            public int awayGoals;
            public List<string> scorers;
        }

        private string currentMatchId;
        private readonly List<string> scorerNames = new List<string>();

        // Optional preference that React side can set before sending a request
        private MatchCreateRequest.UserTeam preferredUserTeam = MatchCreateRequest.UserTeam.None;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureInstance()
        {
            if (Instance != null) return;
            var go = new GameObject("MatchBridge");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<MatchBridge>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

#if UNITY_WEBGL && !UNITY_EDITOR
            try { PublishMatchBridgeAPI(); } catch { }
#endif
        }

        private void OnEnable()
        {
            EventManager.Subscribe<GoalScoredEvent>(OnGoalScored);
            EventManager.Subscribe<FinalWhistleEvent>(OnFinalWhistle);
        }

        private void OnDisable()
        {
            EventManager.UnSubscribe<GoalScoredEvent>(OnGoalScored);
            EventManager.UnSubscribe<FinalWhistleEvent>(OnFinalWhistle);
        }

        /// <summary>
        /// React -> Unity: update the preferred human-controlled team.
        /// Accepts: "Home", "Away" or "None" (case-insensitive).
        /// Stored and used as default for subsequent requests if not provided in JSON.
        /// </summary>
        public void SelectUserTeam(string side)
        {
            try
            {
                if (Enum.TryParse<MatchCreateRequest.UserTeam>(side, true, out var ut))
                {
                    preferredUserTeam = ut;
                    Debug.Log("[MatchBridge] Preferred user team set to: " + preferredUserTeam);
                }
                else
                {
                    Debug.LogWarning("[MatchBridge] SelectUserTeam invalid value: " + side);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[MatchBridge] SelectUserTeam exception: " + ex);
            }
        }

        // React -> Unity: hide the quick overlay if visible
        public void HideOverlay(string _)
        {
            try { SquadOverlay.Hide(); } catch { }
        }

        // React -> Unity: receive only team names and rosters.
        // If autoStart==true -> open Upcoming Match UI and start flow.
        // Else -> push into Team Selection screen (database list) and preselect.
        public async void ShowTeamsFromJSON(string json)
        {
            try
            {
                Debug.Log("[MatchBridge] Teams JSON received: " + json);
                BridgeTeamsRequest req = null;
                if (!TryDeserializeFirst(json, out req))
                {
                    try
                    {
                        req = JsonConvert.DeserializeObject<BridgeTeamsRequest>(json);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[MatchBridge] ShowTeamsFromJSON: primary deserialization failed: " + ex.Message);
                    }
                }

                // Fallback: accept loose inputs like "nameA vs nameB" or variants
                if (req?.home == null || req.away == null)
                {
                    req = CoerceTeamsFromAny(json);
                }
                if (req?.home == null || req.away == null)
                {
                    Debug.LogError("[MatchBridge] Invalid teams payload after coercion.");
                    return;
                }
                Debug.Log($"[MatchBridge] Incoming HOME players: {string.Join(", ", req.home?.players ?? Array.Empty<string>())}");
                Debug.Log($"[MatchBridge] Incoming AWAY players: {string.Join(", ", req.away?.players ?? Array.Empty<string>())}");
                Debug.Log($"[MatchBridge] Incoming HOME playersData count: {(req.home?.playersData?.Length ?? 0)}");
                Debug.Log($"[MatchBridge] Incoming AWAY playersData count: {(req.away?.playersData?.Length ?? 0)}");

                // Log parsed request at a glance
                Debug.Log($"[MatchBridge] Parsed HOME name='{req.home.name}', formation='{req.home.formation}', players={(req.home.players==null?0:req.home.players.Length)}");
                Debug.Log($"[MatchBridge] Parsed AWAY name='{req.away.name}', formation='{req.away.formation}', players={(req.away.players==null?0:req.away.players.Length)}");

                var homeTeam = CreateRuntimeTeam(req.home);
                var awayTeam = CreateRuntimeTeam(req.away);

                // Log created teams summary
                Debug.Log($"[MatchBridge] Created HOME team='{homeTeam.TeamName}', formation='{homeTeam.Formation}', players={homeTeam.Players?.Length}");
                Debug.Log($"[MatchBridge] Created AWAY team='{awayTeam.TeamName}', formation='{awayTeam.Formation}', players={awayTeam.Players?.Length}");

                scorerNames.Clear();

                // Overlay: show names immediately
                try { SquadOverlay.Show(homeTeam, awayTeam); } catch { }

                // If autoStart is false or omitted, update selection carousel instead of opening pre-match UI
                if (!(req.autoStart))
                {
                    // Respect optional openMenu/select flags when not auto-starting
                    bool openMenu = req.openMenu ?? true;
                    bool doSelect = req.select ?? true;
                    PublishTeamsToSelection(homeTeam, awayTeam, openMenu: openMenu, select: doSelect);
                    return;
                }

                // autoStart -> Prepare a match request and open upcoming match panel
                var matchRequest = new MatchCreateRequest(homeTeam, awayTeam);
                try { Debug.Log($"[MatchBridge] matchRequest: HOME='{homeTeam.TeamName}' players={homeTeam.Players?.Length} formation='{homeTeam.Formation}', AWAY='{awayTeam.TeamName}' players={awayTeam.Players?.Length} formation='{awayTeam.Formation}'"); } catch { }

                if (Enum.TryParse<AILevel>(req.aiLevel, true, out var ai)) matchRequest.aiLevel = ai;
                if (Enum.TryParse<MatchCreateRequest.UserTeam>(req.userTeam, true, out var ut)) matchRequest.userTeam = ut; else matchRequest.userTeam = preferredUserTeam;
                if (Enum.TryParse<DayTimes>(req.dayTime, true, out var dt)) matchRequest.dayTime = dt;

                Debug.Log("[MatchBridge] Opening Upcoming Match UI with provided teams (autoStart=true)...");
                await MatchEngineLoader.CreateMatch(matchRequest);
                Debug.Log("[MatchBridge] Upcoming Match UI should now be visible.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[MatchBridge] ShowTeamsFromJSON exception: " + ex);
            }
        }

        /// <summary>
        /// Attempts to coerce various loose inputs into a BridgeTeamsRequest.
        /// Supports:
        /// - Raw string like "madosov4 vs adosov13" (any case, extra spaces)
        /// - JSON string {"teams":"a vs b"}
        /// - JSON string ["a", "b"]
        /// - JSON object with string properties {"home":"a", "away":"b"}
        /// - JSON object with nested {"home": {"name":"a"}, "away": {"name":"b"}}
        /// - JSON object with keys {"homeTeamKey":"a", "awayTeamKey":"b"}
        /// Returns null if it can't resolve two team names.
        /// </summary>
        private static BridgeTeamsRequest CoerceTeamsFromAny(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            // 1) Try parse strict JSON token first
            try
            {
                var tok = JToken.Parse(input);

                // a) Array form: ["home","away"]
                if (tok is JArray arr && arr.Count >= 2)
                {
                    var a = arr[0]?.ToString();
                    var b = arr[1]?.ToString();
                    return BuildTeamsRequestFromNames(a, b);
                }

                // b) Object forms
                if (tok is JObject obj)
                {
                    // { teams: "a vs b" } or { title: "a vs b" }
                    var pair = obj.Value<string>("teams") ?? obj.Value<string>("title");
                    if (!string.IsNullOrWhiteSpace(pair))
                    {
                        var (a, b) = SplitPair(pair);
                        if (!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b))
                            return BuildTeamsRequestFromNames(a, b);
                    }

                    // { home: "a", away: "b" }
                    var homeStr = obj.Value<string>("home") ?? obj.Value<string>("homeTeamKey");
                    var awayStr = obj.Value<string>("away") ?? obj.Value<string>("awayTeamKey");

                    // { home: { name:"a" }, away: { name:"b" } }
                    if (string.IsNullOrWhiteSpace(homeStr) && obj["home"] is JObject ho)
                        homeStr = ho.Value<string>("name") ?? ho.Value<string>("team") ?? ho.Value<string>("title");
                    if (string.IsNullOrWhiteSpace(awayStr) && obj["away"] is JObject ao)
                        awayStr = ao.Value<string>("name") ?? ao.Value<string>("team") ?? ao.Value<string>("title");

                    if (!string.IsNullOrWhiteSpace(homeStr) && !string.IsNullOrWhiteSpace(awayStr))
                        return BuildTeamsRequestFromNames(homeStr, awayStr);
                }

                // c) Token is a JSON string: "a vs b"
                if (tok.Type == JTokenType.String)
                {
                    var s = tok.Value<string>();
                    var (a, b) = SplitPair(s);
                    if (!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b))
                        return BuildTeamsRequestFromNames(a, b);
                }
            }
            catch
            {
                // Not JSON; treat as raw e.g., madosov4 vs adosov13
                var (a, b) = SplitPair(input);
                if (!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b))
                    return BuildTeamsRequestFromNames(a, b);
            }

            return null;
        }

        private static (string a, string b) SplitPair(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return (null, null);
            // Normalize separators: vs, VS, v, -, |, ×, —, –
            var sepCandidates = new string[] { " vs ", " VS ", " Vs ", " v ", "-", "|", "×", "—", "–", ":" };
            foreach (var sep in sepCandidates)
            {
                var idx = s.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    var left = s.Substring(0, idx);
                    var right = s.Substring(idx + sep.Length);
                    return (SanitizeName(left), SanitizeName(right));
                }
            }
            // As a last resort, split on whitespace blocks
            var parts = s.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var left = string.Join(" ", parts, 0, parts.Length / 2);
                var right = string.Join(" ", parts, parts.Length / 2, parts.Length - parts.Length / 2);
                return (SanitizeName(left), SanitizeName(right));
            }
            return (null, null);
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var n = name.Trim();
            // Collapse multiple spaces
            n = System.Text.RegularExpressions.Regex.Replace(n, "\n|\r", " ");
            return n;
        }

        private static BridgeTeamsRequest BuildTeamsRequestFromNames(string homeName, string awayName)
        {
            homeName = SanitizeName(homeName);
            awayName = SanitizeName(awayName);
            if (string.IsNullOrWhiteSpace(homeName) || string.IsNullOrWhiteSpace(awayName)) return null;

            return new BridgeTeamsRequest
            {
                home = new BridgeTeamDefinition { name = homeName },
                away = new BridgeTeamDefinition { name = awayName },
                autoStart = false,
            };
        }

        private async void PublishTeamsToSelection(TeamEntry homeTeam, TeamEntry awayTeam, bool openMenu, bool select)
        {
            try
            {
                // Merge runtime teams into database
                var current = DatabaseService.LoadTeams();
                var list = new List<TeamEntry>(current.Length + 2);
                foreach (var t in current)
                {
                    if (t == null) continue;
                    if (string.Equals(t.TeamName, homeTeam.TeamName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(t.TeamName, awayTeam.TeamName, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(t);
                }
                list.Add(homeTeam);
                list.Add(awayTeam);
                var mergedTeams = list.ToArray();
                DatabaseService.SetTeams(mergedTeams);

                if (openMenu)
                {
                    FStudio.Events.EventManager.Trigger(new FStudio.UI.Events.MainMenuEvent());
                }

                if (select)
                {
                    TeamSelectionTeam homeSel = null, awaySel = null;
                    for (int i = 0; i < 200; i++)
                    {
                        homeSel = FindSelectionTeam("HomeTeamSelection");
                        awaySel = FindSelectionTeam("AwayTeamSelection");
                        if (homeSel != null && awaySel != null) break;
                        await Task.Delay(10);
                    }

                    // Ensure UI TeamSelectionTeam components refresh their internal 'teams' from DatabaseService
                    // Some menus are instantiated after SetTeams; re-emit change now that widgets exist
                    DatabaseService.SetTeams(mergedTeams);
                    await Task.Delay(20);

                    var teams = DatabaseService.LoadTeams();
                    int homeIndex = FindTeamIndex(teams, homeTeam.TeamName);
                    int awayIndex = FindTeamIndex(teams, awayTeam.TeamName);

                    if (homeSel == null || awaySel == null)
                    {
                        // Fallback: try find by type if names fail
                        var all = GameObject.FindObjectsOfType<TeamSelectionTeam>(true);
                        if (all != null && all.Length >= 2)
                        {
                            System.Array.Sort(all, (a,b)=> a.transform.position.x.CompareTo(b.transform.position.x));
                            homeSel = all[0];
                            awaySel = all[all.Length-1];
                        }
                    }

                    if (homeSel != null && homeIndex >= 0) homeSel.SetTeam(homeIndex);
                    if (awaySel != null && awayIndex >= 0) awaySel.SetTeam(awayIndex);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[MatchBridge] PublishTeamsToSelection error: " + ex);
            }
        }

        private static TeamEntry CreateRuntimeTeam(BridgeTeamDefinition def)
        {
            // Create a transient team ScriptableObject instance for runtime only
            var team = ScriptableObject.CreateInstance<TeamEntry>();
            team.TeamName = string.IsNullOrWhiteSpace(def.name) ? "Team" : def.name.Trim();
            team.name = team.TeamName;

            if (TryResolveFormation(def.formation, out var form, out var normalizedValue, out var aliased))
            {
                team.Formation = form;
                if (aliased)
                {
                    Debug.Log($"[MatchBridge] Formation '{def.formation}' mapped to '{normalizedValue}' -> {team.Formation} for '{team.TeamName}'");
                }
                else
                {
                    Debug.Log($"[MatchBridge] Formation parsed for '{team.TeamName}': {team.Formation}");
                }
            }
            else
            {
                Debug.LogWarning($"[MatchBridge] Formation could not be parsed for '{team.TeamName}'. Using default: {team.Formation}");
            }

            // Runtime kits with safe defaults; allow overrides from payload
            var defaultHome1 = new Color(0.13f, 0.36f, 0.78f);
            var defaultHome2 = new Color(0.95f, 0.95f, 0.95f);
            var defaultAway1 = new Color(0.80f, 0.12f, 0.15f);
            var defaultAway2 = new Color(0.95f, 0.95f, 0.95f);
            team.HomeKit = CreateRuntimeKit(defaultHome1, defaultHome2);
            team.AwayKit = CreateRuntimeKit(defaultAway1, defaultAway2);

            if (def.homeKit != null)
            {
                ApplyKitFromDefinition(team.HomeKit, def.homeKit, defaultHome1, defaultHome2);
            }
            if (def.awayKit != null)
            {
                ApplyKitFromDefinition(team.AwayKit, def.awayKit, defaultAway1, defaultAway2);
            }

            // Runtime logo so UI materials do not break
            var logo = ScriptableObject.CreateInstance<LogoEntry>();
            logo.name = team.TeamName + " Logo";
            logo.TeamLogoMaterial = Texture2D.whiteTexture;
            logo.TeamLogoColor1 = new Color(0.85f, 0.85f, 0.85f);
            logo.TeamLogoColor2 = new Color(0.25f, 0.25f, 0.25f);
            team.TeamLogo = logo;

            // Build player list (11 entries). Prefer provided names array; enrich stats from playersData if present
            var names = def.players ?? Array.Empty<string>();
            var players = new PlayerEntry[11];
            if (names.Length != 11)
            {
                Debug.LogWarning($"[MatchBridge] '{team.TeamName}' players count={names.Length}. Expected 11. Will auto-fill missing.");
            }

            // Index playersData by name for quick lookup
            Dictionary<string, JObject> dataByName = null;
            if (def.playersData != null && def.playersData.Length > 0)
            {
                dataByName = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                foreach (var jo in def.playersData)
                {
                    if (jo == null) continue;
                    var n = jo.Value<string>("name");
                    if (!string.IsNullOrWhiteSpace(n) && !dataByName.ContainsKey(n.Trim()))
                    {
                        dataByName[n.Trim()] = jo;
                    }
                }
            }

            for (int i = 0; i < players.Length; i++)
            {
                var p = ScriptableObject.CreateInstance<PlayerEntry>();
                p.team = team;
                string pname = i < names.Length && !string.IsNullOrWhiteSpace(names[i]) ? names[i].Trim() : $"Player {i + 1}";
                p.Name = pname;
                p.name = pname;

                // Default baseline
                SetDefaultStats(p);

                // Enrich from playersData by name if available
                if (dataByName != null && dataByName.TryGetValue(pname, out var pod))
                {
                    try { ApplyStatsFromObject(p, pod); } catch { }
                }

                players[i] = p;
            }
            team.Players = players;
            LogTeamRoster(team, $"runtime-def:{team.TeamName}");

            // Log the final player list (joined)
            try
            {
                var joined = string.Join(", ", System.Array.ConvertAll(players, x => x?.Name ?? "?"));
                Debug.Log($"[MatchBridge] '{team.TeamName}' lineup: {joined}");
            }
            catch { }

            return team;
        }

        private static KitEntry CreateRuntimeKit(Color c1, Color c2)
        {
            var kit = ScriptableObject.CreateInstance<KitEntry>();
            kit.Color1 = c1;
            kit.Color2 = c2;
            kit.TextColor = Color.black;
            kit.GKColor1 = new Color(0.10f, 0.65f, 0.35f);
            kit.GKColor2 = new Color(0.90f, 0.90f, 0.90f);
            kit.GKTextColor = Color.black;
            // Ensure non-null textures to avoid UI material issues in WebGL
            kit.PreviewTexture = Texture2D.whiteTexture;
            kit.KitMaterial = Texture2D.whiteTexture;
            return kit;
        }

        private static void ApplyKitFromDefinition(KitEntry kit, BridgeKitDefinition def, Color fallback1, Color fallback2)
        {
            if (kit == null || def == null) return;
            kit.Color1 = ParseColorOrDefault(def.color1, fallback1);
            kit.Color2 = ParseColorOrDefault(def.color2, fallback2);
            kit.TextColor = ParseColorOrDefault(def.textColor, Color.black);
            kit.GKColor1 = ParseColorOrDefault(def.gkColor1, new Color(0.10f, 0.65f, 0.35f));
            kit.GKColor2 = ParseColorOrDefault(def.gkColor2, new Color(0.90f, 0.90f, 0.90f));
            kit.GKTextColor = ParseColorOrDefault(def.gkTextColor, Color.black);
        }

        // React -> Unity entrypoint
        public async void LoadMatchFromJSON(string json)
        {
            try
            {
                Debug.Log("[MatchBridge] JSON received: " + json);
                BridgeMatchRequest req = null;
                if (!TryDeserializeFirst(json, out req))
                {
                    try
                    {
                        req = JsonConvert.DeserializeObject<BridgeMatchRequest>(json);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("[MatchBridge] Invalid JSON payload. " + ex.Message);
                        return;
                    }
                }

                if (req == null)
                {
                    Debug.LogError("[MatchBridge] Invalid JSON payload.");
                    return;
                }

                currentMatchId = string.IsNullOrEmpty(req.matchId) ? Guid.NewGuid().ToString("N") : req.matchId;

                // Load TeamEntry assets from Resources
                var homeTeam = LoadTeamByKey(req.homeTeamKey);
                var awayTeam = LoadTeamByKey(req.awayTeamKey);

                if (homeTeam == null || awayTeam == null)
                {
                    Debug.LogError($"[MatchBridge] Could not find teams. Home='{req.homeTeamKey}' Away='{req.awayTeamKey}'.");
                    return;
                }

                var matchRequest = new MatchCreateRequest(homeTeam, awayTeam);

                // Optional overrides
                if (Enum.TryParse<AILevel>(req.aiLevel, true, out var ai))
                {
                    matchRequest.aiLevel = ai;
                }

                if (Enum.TryParse<MatchCreateRequest.UserTeam>(req.userTeam, true, out var ut))
                {
                    matchRequest.userTeam = ut;
                }

                if (Enum.TryParse<DayTimes>(req.dayTime, true, out var dt))
                {
                    matchRequest.dayTime = dt;
                }

                // Clear prior scorers list
                scorerNames.Clear();

                // Ensure the names are visible even on menu screens
                try { SquadOverlay.Show(homeTeam, awayTeam); } catch { }

                // Decide whether to autostart or just open upcoming match UI
                bool homeAlt = req.homeAltKit ?? false; // false => Home kit
                bool awayAlt = req.awayAltKit ?? true;  // true  => Away kit

                if (req.autoStart)
                {
                    // Start match immediately
                    var ev = new UpcomingMatchEvent(matchRequest);

                    // Ensure loader exists; give one frame to boot systems
                    await Task.Yield();
                    if (MatchEngineLoader.Current == null)
                    {
                        Debug.LogWarning("[MatchBridge] MatchEngineLoader not found. Attempting to proceed after short delay...");
                        await Task.Delay(50);
                    }

                    if (MatchEngineLoader.Current != null)
                    {
                        await MatchEngineLoader.Current.StartMatchEngine(ev, homeAlt, awayAlt);
                    }
                    else
                    {
                        // Fallback: open pre-match UI (user can start manually)
                        await MatchEngineLoader.CreateMatch(matchRequest);
                    }
                }
                else
                {
                    // Only open the upcoming match UI
                    await MatchEngineLoader.CreateMatch(matchRequest);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[MatchBridge] LoadMatchFromJSON exception: " + ex);
            }
        }

        /// <summary>
        /// React -> Unity: Receive full squads with per-player stats and create runtime TeamEntry/PlayerEntry objects.
        /// This accepts a flexible JSON shape. Expected example:
        /// {
        ///   "home": { "name":"Istanbul", "formation":"_4_5_2", "players":[ {"name":"GK Name", "stats":{"TopSpeed":0.82, "Acceleration":0.61, ...} }, ...11 ] },
        ///   "away": { ... },
        ///   "autoStart": false, "aiLevel":"Legendary", "userTeam":"Home", "dayTime":"Night"
        /// }
        /// - Stats can be 0..1 or 0..100. They will be scaled/clamped to 0..100.
        /// - Alternate stat keys are supported (e.g., "Accel" for Acceleration, "Control" for BallControl, etc.).
        /// </summary>
        public async void LoadSquadsFromJSON(string json)
        {
            try
            {
                Debug.Log("[MatchBridge] Squad JSON received.");
                JObject root = null;
                if (!TryDeserializeFirst(json, out root))
                {
                    try
                    {
                        root = JObject.Parse(json);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("[MatchBridge] LoadSquadsFromJSON: invalid JSON payload. " + ex.Message);
                        return;
                    }
                }

                if (root == null)
                {
                    Debug.LogError("[MatchBridge] LoadSquadsFromJSON: JSON did not contain an object root.");
                    return;
                }

                var homeObj = root["home"] as JObject;
                var awayObj = root["away"] as JObject;
                if (homeObj == null || awayObj == null)
                {
                    Debug.LogError("[MatchBridge] LoadSquadsFromJSON: Missing 'home' or 'away'.");
                    return;
                }
                Debug.Log($"[MatchBridge] LoadSquadsFromJSON raw HOME players: {DescribePlayersToken(homeObj?["players"])}");
                Debug.Log($"[MatchBridge] LoadSquadsFromJSON raw AWAY players: {DescribePlayersToken(awayObj?["players"])}");

                var homeTeam = CreateRuntimeTeamFromJObject(homeObj);
                LogTeamRoster(homeTeam, "squads-home");
                var awayTeam = CreateRuntimeTeamFromJObject(awayObj);
                LogTeamRoster(awayTeam, "squads-away");

                var matchRequest = new MatchCreateRequest(homeTeam, awayTeam);

                var aiLevelStr = root.Value<string>("aiLevel");
                var userTeamStr = root.Value<string>("userTeam");
                var dayTimeStr = root.Value<string>("dayTime");
                var autoStart = root.Value<bool?>("autoStart") ?? false;

                if (!string.IsNullOrEmpty(aiLevelStr) && Enum.TryParse<AILevel>(aiLevelStr, true, out var ai))
                {
                    matchRequest.aiLevel = ai;
                }

                if (!string.IsNullOrEmpty(userTeamStr) && Enum.TryParse<MatchCreateRequest.UserTeam>(userTeamStr, true, out var ut))
                {
                    matchRequest.userTeam = ut;
                }
                else
                {
                    matchRequest.userTeam = preferredUserTeam;
                }

                if (!string.IsNullOrEmpty(dayTimeStr) && Enum.TryParse<DayTimes>(dayTimeStr, true, out var dt))
                {
                    matchRequest.dayTime = dt;
                }

                scorerNames.Clear();

                var homeAlt = homeObj.Value<bool?>("altKit") ?? false;
                var awayAlt = awayObj.Value<bool?>("altKit") ?? true;

                if (autoStart)
                {
                    var ev = new UpcomingMatchEvent(matchRequest);
                    await Task.Yield();
                    if (MatchEngineLoader.Current != null)
                    {
                        await MatchEngineLoader.Current.StartMatchEngine(ev, homeAlt, awayAlt);
                    }
                    else
                    {
                        await MatchEngineLoader.CreateMatch(matchRequest);
                    }
                }
                else
                {
                    await MatchEngineLoader.CreateMatch(matchRequest);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[MatchBridge] LoadSquadsFromJSON exception: " + ex);
            }
        }

        /// <summary>
        /// React -> Unity: Publish runtime HOME and AWAY teams into DatabaseService so they appear in Team Selection screen.
        /// Accepts same payload as ShowTeamsFromJSON. Options: { home:{...}, away:{...}, openMenu:true, select:true }
        /// If openMenu==true, opens main menu and selects these teams on left/right widgets.
        /// </summary>
        public async void LoadTeamsToSelectionFromJSON(string json)
        {
            try
            {
                JObject root = null;
                if (!TryDeserializeFirst(json, out root))
                {
                    try
                    {
                        root = JObject.Parse(json);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("[MatchBridge] LoadTeamsToSelectionFromJSON: invalid JSON payload. " + ex.Message);
                        return;
                    }
                }

                if (root == null)
                {
                    Debug.LogError("[MatchBridge] LoadTeamsToSelectionFromJSON: JSON did not contain an object root.");
                    return;
                }

                var homeObj = root["home"] as JObject;
                var awayObj = root["away"] as JObject;
                if (homeObj == null || awayObj == null)
                {
                    Debug.LogError("[MatchBridge] LoadTeamsToSelectionFromJSON: Missing 'home' or 'away'.");
                    return;
                }
                Debug.Log($"[MatchBridge] LoadTeamsToSelectionFromJSON raw HOME players: {DescribePlayersToken(homeObj?["players"])}");
                Debug.Log($"[MatchBridge] LoadTeamsToSelectionFromJSON raw AWAY players: {DescribePlayersToken(awayObj?["players"])}");

                var homeTeam = CreateRuntimeTeamFromJObject(homeObj);
                LogTeamRoster(homeTeam, "selection-home");
                var awayTeam = CreateRuntimeTeamFromJObject(awayObj);
                LogTeamRoster(awayTeam, "selection-away");

                // Update quick overlay too
                try { SquadOverlay.Show(homeTeam, awayTeam); } catch { }

                // Merge into database list (replace by team name)
                var current = DatabaseService.LoadTeams();
                var list = new List<TeamEntry>(current.Length + 2);
                foreach (var t in current)
                {
                    if (t == null) continue;
                    if (string.Equals(t.TeamName, homeTeam.TeamName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(t.TeamName, awayTeam.TeamName, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(t);
                }
                list.Add(homeTeam);
                list.Add(awayTeam);
                var mergedTeams = list.ToArray();
                DatabaseService.SetTeams(mergedTeams);

                var openMenu = root.Value<bool?>("openMenu") ?? true;
                var doSelect = root.Value<bool?>("select") ?? true;
                if (openMenu)
                {
                    FStudio.Events.EventManager.Trigger(new FStudio.UI.Events.MainMenuEvent());
                }

                if (doSelect)
                {
                    TeamSelectionTeam homeSel = null, awaySel = null;
                    for (int i = 0; i < 200; i++)
                    {
                        homeSel = FindSelectionTeam("HomeTeamSelection");
                        awaySel = FindSelectionTeam("AwayTeamSelection");
                        if (homeSel != null && awaySel != null) break;
                        await Task.Delay(10);
                    }

                    // Ensure UI components refresh to mergedTeams
                    DatabaseService.SetTeams(mergedTeams);
                    await Task.Delay(20);

                    var teams = DatabaseService.LoadTeams();
                    int homeIndex = FindTeamIndex(teams, homeTeam.TeamName);
                    int awayIndex = FindTeamIndex(teams, awayTeam.TeamName);

                    if (homeSel != null && homeIndex >= 0) homeSel.SetTeam(homeIndex);
                    if (awaySel != null && awayIndex >= 0) awaySel.SetTeam(awayIndex);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[MatchBridge] LoadTeamsToSelectionFromJSON exception: " + ex);
            }
        }

        /// <summary>
        /// React -> Unity: Open Main Menu and preselect teams on the Team Selection screen by key/name.
        /// Payload example: { "homeTeamKey":"Istanbul", "awayTeamKey":"London", "userTeam":"Home", "openMenu":true }
        /// </summary>
        public async void PreselectMenuFromJSON(string json)
        {
            try
            {
                Debug.Log("[MatchBridge] PreselectMenuFromJSON received.");
                JObject root = null;
                if (!TryDeserializeFirst(json, out root))
                {
                    try
                    {
                        root = JObject.Parse(json);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("[MatchBridge] PreselectMenuFromJSON: invalid JSON payload. " + ex.Message);
                        return;
                    }
                }

                if (root == null)
                {
                    Debug.LogError("[MatchBridge] PreselectMenuFromJSON: JSON did not contain an object root.");
                    return;
                }

                var homeKey = root.Value<string>("homeTeamKey") ?? root.Value<string>("home");
                var awayKey = root.Value<string>("awayTeamKey") ?? root.Value<string>("away");
                var openMenu = root.Value<bool?>("openMenu") ?? true;
                var userTeamStr = root.Value<string>("userTeam");

                // Map to Database indices
                var teams = DatabaseService.LoadTeams();
                int homeIndex = FindTeamIndex(teams, homeKey);
                int awayIndex = FindTeamIndex(teams, awayKey);

                if (openMenu)
                {
                    FStudio.Events.EventManager.Trigger(new FStudio.UI.Events.MainMenuEvent());
                }

                // Wait until objects appear
                TeamSelectionTeam homeSel = null, awaySel = null;
                for (int i = 0; i < 200; i++) // ~2 seconds max
                {
                    homeSel = FindSelectionTeam("HomeTeamSelection");
                    awaySel = FindSelectionTeam("AwayTeamSelection");
                    if (homeSel != null && awaySel != null) break;
                    await Task.Delay(10);
                }

                if (homeSel == null || awaySel == null)
                {
                    // Fallback: try find by type if names fail
                    var all = GameObject.FindObjectsOfType<TeamSelectionTeam>(true);
                    if (all != null && all.Length >= 2)
                    {
                        // Heuristic: pick by X position (left=home, right=away)
                        System.Array.Sort(all, (a,b)=> a.transform.position.x.CompareTo(b.transform.position.x));
                        homeSel = all[0];
                        awaySel = all[all.Length-1];
                    }
                }

                if (homeSel != null && homeIndex >= 0)
                {
                    homeSel.SetTeam(homeIndex);
                    Debug.Log($"[MatchBridge] Preselected home index {homeIndex} for '{homeKey}'.");
                }
                else
                {
                    Debug.LogWarning($"[MatchBridge] Could not preselect home team for key='{homeKey}'.");
                }

                if (awaySel != null && awayIndex >= 0)
                {
                    awaySel.SetTeam(awayIndex);
                    Debug.Log($"[MatchBridge] Preselected away index {awayIndex} for '{awayKey}'.");
                }
                else
                {
                    Debug.LogWarning($"[MatchBridge] Could not preselect away team for key='{awayKey}'.");
                }

                // Optionally set default side preference for MatchSettingsPanel via PlayerPrefs
                if (!string.IsNullOrWhiteSpace(userTeamStr) && Enum.TryParse<MatchCreateRequest.UserTeam>(userTeamStr, true, out var ut))
                {
                    // MatchSettingsPanel reads PlayerPrefs with key SETTING_SIDE on Awake
                    PlayerPrefs.SetInt("SETTING_SIDE", (int)ut);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[MatchBridge] PreselectMenuFromJSON exception: " + ex);
            }
        }

        private static TeamSelectionTeam FindSelectionTeam(string name)
        {
            var go = GameObject.Find(name);
            if (go == null) return null;
            return go.GetComponent<TeamSelectionTeam>();
        }

        private static int FindTeamIndex(TeamEntry[] teams, string key)
        {
            if (teams == null || string.IsNullOrWhiteSpace(key)) return -1;
            for (int i = 0; i < teams.Length; i++)
            {
                var t = teams[i];
                if (t == null) continue;
                if (string.Equals(t.TeamName, key, StringComparison.OrdinalIgnoreCase)) return i;
                if (string.Equals(t.name, key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private static TeamEntry LoadTeamByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            // Try direct Resources path: Resources/Database/<key>.asset
            var te = Resources.Load<TeamEntry>("Database/" + key);
            if (te != null)
            {
                Debug.Log($"[MatchBridge] Found TeamEntry in Resources: '{key}'");
                return te;
            }

            // Fallback: search loaded teams by name or asset name
            foreach (var t in DatabaseService.LoadTeams())
            {
                if (string.Equals(t.TeamName, key, StringComparison.OrdinalIgnoreCase)) return t;
                if (string.Equals(t.name, key, StringComparison.OrdinalIgnoreCase)) return t;
            }
            Debug.LogWarning($"[MatchBridge] TeamEntry not found for key='{key}'.");
            return null;
        }

        private static TeamEntry CreateRuntimeTeamFromJObject(JObject teamObj)
        {
            var name = teamObj.Value<string>("name");
            var formationStr = teamObj.Value<string>("formation");

            var team = ScriptableObject.CreateInstance<TeamEntry>();
            team.TeamName = string.IsNullOrWhiteSpace(name) ? "Team" : name.Trim();
            team.name = team.TeamName;
            var defaultHome1 = new Color(0.13f, 0.36f, 0.78f);
            var defaultHome2 = new Color(0.95f, 0.95f, 0.95f);
            var defaultAway1 = new Color(0.80f, 0.12f, 0.15f);
            var defaultAway2 = new Color(0.95f, 0.95f, 0.95f);
            team.HomeKit = CreateRuntimeKit(defaultHome1, defaultHome2);
            team.AwayKit = CreateRuntimeKit(defaultAway1, defaultAway2);

            var logo = ScriptableObject.CreateInstance<LogoEntry>();
            logo.name = team.TeamName + " Logo";
            logo.TeamLogoMaterial = Texture2D.whiteTexture;
            logo.TeamLogoColor1 = new Color(0.85f, 0.85f, 0.85f);
            logo.TeamLogoColor2 = new Color(0.25f, 0.25f, 0.25f);
            team.TeamLogo = logo;

            // Formation parsing with normalization
            if (TryResolveFormation(formationStr, out var formation, out var normalized, out var aliased))
            {
                team.Formation = formation;
                if (aliased)
                {
                    Debug.Log($"[MatchBridge] Formation '{formationStr}' mapped to '{normalized}' -> {team.Formation} for '{team.TeamName}'");
                }
            }

            // Optional kit overrides via nested objects
            TryApplyKitFromJObject(teamObj, "homeKit", team.HomeKit, defaultHome1, defaultHome2);
            TryApplyKitFromJObject(teamObj, "awayKit", team.AwayKit, defaultAway1, defaultAway2);

            // Players: accept either
            // - players: [string|object,...]
            // - playersData: [{ name, attributes|stats: {...} }, ...]
            var playersToken = teamObj["players"] as JArray; // may be null
            var playersDataToken = teamObj["playersData"] as JArray; // may be null

            // Index playersData by name for fast match
            Dictionary<string, JObject> dataByName = null;
            if (playersDataToken != null && playersDataToken.Count > 0)
            {
                dataByName = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                foreach (var jt in playersDataToken)
                {
                    if (jt is JObject jo)
                    {
                        var n = jo.Value<string>("name");
                        if (!string.IsNullOrWhiteSpace(n) && !dataByName.ContainsKey(n.Trim()))
                            dataByName[n.Trim()] = jo;
                    }
                }
            }

            var players = new PlayerEntry[11];
            for (int i = 0; i < players.Length; i++)
            {
                var p = ScriptableObject.CreateInstance<PlayerEntry>();
                p.team = team;

                bool filled = false;

                if (playersToken != null && i < playersToken.Count)
                {
                    var it = playersToken[i];
                    if (it.Type == JTokenType.String)
                    {
                        var pname = it.Value<string>();
                        p.Name = string.IsNullOrWhiteSpace(pname) ? ($"Player {i + 1}") : pname.Trim();
                        p.name = p.Name;
                        SetDefaultStats(p);
                        if (dataByName != null && dataByName.TryGetValue(p.Name, out var pod))
                        {
                            try { ApplyStatsFromObject(p, pod); } catch { }
                        }
                        filled = true;
                    }
                    else if (it is JObject po)
                    {
                        p.Name = po.Value<string>("name") ?? ($"Player {i + 1}");
                        p.name = p.Name;
                        ApplyStatsFromObject(p, po);
                        filled = true;
                    }
                }

                if (!filled && playersDataToken != null && i < playersDataToken.Count && playersDataToken[i] is JObject pdo)
                {
                    p.Name = pdo.Value<string>("name") ?? ($"Player {i + 1}");
                    p.name = p.Name;
                    ApplyStatsFromObject(p, pdo);
                    filled = true;
                }

                if (!filled)
                {
                    p.Name = $"Player {i + 1}";
                    p.name = p.Name;
                    SetDefaultStats(p);
                }

                players[i] = p;
            }
            team.Players = players;
            LogTeamRoster(team, $"runtime-json:{team.TeamName}");
            return team;
        }

        private static void TryApplyKitFromJObject(JObject teamObj, string key, KitEntry target, Color fb1, Color fb2)
        {
            try
            {
                var kitObj = teamObj[key] as JObject;
                if (kitObj == null || target == null) return;
                target.Color1 = ParseColorOrDefault(kitObj.Value<string>("color1"), fb1);
                target.Color2 = ParseColorOrDefault(kitObj.Value<string>("color2"), fb2);
                target.TextColor = ParseColorOrDefault(kitObj.Value<string>("textColor"), Color.black);
                target.GKColor1 = ParseColorOrDefault(kitObj.Value<string>("gkColor1"), new Color(0.10f, 0.65f, 0.35f));
                target.GKColor2 = ParseColorOrDefault(kitObj.Value<string>("gkColor2"), new Color(0.90f, 0.90f, 0.90f));
                target.GKTextColor = ParseColorOrDefault(kitObj.Value<string>("gkTextColor"), Color.black);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MatchBridge] TryApplyKitFromJObject error: " + ex.Message);
            }
        }

        private static readonly Dictionary<string, Formations> FormationAliases = new Dictionary<string, Formations>(StringComparer.OrdinalIgnoreCase)
        {
            { "_3_5_3", Formations._3_4_3 },
            { "3_5_3", Formations._3_4_3 },
            { "_353", Formations._3_4_3 },
            { "353", Formations._3_4_3 },
            { "_5_6_0", Formations._5_5_0 },
            { "5_6_0", Formations._5_5_0 },
            { "_560", Formations._5_5_0 },
            { "560", Formations._5_5_0 }
        };

        private static bool TryResolveFormation(string formationValue, out Formations formation, out string normalized, out bool aliasUsed)
        {
            aliasUsed = false;
            normalized = NormalizeFormationString(formationValue);
            if (!string.IsNullOrEmpty(normalized))
            {
                if (Enum.TryParse(normalized, true, out formation))
                {
                    return true;
                }

                if (FormationAliases.TryGetValue(normalized, out formation))
                {
                    aliasUsed = true;
                    return true;
                }

                var compact = normalized.TrimStart('_');
                if (FormationAliases.TryGetValue(compact, out formation))
                {
                    aliasUsed = true;
                    normalized = compact;
                    return true;
                }
            }

            formation = default;
            return false;
        }
        private static void LogTeamRoster(TeamEntry team, string tag)
        {
            if (team == null)
            {
                Debug.LogWarning($"[MatchBridge] LogTeamRoster[{tag}] team is null");
                return;
            }

            var names = team.Players == null
                ? "<no players>"
                : string.Join(", ", team.Players.Select((p, idx) => p == null ? $"#{idx + 1}:<null>" : $"#{idx + 1}:{p.Name}"));

            Debug.Log($"[MatchBridge] Roster[{tag}] '{team.TeamName}' ({team.Formation}) => {names}");
        }
        private static string DescribePlayersToken(JToken token)
        {
            if (token == null) return "<null>";
            if (token is JArray arr)
            {
                var parts = arr.Select((jt, idx) =>
                {
                    if (jt == null || jt.Type == JTokenType.Null) return $"#{idx + 1}:<null>";
                    if (jt.Type == JTokenType.String) return $"#{idx + 1}:{jt.Value<string>()}";
                    if (jt is JObject obj)
                    {
                        var n = obj.Value<string>("name") ?? obj.Value<string>("Name");
                        return $"#{idx + 1}:{(string.IsNullOrWhiteSpace(n) ? obj.ToString(Formatting.None) : n)}";
                    }
                    return $"#{idx + 1}:{jt.ToString(Formatting.None)}";
                });
                return string.Join(", ", parts);
            }
            return token.ToString(Formatting.None);
        }
        private static bool TryDeserializeFirst<T>(string raw, out T result) where T : class
        {
            result = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(raw)))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    reader.FloatParseHandling = FloatParseHandling.Double;
                    reader.SupportMultipleContent = true;
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonToken.Comment) continue;
                        JToken token;
                        try
                        {
                            token = JToken.ReadFrom(reader);
                        }
                        catch (Exception innerEx)
                        {
                            Debug.LogWarning($"[MatchBridge] JSON token read error: {innerEx.Message}");
                            break;
                        }

                        if (token == null) continue;

                        if (token.Type == JTokenType.String)
                        {
                            var inner = token.Value<string>();
                            if (TryDeserializeFirst(inner, out result)) return true;
                            continue;
                        }

                        if (token.Type == JTokenType.Object)
                        {
                            if (typeof(T) == typeof(JObject))
                            {
                                result = token as T;
                            }
                            else
                            {
                                result = token.ToObject<T>();
                            }
                            if (result != null) return true;
                        }
                        else if (token.Type == JTokenType.Array)
                        {
                            foreach (var child in (JArray)token)
                            {
                                if (child is JObject childObj)
                                {
                                    if (typeof(T) == typeof(JObject))
                                    {
                                        result = childObj as T;
                                    }
                                    else
                                    {
                                        result = childObj.ToObject<T>();
                                    }
                                    if (result != null) return true;
                                }
                                else if (child.Type == JTokenType.String)
                                {
                                    if (TryDeserializeFirst(child.Value<string>(), out result)) return true;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MatchBridge] TryDeserializeFirst<{typeof(T).Name}> error: {ex.Message}");
            }
            return false;
        }
        private static string NormalizeFormationString(string formation)
        {
            if (string.IsNullOrWhiteSpace(formation)) return null;
            var f = formation.Trim();
            f = f.Replace('-', '_').Replace(' ', '_');
            if (!f.StartsWith("_")) f = "_" + f;
            return f;
        }

        private static void SetDefaultStats(PlayerEntry p)
        {
            p.strength = 60;
            p.acceleration = 60;
            p.topSpeed = 60;
            p.dribbleSpeed = 60;
            p.jump = 60;
            p.tackling = 60;
            p.ballKeeping = 60;
            p.passing = 60;
            p.longBall = 60;
            p.agility = 60;
            p.shooting = 60;
            p.shootPower = 60;
            p.positioning = 60;
            p.reaction = 60;
            p.ballControl = 60;
        }

        private static void ApplyStatsFromObject(PlayerEntry p, JObject po)
        {
            // Accept either direct fields or nested 'stats' or 'attributes' object
            var stats = po["stats"] as JObject ?? po["attributes"] as JObject ?? po;

            p.strength      = ReadStat(stats, 60, "strength", "Strength");
            p.acceleration  = ReadStat(stats, 60, "acceleration", "Acceleration", "Accel");
            p.topSpeed      = ReadStat(stats, 60, "topSpeed", "TopSpeed", "Speed", "Pace");
            p.dribbleSpeed  = ReadStat(stats, 60, "dribbleSpeed", "DribbleSpeed", "Dribbling");
            p.jump          = ReadStat(stats, 60, "jump", "Jump");
            p.tackling      = ReadStat(stats, 60, "tackling", "Tackling", "Tackle");
            p.ballKeeping   = ReadStat(stats, 60, "ballKeeping", "BallKeeping");
            p.passing       = ReadStat(stats, 60, "passing", "Passing", "Pass");
            p.longBall      = ReadStat(stats, 60, "longBall", "LongBall", "LongPass");
            p.agility       = ReadStat(stats, 60, "agility", "Agility");
            p.shooting      = ReadStat(stats, 60, "shooting", "Shooting");
            p.shootPower    = ReadStat(stats, 60, "shootPower", "ShootPower", "shotPower", "ShotPower", "Power");
            p.positioning   = ReadStat(stats, 60, "positioning", "Positioning");
            p.reaction      = ReadStat(stats, 60, "reaction", "Reaction", "Reactions");
            p.ballControl   = ReadStat(stats, 60, "ballControl", "BallControl", "Control");
        }

        private static int ReadStat(JObject stats, int fallback, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (stats.TryGetValue(k, StringComparison.OrdinalIgnoreCase, out var tok))
                {
                    if (tok != null && tok.Type != JTokenType.Null)
                    {
                        var val = tok.Value<float>();
                        return Scale01Or100(val);
                    }
                }
            }
            return fallback;
        }

        private static int Scale01Or100(float v)
        {
            // If the number looks like 0..1, scale up; otherwise clamp 0..100
            if (v <= 1.5f) v *= 100f;
            return Mathf.Clamp(Mathf.RoundToInt(v), 0, 100);
        }

        private static Color ParseColorOrDefault(string val, Color fallback)
        {
            if (TryParseColor(val, out var c)) return c;
            return fallback;
        }

        private static bool TryParseColor(string val, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(val)) return false;
            val = val.Trim();

            try
            {
                // Hex #RRGGBB or #RRGGBBAA
                if (val.StartsWith("#"))
                {
                    if (ColorUtility.TryParseHtmlString(val, out var hc))
                    {
                        color = hc; return true;
                    }
                }
                else if (val.Contains(","))
                {
                    var parts = val.Split(',');
                    if (parts.Length >= 3)
                    {
                        float[] nums = new float[4] {1,1,1,1};
                        for (int i = 0; i < parts.Length && i < 4; i++)
                        {
                            var s = parts[i].Trim();
                            if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                            {
                                // If values are >1 assume 0..255
                                nums[i] = f > 1.01f ? Mathf.Clamp01(f/255f) : Mathf.Clamp01(f);
                            }
                        }
                        color = new Color(nums[0], nums[1], nums[2], nums.Length>=4?nums[3]:1f);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // Unity -> React: send match result JSON back
        public void SendResultToReact(string resultJson)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { SendMessageToJS(resultJson); } catch (Exception ex) { Debug.LogError("[MatchBridge] SendResultToReact failed: " + ex); }
#else
            Debug.Log("[MatchBridge->JS] " + resultJson);
#endif
        }

        private void OnGoalScored(GoalScoredEvent e)
        {
            if (e.Scorer != null)
            {
                scorerNames.Add(e.Scorer.Name);
            }
        }

        private void OnFinalWhistle(FinalWhistleEvent _)
        {
            try
            {
                var mm = MatchManager.Current;
                var details = MatchManager.CurrentMatchDetails;
                if (mm == null || details == null) return;

                var result = new BridgeMatchResult
                {
                    matchId = currentMatchId,
                    homeTeam = details.homeTeam?.TeamName,
                    awayTeam = details.awayTeam?.TeamName,
                    homeGoals = mm.homeTeamScore,
                    awayGoals = mm.awayTeamScore,
                    scorers = new List<string>(scorerNames),
                };

                var json = JsonConvert.SerializeObject(result);
                SendResultToReact(json);
            }
            catch (Exception ex)
            {
                Debug.LogError("[MatchBridge] OnFinalWhistle error: " + ex);
            }
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void SendMessageToJS(string message);

        [DllImport("__Internal")]
        private static extern void PublishMatchBridgeAPI();
#else
        // Editor/Standalone stub for local testing
        private static void SendMessageToJS(string message) { Debug.Log("[Stub JS] " + message); }
#if UNITY_EDITOR || !UNITY_WEBGL
        private static void PublishMatchBridgeAPI() { }
#endif
#endif
    }
}






















