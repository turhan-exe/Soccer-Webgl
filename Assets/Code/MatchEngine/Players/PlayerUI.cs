using UnityEngine;
using TMPro;
using FStudio.Utilities;
using System.Linq;
using System.Collections.Generic;
using FStudio.Graphics.Cameras;

namespace FStudio.MatchEngine.Players {
    public class PlayerUI : MonoBehaviour {
        public enum UIAnimatorVariable {
            ShowName,
            ShowOffside,
            ParameterCount // Parameter count of the animator.
        }

        private static readonly List<PlayerUI> list = new List<PlayerUI>();
        public static IEnumerable<PlayerUI> Members => list.AsEnumerable ();

        private System.Collections.Generic.Dictionary<string, int> animatorVariableHashes;

        private MainCamera mainCamera;

        [SerializeField] private Animator animator;

        public TextMeshPro nameText = default;

        private void OnEnable() {
            list.Add(this);
        }

        private void OnDisable() {
            list.Remove(this);
        }

        private void Awake () {
            animatorVariableHashes = AnimatorEnumHasher.GetHashes<UIAnimatorVariable>(animator);
        }

        public void SetBool(UIAnimatorVariable prop, bool value) {
            if (!animator.isActiveAndEnabled) {
                return;
            }

            // Use enum name to look up the correct hash instead of relying on dictionary order.
            var key = prop.ToString();
            if (animatorVariableHashes.TryGetValue(key, out var hash)) {
                animator.SetBool(hash, value);
            } else {
                Debug.LogWarning($"[PlayerUI] Animator hash not found for '{key}'.");
            }
        }

        public void SetName (string name) {
            nameText.text = name;
        }

        public void ShowOffside (bool value) {
            SetBool(UIAnimatorVariable.ShowOffside, value);
        }

        public void ShowName (bool value) {
            SetBool(UIAnimatorVariable.ShowName, value);
        }

        private void LateUpdate () {
            if (mainCamera == null) {
                mainCamera = MainCamera.Current;
            }
            
            var toCamera = Quaternion.LookRotation(transform.position - mainCamera.transform.position);
            toCamera.eulerAngles = new Vector3(toCamera.eulerAngles.x, toCamera.eulerAngles.y, 0);
            transform.rotation = toCamera;
        }
    }
}
