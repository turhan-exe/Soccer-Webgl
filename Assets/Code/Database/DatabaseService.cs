using FStudio.Database;
using System.Linq;
using UnityEngine;
using System;

namespace FStudio {
    public static class DatabaseService {
        private static TeamEntry[] loadedTeams;
        public static event Action TeamsChanged;

        public static TeamEntry[] LoadTeams() {
            if (loadedTeams != null) {
                return loadedTeams;
            }
            var database = Resources.LoadAll("Database");
            loadedTeams = database.
                Where(x => x is TeamEntry).
                Select(x => (TeamEntry)x).ToArray();

            return loadedTeams;
        }

        // Replace the runtime team list (used by WebGL bridge to inject teams coming from React)
        public static void SetTeams(TeamEntry[] teams)
        {
            loadedTeams = teams ?? Array.Empty<TeamEntry>();
            try { TeamsChanged?.Invoke(); } catch { }
        }

        public static void AddOrReplaceTeam(TeamEntry team)
        {
            if (team == null) return;
            var list = LoadTeams().ToList();
            var idx = FindIndexByKey(team.TeamName);
            if (idx >= 0) list[idx] = team; else list.Add(team);
            SetTeams(list.ToArray());
        }

        public static int FindIndexByKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return -1;
            var teams = LoadTeams();
            for (int i = 0; i < teams.Length; i++)
            {
                var t = teams[i];
                if (t == null) continue;
                if (string.Equals(t.TeamName, key, StringComparison.OrdinalIgnoreCase)) return i;
                if (string.Equals(t.name, key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        public static Color RandomColor() {
            var r = UnityEngine.Random.Range(0, 2);
            var g = UnityEngine.Random.Range(0, 2);
            var b = UnityEngine.Random.Range(0, 2);

            return new Color(r, g, b, 1);   
        }
    }
}

