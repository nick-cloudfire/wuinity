using ImGuiNET;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    public static class Themes
    {       
        public static void ApplyAdobeSpectrum(bool darkTheme)
        {
            //Styling needs a live ImGui context, and there is no guaranteed order between this
            //component's OnEnable and the one where UImGui creates that context. Reached too
            //early, ImGui.GetStyle() hands back a pointer to nothing and the first field write
            //throws a NullReferenceException - which then cascades, because the GUI never
            //finishes enabling. Returning quietly is correct here: the caller re-applies the
            //theme from UImGuiUtility.OnInitialize once the context exists.
            if (ImGui.GetCurrentContext() == System.IntPtr.Zero)
            {
                return;
            }

            var style = ImGui.GetStyle();

            // ----- Layout -----
            style.WindowPadding = new Vector2(6f, 6f);
            style.FramePadding = new Vector2(4f, 3f);
            style.ItemSpacing = new Vector2(6f, 6f);
            style.ItemInnerSpacing = new Vector2(4f, 4f);

            style.IndentSpacing = 20f;
            style.ScrollbarSize = 16f;
            style.GrabMinSize = 10f;

            style.WindowBorderSize = 1f;
            style.ChildBorderSize = 1f;
            style.PopupBorderSize = 1f;
            style.FrameBorderSize = 1f;
            style.TabBorderSize = 1f;

            style.WindowRounding = 0f;
            style.ChildRounding = 0f;
            style.FrameRounding = 0f;
            style.PopupRounding = 0f;
            style.ScrollbarRounding = 0f;
            style.GrabRounding = 0f;
            style.TabRounding = 0f;

            //ImGui's own dark and light colours. A Spectrum palette of its own followed each of these, after a
            //return that made it unreachable (and a compiler warning); it is in the history if it is wanted.
            if (darkTheme)
            {
                ImGui.StyleColorsDark();
            }
            else
            {
                ImGui.StyleColorsLight();
            }
        }
    }
}
