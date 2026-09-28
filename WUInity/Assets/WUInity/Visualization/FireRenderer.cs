//This file is part of WUIPlatform Copyright (C) 2024 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using UnityEngine;
using PREACT.Input;
using PREACT.Visualization;
using PREACT.Math;
using PREACT;

namespace WUInity.Visualization
{
    public class FireRenderer : MonoBehaviour
    {
        [SerializeField] private Material _fireMaterial;
        [SerializeField] private Material sootMaterial;
        int _fireCellCountX, _fireCellCountY, _smokeCellCountX, _smokeCellCountY;

        ComputeBuffer _fireBuffer, sootBuffer;
        MeshRenderer fireMeshRenderer, sootMeshRenderer;
        float lowerExtCoeff = 0.002608695f; //500 meters with C = 3
        float upperExtCoeff = 0.260869565f; //5 meters with C = 3
        float lowerFirelineIntensityValue = 0.0f;
        float upperFirelineIntensityValue = 6000.0f;

        Texture2D horizontalRandomLegend;

        public bool ToggleFire(PREACTInput input)
        {
            if(input.WildfireModule.Enabled && fireMeshRenderer != null)
            {
                fireMeshRenderer.gameObject.SetActive(!fireMeshRenderer.gameObject.activeSelf);
                return fireMeshRenderer.gameObject.activeSelf;
            }
            else
            {
                return false;
            }
        }

        public bool ToggleSoot(PREACTInput input)
        {
            if(input.SmokeModule.Enabled && sootMeshRenderer != null)
            {
                sootMeshRenderer.gameObject.SetActive(!sootMeshRenderer.gameObject.activeSelf);
                return sootMeshRenderer.gameObject.activeSelf;
            }
            else
            {
                return false;
            }
        }

        public void CreateBuffers(Simulation simulation)
        {
            Release(simulation, true);

            //The previous run's planes. Each run used to add a new FireSpread and SootSpread object and mesh
            //without removing the last, so every earlier run's fire stayed in the scene, still active, under
            //the new one - and the toggles only ever reached the newest.
            DestroyPlane(ref fireMeshRenderer);
            DestroyPlane(ref sootMeshRenderer);

            if (simulation.Input.WildfireModule.Enabled)
            {
                CreateFireBuffer(simulation);
            }
            
            if(simulation.Input.SmokeModule.Enabled)
            {
                CreateSootBuffer(simulation);
            }            
        }

        private void CreateFireBuffer(Simulation simulation)
        {
            _fireCellCountX = simulation.Hazards.Wildfire.GetCellCountX();
            _fireCellCountY = simulation.Hazards.Wildfire.GetCellCountY();
            _fireBuffer = new ComputeBuffer(_fireCellCountX * _fireCellCountY, sizeof(float));
            _fireMaterial.SetInteger("_CellsX", _fireCellCountX);
            _fireMaterial.SetInteger("_CellsY", _fireCellCountY);
            SetFirelineIntensityDisplay();
            fireMeshRenderer = CreateDataPlane(_fireMaterial, "FireSpread", true, simulation);
        }

        private void CreateSootBuffer(Simulation simulation)
        {
            if (simulation.Input.SmokeModule.Module != SmokeInput.SmokeModules.None)
            {
                _smokeCellCountX = simulation.Hazards.Smoke.GetCellsX();
                _smokeCellCountY = simulation.Hazards.Smoke.GetCellsY();
                sootBuffer = new ComputeBuffer(_smokeCellCountX * _smokeCellCountY, sizeof(float));
                sootMaterial.SetInteger("_CellsX", _smokeCellCountX);
                sootMaterial.SetInteger("_CellsY", _smokeCellCountY);
                sootMaterial.SetFloat("_LowerCutOff", 0.0f);
                sootMaterial.SetFloat("_MinValue", lowerExtCoeff); //500 meters with C = 3
                sootMaterial.SetFloat("_MaxValue", upperExtCoeff); //5 meters with C = 3

                sootMeshRenderer = CreateDataPlane(sootMaterial, "SootSpread", true, simulation);

                // GlobalSmoke provides the extinction coefficient directly.
                sootMaterial.SetFloat("_DataMultiplier", 1f);
            }
            else
            {
                Engine.Message(null, Engine.LogType.Warning, "Unsupported smoke module, fire/smoke renderer failed to initialize.");
            }
        }       
        
        //Fireline intensity is the only thing the fire plane has ever been asked to show. The fuel model,
        //arrival time and distance-to-front modes it also offered had no caller; results rasters (arrival,
        //probability, the trigger boundary) are drawn by FireDomainVisualizerUnity.DisplayRaster instead.
        private void SetFirelineIntensityDisplay()
        {
            if (_fireMaterial == null)
            {
                return;
            }

            _fireMaterial.SetFloat("_LowerCutOff", 0.01f);
            _fireMaterial.SetFloat("_MinValue", lowerFirelineIntensityValue);
            _fireMaterial.SetFloat("_MaxValue", upperFirelineIntensityValue);
            _fireMaterial.SetFloat("_DataMultiplier", 1.0f);

            if (horizontalRandomLegend == null)
            {
                horizontalRandomLegend = (Texture2D)_fireMaterial.GetTexture("_ScaleGradient");
            }
            _fireMaterial.SetTexture("_ScaleGradient", horizontalRandomLegend);
        }

        public void UpdateFireRenderer(bool renderFire, bool renderSoot, Simulation simulation)
        {
            if (renderFire)
            {
                float[] fireData = simulation.Hazards.Wildfire.GetFireLineIntensityData();

                if (fireData != null)
                {
                    _fireBuffer.SetData(fireData);
                    _fireMaterial.SetBuffer("_Data", _fireBuffer);
                }                
            }

            if (renderSoot)
            {
                if(simulation.Input.SmokeModule.Module != SmokeInput.SmokeModules.None)
                {
                    //Extinction coefficient, 1/m: what GlobalSmoke's ramp holds and what the overlay has always been
                    //handed, now under its own name.
                    float[] newSoot = simulation.Hazards.Smoke.GetExtinctionCoefficientData();
                    if(newSoot != null)
                    {
                        sootBuffer.SetData(newSoot);
                        sootMaterial.SetBuffer("_Data", sootBuffer);
                    }                              
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Warning, "Unsupported smoke module, fire/smoke renderer failed to initialize.");
                }
            }
        }

        private static void DestroyPlane(ref MeshRenderer plane)
        {
            if (plane == null)
            {
                return;
            }

            MeshFilter filter = plane.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
            {
                Destroy(filter.sharedMesh);
            }
            Destroy(plane.gameObject);
            plane = null;
        }

        MeshRenderer CreateDataPlane(Material material, string name, bool setActive, Simulation simulation)
        {
            GameObject gO = new GameObject(name);
            gO.transform.parent = transform;
            gO.isStatic = true;
            // You can change that line to provide another MeshFilter
            MeshFilter filter = gO.AddComponent<MeshFilter>();
            Mesh mesh = new Mesh(); // filter.mesh;
            filter.mesh = mesh;
            MeshRenderer mR = gO.AddComponent<MeshRenderer>();
            mR.receiveShadows = false;
            mR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mesh.Clear();

            float width;
            float height;
            Vector3 offset;
            Vector2 maxUV = Vector2.one;

            simulation.Hazards.Wildfire.GetOffsetAndSize(out Vector2d offsetFire, out Vector2d size);
            width = (float)size.x;
            height = (float)size.y;             
            offset = new Vector3((float)offsetFire.x, 0f, (float)offsetFire.y);

            VisualizeUtilities.CreateSimplePlane(mesh, width, height, 0.0f, offset);

            mR.material = material;
            //move up one meter
            gO.transform.position += Vector3.up;
            gO.SetActive(setActive);
            return mR;
        }

        void OnDisable()
        {
            SetFirelineIntensityDisplay();
            Release();          
        }

        void OnDestroy()
        {
            Release();
        }

        void Release(Simulation simulation = null, bool creationCall = false)
        {
            if (_fireBuffer != null)
            {
                _fireBuffer.Release();
                _fireBuffer = null;
            }

            if (sootBuffer != null)
            {
                sootBuffer.Release();
                sootBuffer = null;
            }

            // GlobalSmoke has no GPU buffers to release here.
            
        }
    }    
}

