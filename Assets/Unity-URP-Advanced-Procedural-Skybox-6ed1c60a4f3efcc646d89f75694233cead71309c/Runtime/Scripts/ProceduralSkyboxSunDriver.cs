using UnityEngine;

[ExecuteAlways]
public class ProceduralSkyboxSunDriver : MonoBehaviour
{
    [Header("References")]
    public Light sunLight;               // Your Directional Light
    public Material skyboxMaterial;      // Skybox material using the shader

    void Update()
    {
        if (!sunLight || !skyboxMaterial) return;

        // Sun direction (IMPORTANT: inverse forward)
        Vector3 sunDir = -sunLight.transform.forward;

        skyboxMaterial.SetVector("_SunDirection", sunDir);
        skyboxMaterial.SetColor("_SunColor", sunLight.color);
        skyboxMaterial.SetFloat("_SunIntensity", sunLight.intensity);
    }
}
