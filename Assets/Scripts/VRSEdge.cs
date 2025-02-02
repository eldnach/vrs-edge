using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using Unity.Sentis;
using Unity.Sentis.Layers;

public class VRSEdge : ScriptableRendererFeature
{
    [SerializeField] private RenderPassEvent m_InjectionPoint = RenderPassEvent.AfterRenderingPrePasses;
    [SerializeField] private bool m_DebugVRS;
    [SerializeField] private BackendType m_Backend = BackendType.GPUCompute;

    private ModelAsset m_ModelAsset;
    private Model m_Model;
    private Worker m_Worker;

    private VRSGenerationPass m_ScriptablePass;
    private VRSDebugPass m_DebugPass;   

    public override void Create()
    {
        m_Model = ModelLoader.Load(Resources.Load<ModelAsset>("Models/sobel"));
        var graph = new FunctionalGraph();
        var input = graph.AddInput(DataType.Float, new TensorShape(1, 1, 256, 256));
        var output = Functional.Forward(m_Model, input)[0];
        m_Model = graph.Compile(output);      
        m_Worker = new Worker(m_Model, m_Backend);

        m_ScriptablePass = new VRSGenerationPass(m_Worker);
        m_ScriptablePass.renderPassEvent = m_InjectionPoint;

        if(m_DebugVRS){
            m_DebugPass = new VRSDebugPass();
            m_DebugPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
        }

    }

    // Here you can inject one or multiple render passes in the renderer.
    // This method is called when setting up the renderer once per-camera.
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(m_ScriptablePass);
        if(m_DebugVRS){
            renderer.EnqueuePass(m_DebugPass);
        }
    }

        // Create the custom data class that contains the new texture
        public class VRSData : ContextItem {
            public TextureHandle shadingRateTex;
            public TextureHandle sri;
            public TextureHandle edge;

            public override void Reset()
            {
                shadingRateTex = TextureHandle.nullHandle;
                sri = TextureHandle.nullHandle;
                edge = TextureHandle.nullHandle;
            }
        }   
    
    class VRSGenerationPass : ScriptableRenderPass
    {
        private Worker m_Worker;
        private Tensor<float> m_Output;

        private RTHandle m_DepthRT;
        private TextureHandle m_Depth;

        private RTHandle m_EdgeRT;  
        private TextureHandle m_EdgeMask;

        private TextureHandle m_SRIColorMask;
        private TextureHandle m_SRI;
        private Material m_EdgeMaterial;
        private Material m_BlitDepthMaterial;

        public VRSGenerationPass(Worker worker){
            m_Worker = worker;
        }

        // This class stores the data needed by the RenderGraph pass.
        // It is passed as a parameter to the delegate function that executes the RenderGraph pass.
        private class SentisPassData
        {
            public RTHandle m_DepthRT;
            public RTHandle m_EdgeRT;
            public TextureHandle m_DepthMask;
            public TextureHandle m_EdgeMask;
            public Worker m_Worker;
            public Tensor<float> m_Output;
            public Material m_Mat;
        }

        private class RasterPassData
        {
            public Material m_Mat;
            public TextureHandle m_BlitSrc;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) 
        {
            const string passName = "VRS Generation";

            if (!ShadingRateInfo.supportsPerImageTile) {
                Debug.Log("VRS is not supported!");
                return;
            }

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            var vrsData = frameData.Create<VRSData>(); 
            var tileSize = ShadingRateImage.GetAllocTileSize(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);

            VrsLut lut = new VrsLut();
            lut = VrsLut.CreateDefault();

            if (m_BlitDepthMaterial == null) {
                m_BlitDepthMaterial = new Material(Resources.Load<Shader>("Shaders/DepthCopy"));
            }

            if (m_EdgeMaterial == null) {
                m_EdgeMaterial = new Material(Resources.Load<Shader>("Shaders/EdgeVRS"));
                m_EdgeMaterial.SetColor("_ShadingRateColor1x1", lut[ShadingRateFragmentSize.FragmentSize1x1]);
                m_EdgeMaterial.SetColor("_ShadingRateColor2x2", lut[ShadingRateFragmentSize.FragmentSize2x2]);
                m_EdgeMaterial.SetColor("_ShadingRateColor4x4", lut[ShadingRateFragmentSize.FragmentSize4x4]);
            }

            using (var builder = renderGraph.AddRasterRenderPass<RasterPassData>("CopyDepthForSobel", out var passData))
            {

                RenderTextureDescriptor textureProperties = new RenderTextureDescriptor(256, 256, RenderTextureFormat.Default, 0);
                RenderingUtils.ReAllocateHandleIfNeeded(ref m_DepthRT, textureProperties, FilterMode.Bilinear);
                
                TextureHandle depthMask = renderGraph.ImportTexture(m_DepthRT);
                builder.SetRenderAttachment(depthMask, 0);

                builder.AllowPassCulling(false); 
                passData.m_BlitSrc = resourceData.activeDepthTexture;

                builder.UseTexture(resourceData.activeDepthTexture, AccessFlags.Read);

                passData.m_Mat = m_BlitDepthMaterial;

                //Blit
                builder.SetRenderFunc((RasterPassData data, RasterGraphContext context) =>
                {
                    RasterCommandBuffer cmd = context.cmd;
                    Blitter.BlitTexture(cmd, data.m_BlitSrc, new Vector4(1, 1, 0, 0), data.m_Mat, 0);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<SentisPassData>("Sentis Sobel", out var passData))
            {
                passData.m_DepthRT = m_DepthRT; // get temp depth RT handle

                RenderTextureDescriptor textureProperties = new RenderTextureDescriptor(256, 256, RenderTextureFormat.Default, 0);
                RenderingUtils.ReAllocateHandleIfNeeded(ref m_EdgeRT, textureProperties, FilterMode.Point); // allocate memory for edge RT handle
                m_EdgeMask = UniversalRenderer.CreateRenderGraphTexture(renderGraph, textureProperties, "_EdgeMask", false); // create a render graph texture for edge mask

                passData.m_EdgeMask = m_EdgeMask;
                passData.m_EdgeRT = m_EdgeRT;
                passData.m_Worker = m_Worker;
                passData.m_Output = m_Output;
                passData.m_Mat = m_BlitDepthMaterial;
                builder.SetRenderAttachment(m_EdgeMask, 0, AccessFlags.Write);
                
                builder.AllowGlobalStateModification(true);

                 builder.SetRenderFunc((SentisPassData data, RasterGraphContext context) =>
                {
                    Tensor<float> tensor = TextureConverter.ToTensor(passData.m_DepthRT, width: 256, height: 256, channels: 1);
                    passData.m_Worker.Schedule(tensor);
                    passData.m_Output = passData.m_Worker.PeekOutput() as Tensor<float>;

                    TextureTransform settings = new TextureTransform().SetBroadcastChannels(false).SetDimensions(256, 256, 4);
                    TextureConverter.RenderToTexture(passData.m_Output, passData.m_EdgeRT, settings); 

                    RasterCommandBuffer cmd = context.cmd;
                    Blitter.BlitTexture(cmd, passData.m_EdgeRT, new Vector4(1, 1, 0, 0), passData.m_Mat, 0);

                });

                vrsData.edge = passData.m_EdgeMask; 

            }

            using (var builder = renderGraph.AddRasterRenderPass<RasterPassData>("Shading Rate from Edge", out var passData))
            {
                RenderTextureDescriptor textureProperties = new RenderTextureDescriptor(tileSize.x, tileSize.y, RenderTextureFormat.Default, 0);
                m_SRIColorMask = UniversalRenderer.CreateRenderGraphTexture(renderGraph, textureProperties, "_ShadingRateColor", false);

                builder.AllowPassCulling(false); 
                passData.m_BlitSrc = vrsData.edge;
                
                builder.UseTexture(passData.m_BlitSrc, AccessFlags.Read);
                builder.SetRenderAttachment(m_SRIColorMask, 0, AccessFlags.Write);
                vrsData.shadingRateTex = m_SRIColorMask;

                passData.m_Mat = m_EdgeMaterial;

                //Blit
                builder.SetRenderFunc((RasterPassData data, RasterGraphContext context) =>
                {
                    RasterCommandBuffer cmd = context.cmd;
                    Blitter.BlitTexture(cmd, passData.m_BlitSrc, new Vector4(1, 1, 0, 0), data.m_Mat, 0);
                });

                //Create sri target
                RenderTextureDescriptor sriDesc = new RenderTextureDescriptor(tileSize.x, tileSize.y, GraphicsFormat.R8_UInt,
                    GraphicsFormat.None);
                sriDesc.enableRandomWrite = true;
                sriDesc.enableShadingRate = true;
                sriDesc.autoGenerateMips = false;

                m_SRI = UniversalRenderer.CreateRenderGraphTexture(renderGraph, sriDesc, "_SRI", false);
            }

            Vrs.ColorMaskTextureToShadingRateImage(renderGraph, m_SRI, m_SRIColorMask, TextureDimension.Tex2D, true);
            vrsData.sri = m_SRI;

        }

        public void Dispose()
        {
            Debug.Log("Disposing");
            CoreUtils.Destroy(m_EdgeMaterial);
            CoreUtils.Destroy(m_BlitDepthMaterial);
            m_DepthRT.Release();
            m_EdgeRT.Release();
            m_Worker.Dispose();
            m_Output.Dispose();
        }
    }

    class VRSDebugPass : ScriptableRenderPass
    {
        private Material m_Material;
        private RenderPassEvent m_Event;
        private TextureHandle m_SRIColorMask;

        // This class stores the data needed by the RenderGraph pass.
        // It is passed as a parameter to the delegate function that executes the RenderGraph pass.
        private class PassData
        {
            public Material m_Mat;
            public TextureHandle m_Tex;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) 
        {
            const string passName = "VRS Debugging";

            if (!ShadingRateInfo.supportsPerImageTile) return;

            VrsLut lut = new VrsLut();
            lut = VrsLut.CreateDefault();

            if (m_Material == null)
                m_Material = new Material(Resources.Load<Shader>("Shaders/DebugVRS"));

            var vrsData = frameData.Get<VRSData>();

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData))
            {
                builder.AllowPassCulling(false); 
                passData.m_Tex = vrsData.shadingRateTex; 

                builder.UseTexture(passData.m_Tex, AccessFlags.Read);
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0);

                passData.m_Mat = m_Material;

                //Blit
                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    RasterCommandBuffer cmd = context.cmd;
                    Blitter.BlitTexture(cmd, passData.m_Tex, new Vector4(1, 1, 0, 0), data.m_Mat, 0);
                });
            }
        }

        public void Dispose()
        {
            Debug.Log("Disposing");
            CoreUtils.Destroy(m_Material);
        }
    }
 
}