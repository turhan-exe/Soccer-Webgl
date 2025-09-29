using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

namespace FStudio.Input
{
    /// <summary>
    /// WebGL does not include XInput. Some UI OnScreen controls may reference
    /// XInput-specific layouts (e.g. "XInputController"). This bootstrap maps
    /// those layout names to the generic Gamepad layout at runtime so device
    /// creation succeeds on WebGL builds.
    /// </summary>
    internal static class WebGLInputLayoutAliases
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterAliases()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                // If XInput layouts are not present on WebGL, alias them to Gamepad.
                if (InputSystem.LoadLayout("XInputController") == null)
                    InputSystem.RegisterLayout<Gamepad>("XInputController");

                if (InputSystem.LoadLayout("XInputControllerWindows") == null)
                    InputSystem.RegisterLayout<Gamepad>("XInputControllerWindows");

                // Some projects reference this shorter alias too.
                if (InputSystem.LoadLayout("XInput") == null)
                    InputSystem.RegisterLayout<Gamepad>("XInput");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[WebGLInputLayoutAliases] Failed to register layout aliases: " + e.Message);
            }
#endif
        }
    }
}

