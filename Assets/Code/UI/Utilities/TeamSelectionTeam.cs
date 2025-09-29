
using FStudio.Database;
using FStudio.UI.Graphics;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System;

namespace FStudio.UI.Utilities {
    public class TeamSelectionTeam : MonoBehaviour {
        [SerializeField] private Selector selector;
        [SerializeField] private TextMeshProUGUI teamNameText;
        [SerializeField] private TextMeshProUGUI overallText;
        [SerializeField] private Image teamLogoImage;
        [SerializeField] private ImageFiller overallFiller;

        public TeamEntry SelectedTeam { private set; get; }

        private TeamEntry[] teams;
        private bool initialized;

        private void Awake() {
            RefreshFromDatabase(true);
        }

        private void OnEnable()
        {
            DatabaseService.TeamsChanged += OnTeamsChanged;
        }

        private void OnDisable()
        {
            DatabaseService.TeamsChanged -= OnTeamsChanged;
        }

        private void OnTeamsChanged()
        {
            RefreshFromDatabase(false);
        }

        private void RefreshFromDatabase(bool firstInit)
        {
            teams = DatabaseService.LoadTeams();

            selector.Max = teams.Length;
            selector.OnSelectionUpdate = (value) => { if (teams != null && teams.Length > 0) SetTeam(teams[Mathf.Clamp(value,0,teams.Length-1)]); };

            if (teams.Length == 0)
            {
                teamNameText.text = "";
                overallText.text = "";
                SelectedTeam = null;
                return;
            }

            if (firstInit && !initialized)
            {
                selector.SetSelected(UnityEngine.Random.Range(0, selector.Max));
                initialized = true;
            }
            else
            {
                var current = Mathf.Clamp(selector.CurrentSelected, 0, selector.Max - 1);
                selector.SetSelected(current);
            }
        }

        public void SetTeam (int teamIndex) {
            selector.SetSelected(teamIndex);
        }
        private void SetTeam (TeamEntry teamEntry) {
            SelectedTeam = teamEntry;

            teamNameText.text = teamEntry.TeamName;
            teamLogoImage.material = TeamLogoMaterial.Current.GetColoredMaterial(teamEntry.TeamLogo);

            int rangeMin = 60, rangeMax = 84;
            var clamped = Mathf.Clamp (teamEntry.Overall, rangeMin, rangeMax);
            var fill = (clamped - rangeMin) / 3;
            var result = 0.2f + 0.01f + fill * 0.1f;

            overallFiller.FillTo (result);
            overallText.text = teamEntry.Overall.ToString ();
        }
    }
}
