using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Composites every active Portal's render texture directly into the main camera's own
// color/depth buffer, stencil-clipped to the portal's silhouette (see
// Assets/Shaders/PortalComposite.shader), instead of showing it via a textured quad in
// the normal render queue. Runs after opaques (so real geometry already in the depth
// buffer correctly occludes portals behind it) and before the pipeline's single
// post-processing pass, so the destination room is graded/tonemapped exactly once,
// together with the rest of the frame - no separate camera's post stack touches it.
//
// SETUP (one-time, per Renderer asset used by a build target - eg. PC_Renderer,
// Mobile_Renderer): select the Universal Renderer Data asset in the Project window,
// then in its Inspector click "Add Renderer Feature" and choose "Portal Composite
// Feature". Nothing else to configure - it finds Custom/PortalComposite itself.
public class PortalCompositeFeature : ScriptableRendererFeature
{
    class CompositePass : ScriptableRenderPass
    {
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly int StencilRefId = Shader.PropertyToID("_StencilRef");
        const int StencilRef = 32;

        readonly Material material;
        readonly MaterialPropertyBlock mpb;

        public CompositePass(Material material)
        {
            this.material = material;
            mpb = new MaterialPropertyBlock();
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }

        // Without this, the pass has no declared write target - URP's Render Graph
        // compatibility wrapper then has nothing to hang the pass's Execute() on and
        // silently culls it as unused, which is why nothing was rendering at all.
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ConfigureTarget(renderingData.cameraData.renderer.cameraColorTargetHandle,
                renderingData.cameraData.renderer.cameraDepthTargetHandle);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (material == null) return;
            if (Portal.ActivePortals.Count == 0) return;

            Camera camera = renderingData.cameraData.camera;
            if (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView) return;
            if (Portal.PortalCameras.Contains(camera)) return;

            CommandBuffer cmd = CommandBufferPool.Get("Portal Composite");
            material.SetInt(StencilRefId, StencilRef);

            foreach (Portal portal in Portal.ActivePortals)
            {
                if (!portal.IsRenderReady) continue;

                Mesh mesh = portal.SurfaceMesh;
                if (mesh == null) continue;

                Matrix4x4 matrix = portal.SurfaceMatrix;

                cmd.DrawMesh(mesh, matrix, material, 0, 0); // Pass 0 = Mask

                mpb.Clear();
                mpb.SetTexture(MainTexId, portal.PortalRenderTexture);
                cmd.DrawMesh(mesh, matrix, material, 0, 1, mpb); // Pass 1 = Composite
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    Material material;
    CompositePass pass;

    public override void Create()
    {
        Shader shader = Shader.Find("Custom/PortalComposite");
        if (shader == null)
        {
            Debug.LogWarning("[PortalCompositeFeature] Could not find shader 'Custom/PortalComposite'. Portals will not render.");
            return;
        }

        material = CoreUtils.CreateEngineMaterial(shader);
        pass = new CompositePass(material);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (pass == null) return;
        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing)
    {
        if (material != null) CoreUtils.Destroy(material);
    }
}
