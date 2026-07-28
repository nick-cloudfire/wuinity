using ImGuiNET;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{    
    public static class SimulationInputTab
    {
        private static Vector2 _latLon, _domainSize, _newLatLon, _newDomainSize;
        private static bool _redefinePrefilled;

        public static void Draw(PREACT.Input.SimulationInput input)
        {
            ImGui.InputText(nameof(input.Name), ref input.Name, 128);

            //Shown disabled, because these are a readout rather than an input. They are reassigned
            //from the scenario every frame, so anything typed into them was overwritten on the next
            //one and never reached the input - the fields looked editable and silently discarded
            //every edit. Changing the domain goes through "Redefine domain" below, which is
            //deliberate: it moves the whole simulation grid.
            _latLon.x = (float)input.LowerLeftLatLon.x;
            _latLon.y = (float)input.LowerLeftLatLon.y;
            _domainSize.x = (float)input.DomainSize.x;
            _domainSize.y = (float)input.DomainSize.y;

            ImGui.BeginDisabled();
            ImGui.InputFloat2(nameof(input.LowerLeftLatLon), ref _latLon);
            ImGui.InputFloat2(nameof(input.DomainSize), ref _domainSize);
            ImGui.EndDisabled();

            if(ImGui.TreeNode("Redefine domain"))
            {
                //Seeded from the current domain the first time this opens. They used to start at
                //zero, so pressing Apply without typing anything moved the domain to lat/lon 0,0
                //and collapsed its size to nothing.
                if (!_redefinePrefilled)
                {
                    _newLatLon = _latLon;
                    _newDomainSize = _domainSize;
                    _redefinePrefilled = true;
                }

                ImGui.InputFloat2("New " + nameof(input.LowerLeftLatLon), ref _newLatLon);
                ImGui.InputFloat2("New " + nameof(input.DomainSize), ref _newDomainSize);

                //A zero-sized domain is never meaningful and leaves the simulation unusable.
                bool validDomain = _newDomainSize.x > 0f && _newDomainSize.y > 0f;
                ImGui.BeginDisabled(!validDomain);
                if (ImGui.Button("Apply"))
                {
                    input.LowerLeftLatLon = new PREACT.Math.Vector2d(_newLatLon.x, _newLatLon.y);
                    input.DomainSize = new PREACT.Math.Vector2d(_newDomainSize.x, _newDomainSize.y);
                }
                ImGui.EndDisabled();
                if (!validDomain)
                {
                    ImGui.TextDisabled("Domain size must be greater than zero.");
                }

                ImGui.TreePop();
            }
            else
            {
                //Re-seed next time it opens, so it reflects the domain as it stands then.
                _redefinePrefilled = false;
            }

            ImGui.InputFloat(nameof(input.DeltaTime), ref input.DeltaTime);

            CustomTypes.InputDateTimePopup(nameof(input.StartDateTime), ref input.StartDateTime);
            CustomTypes.InputDateTimePopup(nameof(input.EndDateTime), ref input.EndDateTime);

            ImGui.Checkbox(nameof(input.StopWhenEvacuated), ref input.StopWhenEvacuated);
        }

        private static void DrawVector2d(string name, ref PREACT.Math.Vector2d value)
        {
            Vector2 displayValue = new Vector2((float)value.x, (float)value.y);
            ImGui.InputFloat2(name, ref displayValue);
            value.x = displayValue.x;
            value.y = displayValue.y;
        }
    }
}
