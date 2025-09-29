using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using FStudio.Database;
using FStudio.Events;
using FStudio.MatchEngine;
using FStudio.MatchEngine.Enums;
using FStudio.MatchEngine.Events;
using FStudio.UI.MatchThemes.MatchEvents;
using Shared.Responses;
using FStudio.Data;
using FStudio;
using Newtonsoft.Json.Linq;
using System.Globalization;

public class Bridge : MonoBehaviour
{
    [Serializable]
    public class PlayerDto
    {
        public string id;
        public string name;
        public string pos;
        public float overall;
    }

    [Serializable]
    public class TeamsDto
    {
        public string matchId;
        public string homeTeamKey;
        public string awayTeamKey;
        public List<PlayerDto> home;
        public List<PlayerDto> away;
        public string formationHome;
        public string formationAway;
        public bool autoStart;
        public string aiLevel;
        public string userTeam;
        public string dayTime;
        public bool homeUseAwayKit;
        public bool awayUseAwayKit;
    }

    [Serializable]
    private class MatchResultDto
    {
        public string matchId;
        public string homeTeam;
        public string awayTeam;
        public int homeScore;
        public int awayScore;
        public List<string> scorers;
    }

    private const string DefaultHomeTeamKey = "Istanbul";
    private const string DefaultAwayTeamKey = "London";

    private TeamEntry runtimeHomeTeam;
    private TeamEntry runtimeAwayTeam;

    private string activeHomeKey = DefaultHomeTeamKey;
    private string activeAwayKey = DefaultAwayTeamKey;

    private MatchCreateRequest.UserTeam requestedUserTeam = MatchCreateRequest.UserTeam.None;
    private AILevel requestedAiLevel = AILevel.Legendary;
    private DayTimes requestedDayTime = DayTimes.Night;

    private bool useHomeAwayKit;
    private bool useAwayAwayKit = true;

    private bool isStartingMatch;
    private bool matchInProgress;

    private int homeGoals;
    private int awayGoals;
    private readonly List<string> scorerNames = new List<string>();

    private string currentMatchId;

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

    public void ApplyTeams(string json)
    {
        try
        {
            var data = ParseTeamsPayload(json);
            if (data == null)
            {
                Debug.LogWarning("Bridge.ApplyTeams: JSON parse null");
                return;
            }

            activeHomeKey = string.IsNullOrWhiteSpace(data.homeTeamKey) ? DefaultHomeTeamKey : data.homeTeamKey;
            activeAwayKey = string.IsNullOrWhiteSpace(data.awayTeamKey) ? DefaultAwayTeamKey : data.awayTeamKey;

            runtimeHomeTeam = CreateRuntimeTeam(activeHomeKey);
            if (runtimeHomeTeam == null)
            {
                if (!string.Equals(activeHomeKey, DefaultHomeTeamKey, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning($"Bridge.ApplyTeams: home team '{activeHomeKey}' not found. Falling back to '{DefaultHomeTeamKey}'.");
                }
                activeHomeKey = DefaultHomeTeamKey;
                runtimeHomeTeam = CreateRuntimeTeam(activeHomeKey);
            }

            runtimeAwayTeam = CreateRuntimeTeam(activeAwayKey);
            if (runtimeAwayTeam == null)
            {
                if (!string.Equals(activeAwayKey, DefaultAwayTeamKey, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning($"Bridge.ApplyTeams: away team '{activeAwayKey}' not found. Falling back to '{DefaultAwayTeamKey}'.");
                }
                activeAwayKey = DefaultAwayTeamKey;
                runtimeAwayTeam = CreateRuntimeTeam(activeAwayKey);
            }
            ApplyPlayersToTeam(runtimeHomeTeam, data.home, 0);
            ApplyPlayersToTeam(runtimeAwayTeam, data.away, 100);

            ApplyFormation(runtimeHomeTeam, data.formationHome);
            ApplyFormation(runtimeAwayTeam, data.formationAway);

            if (runtimeHomeTeam != null)
            {
                if (!string.IsNullOrWhiteSpace(data.homeDisplayName))
                {
                    runtimeHomeTeam.TeamName = data.homeDisplayName;
                }
                else if (!string.IsNullOrWhiteSpace(data.homeTeamKey))
                {
                    runtimeHomeTeam.TeamName = data.homeTeamKey;
                }
            }

            if (runtimeAwayTeam != null)
            {
                if (!string.IsNullOrWhiteSpace(data.awayDisplayName))
                {
                    runtimeAwayTeam.TeamName = data.awayDisplayName;
                }
                else if (!string.IsNullOrWhiteSpace(data.awayTeamKey))
                {
                    runtimeAwayTeam.TeamName = data.awayTeamKey;
                }
            }

            if (runtimeHomeTeam == null || runtimeAwayTeam == null)
            {
                Debug.LogError($"Bridge.ApplyTeams: Teams not found. home='{activeHomeKey}' away='{activeAwayKey}'");
                return;
            }

useHomeAwayKit = ReadBooleanWithDefault(json, "homeUseAwayKit", data.homeUseAwayKit, false);
            useAwayAwayKit = ReadBooleanWithDefault(json, "awayUseAwayKit", data.awayUseAwayKit, true);

            requestedAiLevel = ParseEnum(data.aiLevel, AILevel.Legendary);
            requestedUserTeam = ParseEnum(data.userTeam, MatchCreateRequest.UserTeam.None);
            requestedDayTime = ParseEnum(data.dayTime, DayTimes.Night);

            currentMatchId = string.IsNullOrWhiteSpace(data.matchId) ? Guid.NewGuid().ToString("N") : data.matchId;

            matchInProgress = false;
            homeGoals = 0;
            awayGoals = 0;
            scorerNames.Clear();

            try
            {
                FStudio.WebGL.SquadOverlay.Show(runtimeHomeTeam, runtimeAwayTeam);
            }
            catch (Exception overlayEx)
            {
                Debug.LogWarning($"Bridge.ApplyTeams: SquadOverlay.Show failed {overlayEx}");
            }

            Debug.Log($"Bridge.ApplyTeams: homeTeam={runtimeHomeTeam.TeamName} awayTeam={runtimeAwayTeam.TeamName} homePlayers={data.home?.Count ?? 0} awayPlayers={data.away?.Count ?? 0}");

            if (data.autoStart)
            {
                StartMatchInternal();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Bridge.ApplyTeams error: {e}");
        }
    }


    private static TeamsDto ParseTeamsPayload(string json)
    {
        TeamsDto dto = null;
        try
        {
            dto = JsonUtility.FromJson<TeamsDto>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Bridge.ParseTeamsPayload JsonUtility error: {ex}");
        }

        if (dto == null)
        {
            dto = new TeamsDto();
        }

        var homeMissing = dto.home == null || dto.home.Count == 0;
        var awayMissing = dto.away == null || dto.away.Count == 0;

        try
        {
            var root = JToken.Parse(json);

            dto.matchId = string.IsNullOrWhiteSpace(dto.matchId) ? ReadString(root, "matchId") : dto.matchId;
            dto.homeTeamKey = string.IsNullOrWhiteSpace(dto.homeTeamKey)
                ? (ReadString(root, "homeTeamKey") ?? ReadString(root, "home.teamKey") ?? ReadString(root, "homeTeam"))
                : dto.homeTeamKey;
            dto.awayTeamKey = string.IsNullOrWhiteSpace(dto.awayTeamKey)
                ? (ReadString(root, "awayTeamKey") ?? ReadString(root, "away.teamKey") ?? ReadString(root, "awayTeam"))
                : dto.awayTeamKey;
            dto.formationHome = string.IsNullOrWhiteSpace(dto.formationHome)
                ? (ReadString(root, "formationHome") ?? ReadString(root, "homeFormation") ?? ReadString(root, "home.formation"))
                : dto.formationHome;
            dto.formationAway = string.IsNullOrWhiteSpace(dto.formationAway)
                ? (ReadString(root, "formationAway") ?? ReadString(root, "awayFormation") ?? ReadString(root, "away.formation"))
                : dto.formationAway;
            dto.aiLevel = string.IsNullOrWhiteSpace(dto.aiLevel) ? ReadString(root, "aiLevel") : dto.aiLevel;
            dto.userTeam = string.IsNullOrWhiteSpace(dto.userTeam) ? ReadString(root, "userTeam") : dto.userTeam;
            dto.dayTime = string.IsNullOrWhiteSpace(dto.dayTime) ? ReadString(root, "dayTime") : dto.dayTime;
            dto.homeDisplayName = string.IsNullOrWhiteSpace(dto.homeDisplayName)
                ? ReadString(root, "home.displayName", "home.teamName", "home.name")
                : dto.homeDisplayName;
            dto.awayDisplayName = string.IsNullOrWhiteSpace(dto.awayDisplayName)
                ? ReadString(root, "away.displayName", "away.teamName", "away.name")
                : dto.awayDisplayName;

            var autoStartToken = ReadToken(root, "autoStart");
            if (autoStartToken != null && autoStartToken.Type != JTokenType.Null)
            {
                dto.autoStart = autoStartToken.Value<bool>();
            }

            var homeKitToken = ReadToken(root, "homeUseAwayKit");
            if (homeKitToken != null && homeKitToken.Type != JTokenType.Null)
            {
                dto.homeUseAwayKit = homeKitToken.Value<bool>();
            }

            var awayKitToken = ReadToken(root, "awayUseAwayKit");
            if (awayKitToken != null && awayKitToken.Type != JTokenType.Null)
            {
                dto.awayUseAwayKit = awayKitToken.Value<bool>();
            }

            if (homeMissing)
            {
                dto.home = ExtractPlayers(root, "home");
            }
            else
            {
                var fallbackHome = ExtractPlayers(root, "home");
                if (fallbackHome != null && fallbackHome.Count >= dto.home.Count)
                {
                    dto.home = fallbackHome;
                }
            }

            if (awayMissing)
            {
                dto.away = ExtractPlayers(root, "away");
            }
            else
            {
                var fallbackAway = ExtractPlayers(root, "away");
                if (fallbackAway != null && fallbackAway.Count >= dto.away.Count)
                {
                    dto.away = fallbackAway;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Bridge.ParseTeamsPayload fallback error: {ex}");
        }

        dto.home ??= new List<PlayerDto>();
        dto.away ??= new List<PlayerDto>();

        if (string.IsNullOrWhiteSpace(dto.homeDisplayName))
        {
            dto.homeDisplayName = dto.homeTeamKey;
        }

        if (string.IsNullOrWhiteSpace(dto.awayDisplayName))
        {
            dto.awayDisplayName = dto.awayTeamKey;
        }

        return dto;
    }

    private static List<PlayerDto> ExtractPlayers(JToken root, string sideKey)
    {
        var players = new List<PlayerDto>();
        if (root == null)
        {
            return players;
        }

        var sideToken = ReadToken(root, sideKey) ?? ReadToken(root, sideKey + "Players") ?? ReadToken(root, sideKey + ".players");
        if (sideToken == null)
        {
            return players;
        }

        void Collect(JToken token)
        {
            if (token == null)
            {
                return;
            }

            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token)
                {
                    var dto = ToPlayerDto(item);
                    if (dto != null)
                    {
                        players.Add(dto);
                    }
                }
                return;
            }

            if (token is JObject nestedObj)
            {
                foreach (var property in nestedObj.Properties())
                {
                    if (property.Value is JArray nestedArray)
                    {
                        Collect(nestedArray);
                    }
                }
            }
        }

        if (sideToken.Type == JTokenType.Array)
        {
            Collect(sideToken);
        }
        else if (sideToken is JObject obj)
        {
            var keys = new[]
            {
                "players","playerList","lineup","lineUp","starters","starting","starting11","startingXI",
                "firstXI","firstEleven","first11","squad","members","list","main","firstTeam"
            };

            foreach (var key in keys)
            {
                var candidate = GetCaseInsensitive(obj, key);
                if (candidate != null)
                {
                    Collect(candidate);
                }
            }

            if (players.Count == 0)
            {
                foreach (var property in obj.Properties())
                {
                    if (property.Value is JArray arr)
                    {
                        Collect(arr);
                    }
                }
            }
        }

        return players;
    }

    private static PlayerDto ToPlayerDto(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return null;
        }

        if (token.Type == JTokenType.String)
        {
            var nameValue = token.Value<string>();
            if (string.IsNullOrWhiteSpace(nameValue))
            {
                return null;
            }

            return new PlayerDto { name = nameValue };
        }

        if (token is JObject obj)
        {
            var dto = new PlayerDto
            {
                id = ReadString(obj, "id", "playerId", "uuid", "key"),
                name = ReadString(obj, "name", "playerName", "fullName", "displayName", "nickname", "nickName", "label", "title", "shortName", "player.fullName", "person.name"),
                pos = ReadString(obj, "pos", "position", "role", "positionShort", "positionCode", "shortPosition", "positionAbbr", "position.code", "position.short", "position.abbreviation")
            };

            if (!string.IsNullOrWhiteSpace(dto.name))
            {
                dto.name = dto.name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(dto.pos))
            {
                dto.pos = dto.pos.Trim().ToUpperInvariant();
            }

            var rating = ReadNumber(obj, "overall", "ovr", "rating", "overallScore", "overallRating", "overallNormalized", "ovrNormalized", "score", "value", "ovrDisplay");
            if (rating.HasValue)
            {
                dto.overall = (float)rating.Value;
            }
            else if (dto.overall <= 0f)
            {
                dto.overall = 60f;
            }

            if (string.IsNullOrWhiteSpace(dto.name))
            {
                dto.name = ReadString(obj, "player.name", "profile.name", "player.fullName");
                if (!string.IsNullOrWhiteSpace(dto.name))
                {
                    dto.name = dto.name.Trim();
                }
            }

            return dto;
        }

        return null;
    }

    private static string ReadString(JToken root, params string[] keys)
    {
        foreach (var key in keys)
        {
            var token = ReadToken(root, key);
            if (token == null)
            {
                continue;
            }

            var text = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static double? ReadNumber(JToken root, params string[] keys)
    {
        foreach (var key in keys)
        {
            var token = ReadToken(root, key);
            if (token == null)
            {
                continue;
            }

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                return token.Value<double>();
            }

            if (double.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static JToken ReadToken(JToken token, string path)
    {
        if (token == null || string.IsNullOrEmpty(path))
        {
            return null;
        }

        var parts = path.Split('.');
        return ReadTokenRecursive(token, parts, 0);
    }

    private static JToken ReadTokenRecursive(JToken token, string[] parts, int index)
    {
        if (token == null)
        {
            return null;
        }

        if (index >= parts.Length)
        {
            return token;
        }

        if (token is JObject obj)
        {
            var child = GetCaseInsensitive(obj, parts[index]);
            if (child == null)
            {
                return null;
            }

            return ReadTokenRecursive(child, parts, index + 1);
        }

        if (token is JArray arr)
        {
            foreach (var item in arr)
            {
                var result = ReadTokenRecursive(item, parts, index);
                if (result != null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static JToken GetCaseInsensitive(JObject obj, string key)
    {
        foreach (var property in obj.Properties())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    public void StartMatch(string _)
    {
        StartMatchInternal();
    }

    private async void StartMatchInternal()
    {
        if (isStartingMatch)
        {
            Debug.LogWarning("Bridge.StartMatchInternal: already starting");
            return;
        }

        EnsureRuntimeTeams();

        if (runtimeHomeTeam == null || runtimeAwayTeam == null)
        {
            Debug.LogError("Bridge.StartMatchInternal: team data not ready");
            return;
        }

        isStartingMatch = true;
        matchInProgress = false;
        homeGoals = 0;
        awayGoals = 0;
        scorerNames.Clear();

        try
        {
            var matchRequest = new MatchCreateRequest(runtimeHomeTeam, runtimeAwayTeam);
            matchRequest.aiLevel = requestedAiLevel;
            matchRequest.userTeam = requestedUserTeam;
            matchRequest.dayTime = requestedDayTime;

            var upcoming = new UpcomingMatchEvent(matchRequest);

            await Task.Yield();

            if (MatchEngineLoader.Current != null)
            {
                await MatchEngineLoader.Current.StartMatchEngine(upcoming, useHomeAwayKit, useAwayAwayKit);
                matchInProgress = true;
            }
            else
            {
                Debug.LogWarning("Bridge.StartMatchInternal: MatchEngineLoader.Current missing, calling CreateMatch only");
                await MatchEngineLoader.CreateMatch(matchRequest);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Bridge.StartMatchInternal error: {ex}");
        }
        finally
        {
            isStartingMatch = false;
        }
    }

    private static void ApplyFormation(TeamEntry team, string formationCode)
    {
        if (team == null || string.IsNullOrWhiteSpace(formationCode))
        {
            return;
        }

        var token = formationCode.Trim();
        token = token.Replace("-", "_").Replace(" ", string.Empty);
        if (!token.StartsWith("_"))
        {
            token = "_" + token;
        }

        if (Enum.TryParse<Formations>(token, true, out var formation))
        {
            team.Formation = formation;
        }
    }
    private static void ApplyPlayersToTeam(TeamEntry team, List<PlayerDto> players, int idOffset)
    {
        if (team == null || team.Players == null || players == null)
        {
            return;
        }

        if (players.Count == 0)
        {
            Debug.LogWarning($"Bridge.ApplyPlayersToTeam: No players received for '{team?.TeamName ?? "Unknown"}'. Keeping existing roster.");
            return;
        }

        var target = team.Players;
        var count = Mathf.Min(target.Length, players.Count);
        for (int i = 0; i < count; i++)
        {
            var dto = players[i];
            var entry = target[i];
            if (dto == null || entry == null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(dto.name))
            {
                entry.Name = dto.name.Trim();
            }

            entry.id = idOffset + i;

            ApplyOverallToPlayer(entry, dto.overall);
        }
    }

    private static void ApplyOverallToPlayer(PlayerEntry entry, float overall)
    {
        var value = float.IsNaN(overall) ? 0f : overall;
        if (value >= 0f && value <= 1f)
        {
            value *= 100f;
        }

        var val = Mathf.Clamp(Mathf.RoundToInt(value), 0, 100);
        entry.strength = val;
        entry.acceleration = val;
        entry.topSpeed = val;
        entry.dribbleSpeed = val;
        entry.jump = val;
        entry.tackling = val;
        entry.ballKeeping = val;
        entry.passing = val;
        entry.longBall = val;
        entry.agility = val;
        entry.shooting = val;
        entry.shootPower = val;
        entry.positioning = val;
        entry.reaction = val;
        entry.ballControl = val;
    }

    private void OnGoalScored(GoalScoredEvent e)
    {
        if (!matchInProgress)
        {
            return;
        }

        if (!e.Side)
        {
            homeGoals++;
        }
        else
        {
            awayGoals++;
        }

        if (e.Scorer != null)
        {
            var teamTag = e.Side ? runtimeAwayTeam?.TeamName : runtimeHomeTeam?.TeamName;
            var minuteTag = e.Minute > 0 ? $" ({e.Minute}')" : string.Empty;
            scorerNames.Add($"{teamTag ?? (e.Side ? "Away" : "Home")}: {e.Scorer.Name}{minuteTag}");
        }
    }

    private void OnFinalWhistle(FinalWhistleEvent _)
    {
        if (!matchInProgress)
        {
            return;
        }

        matchInProgress = false;

        var result = new MatchResultDto
        {
            matchId = currentMatchId,
            homeTeam = runtimeHomeTeam?.TeamName ?? activeHomeKey,
            awayTeam = runtimeAwayTeam?.TeamName ?? activeAwayKey,
            homeScore = homeGoals,
            awayScore = awayGoals,
            scorers = new List<string>(scorerNames)
        };

        var resultJson = JsonUtility.ToJson(result);
        SendMessageToJS(resultJson);

        scorerNames.Clear();
        homeGoals = 0;
        awayGoals = 0;

        try
        {
            FStudio.WebGL.SquadOverlay.Hide();
        }
        catch { }
    }

    private void EnsureRuntimeTeams()
    {
        if (runtimeHomeTeam == null)
        {
            runtimeHomeTeam = CreateRuntimeTeam(activeHomeKey);
        }

        if (runtimeAwayTeam == null)
        {
            runtimeAwayTeam = CreateRuntimeTeam(activeAwayKey);
        }
    }

    private static TeamEntry CreateRuntimeTeam(string key)
    {
        var asset = FindTeamAsset(key);
        if (asset == null)
        {
            return null;
        }

        var clone = asset.Clone() as TeamEntry;
        if (clone == null)
        {
            Debug.LogError($"Bridge.CreateRuntimeTeam: cloning failed for '{key}'");
            return null;
        }
        return clone;
    }

    private static TeamEntry FindTeamAsset(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var teams = DatabaseService.LoadTeams();
        foreach (var team in teams)
        {
            if (team == null)
            {
                continue;
            }

            if (string.Equals(team.TeamName, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(team.name, key, StringComparison.OrdinalIgnoreCase))
            {
                return team;
            }
        }

        Debug.LogWarning($"Bridge.FindTeamAsset: no team for '{key}'");
        return null;
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct
    {
        if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out TEnum parsed))
        {
            return parsed;
        }
        return fallback;
    }

    private static bool ReadBooleanWithDefault(string json, string propertyName, bool parsedValue, bool defaultValue)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
        {
            return parsedValue;
        }

        var marker = "\"" + propertyName + "\"";
        return json.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0 ? parsedValue : defaultValue;
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void SendMessageToJS(string msg);
#else
    private static void SendMessageToJS(string msg)
    {
        Debug.Log("[Editor] SendMessageToJS => " + msg);
    }
#endif
}



















