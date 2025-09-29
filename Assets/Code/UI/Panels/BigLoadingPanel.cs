
using FStudio.UI.Events;
using UnityEngine;
using System.Collections;

namespace FStudio.UI {
    public class BigLoadingPanel : EventPanel<BigLoadingEvent> {
        private Coroutine hideRoutine;

        protected override void OnEventCalled(BigLoadingEvent eventObject) {
            Debug.Log("Big loading : " + (eventObject != null));
            if (eventObject == null) {
                if (hideRoutine != null) {
                    StopCoroutine(hideRoutine);
                }
                hideRoutine = StartCoroutine(HideDelayed(1f));
            } else {
                if (hideRoutine != null) {
                    StopCoroutine(hideRoutine);
                    hideRoutine = null;
                }
                Appear();
            }
        }

        private IEnumerator HideDelayed(float seconds) {
            yield return new WaitForSecondsRealtime(seconds);
            hideRoutine = null;
            Disappear();
        }
    }
}
