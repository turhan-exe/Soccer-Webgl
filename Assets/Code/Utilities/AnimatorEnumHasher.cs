using UnityEngine;
using System;
using System.Collections.Generic;

namespace FStudio.Utilities {
    public static class AnimatorEnumHasher {
        /// <summary>
        /// Returns a mapping from enum member name to Animator parameter hash.
        /// Ignores sentinel enum members named "ParameterCount" or "Count".
        /// Logs a concise warning if a parameter is missing on the Animator.
        /// </summary>
        public static Dictionary<string, int> GetHashes<T>(Animator animator) where T : Enum {
            var result = new Dictionary<string, int>();

            // Collect existing animator parameter names for validation.
            var existing = new HashSet<string>();
            foreach (var p in animator.parameters) {
                existing.Add(p.name);
            }

            var values = (T[])Enum.GetValues(typeof(T));
            foreach (var value in values) {
                var name = value.ToString();
                if (name == "ParameterCount" || name == "Count") {
                    continue; // ignore sentinel value if present
                }

                var hash = Animator.StringToHash(name);
                result[name] = hash;

                // If the animator doesn't have this parameter, log a single, clear line.
                if (!existing.Contains(name)) {
                    Debug.LogWarning($"[AnimatorEnumHasher] Animator missing parameter '{name}' for enum {typeof(T).Name}.");
                }
            }

            // Extra signal when the overall counts don't line up (non-fatal).
            int enumParamCount = result.Count;
            if (animator.parameterCount != enumParamCount) {
                Debug.LogWarning($"[AnimatorEnumHasher] Parameter count mismatch: Animator={animator.parameterCount}, Enum({typeof(T).Name})={enumParamCount}.");
            }

            return result;
        }
    }
}
