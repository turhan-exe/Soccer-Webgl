using FStudio.Events;
using FStudio.Graphics;
using FStudio.Loaders;
using FStudio.Graphics.TimeOfDay;
using FStudio.UI;
using FStudio.UI.Events;
using FStudio.UI.GamepadInput;
using FStudio.UI.MatchThemes;
using FStudio.Utilities;
using Shared.Responses;
using FStudio.Data;
using System.Threading.Tasks;
using UnityEngine;
using FStudio.MatchEngine.Graphics.GraphicsModes;
using FStudio.Graphics.Cameras;
using FStudio.UI.MatchThemes.MatchEvents;
using FStudio.Utilities;
using FStudio.MatchEngine.Enums;

namespace FStudio.MatchEngine {
    public class MatchEngineLoader : SceneObjectSingleton<MatchEngineLoader> {
        [SerializeField] private SingleAddressableLoader loader;

        private bool isLoading;
        private bool isLoaded;

        public static async Task CreateMatch(MatchCreateRequest matchData) {
            // close all UI.
            EventManager.Trigger(new CloseAllPanelsEvent());

            // clear all snap history.
            SnapManager.Clear();

            // load the match UI
            await UILoader.Current.MatchUILoader.Load();

            // unload the general UI
            UILoader.Current.GeneralUILoader.Unload();

            var upcomingMatchEvent = new UpcomingMatchEvent(matchData);

            EventManager.Trigger(upcomingMatchEvent);
        }

        public async Task StartMatchEngine (
            UpcomingMatchEvent matchEvent,
            bool homeKit,
            bool awayKit) {

            if (isLoading) {
                return;
            }

            if (isLoaded) {
                // unload.
                await UnloadMatch();
            }

            // match kits.
            EventManager.Trigger(
                new MatchKitsEvent(
                homeKit ? matchEvent.details.homeTeam.AwayKit : matchEvent.details.homeTeam.HomeKit,
                awayKit ? matchEvent.details.awayTeam.AwayKit : matchEvent.details.awayTeam.HomeKit));
            //

            isLoading = true;

            // close all UI and show loader
            EventManager.Trigger(new CloseAllPanelsEvent());
            EventManager.Trigger(new BigLoadingEvent());

            try {
                var template = GraphicLoaders.Current;

                // load stadium scene
                StadiumType stadium = StadiumType.SmallStadium;
                Debug.Log("[MatchEngineLoader] Loading stadium...");
                await template.stadiumLoader.LoadStadium(stadium).WithTimeout(30000, "LoadStadium");

                // load match prefab
                Debug.Log("[MatchEngineLoader] Loading match prefab...");
                await loader.Load().WithTimeout(30000, "MatchPrefab");

                if (TimeOfDaySystem.Current != null) {
                    Debug.Log("[MatchEngineLoader] Loading time of day...");
                    await TimeOfDaySystem.Current.LoadTemplate(matchEvent.details.dayTime).WithTimeout(20000, "TimeOfDay");
                }

                MainCamera.Current.Camera.cullingMask = template.renderLayer;

                // skybox mode on.
                MainCamera.Current.Camera.clearFlags = CameraClearFlags.Skybox;

                Debug.Log("[MatchEngineLoader] Creating core match...");

                await MatchManager.CreateMatch(
                    new MatchManager.MatchDetails(
                        matchEvent,
                        homeKit,
                        awayKit)
                    ).WithTimeout(30000, "CreateMatch");

                Debug.Log("[MatchEngineLoader] Loading ball...");
                await template.ballLoader.LoadRandomBall().WithTimeout(15000, "LoadBall");

                isLoaded = true;
                Debug.Log("[MatchEngineLoader] Done.");
            } catch (System.Exception ex) {
                Debug.LogError($"[MatchEngineLoader] StartMatchEngine failed: {ex}");
            } finally {
                isLoading = false;
                // Always close loading panel to avoid permanent freeze
                EventManager.Trigger<BigLoadingEvent>(null);
            }
        }

        public async Task UnloadMatch () {
            if (MatchManager.Current == null) {
                Debug.LogWarning($"Match is not loaded to unload.");
                return;
            }

            // skybox mode off.
            MainCamera.Current.Camera.clearFlags = CameraClearFlags.SolidColor;

            // close all UI.
            EventManager.Trigger(new CloseAllPanelsEvent());

            var template = GraphicLoaders.Current;

            // unload ball & stadium.
            template.ballLoader.UnloadBall();
            template.stadiumLoader.Unload();
            // 

            SnapManager.Clear();

            UILoader.Current.MatchUILoader.Unload();

            MatchManager.Current.ClearMatch(); // clear field.

            loader.Unload(); // clear match manager prefab.

            await UILoader.Current.GeneralUILoader.Load();

            GameInput.SwitchToUI();

            isLoaded = false;
        }
    }
}
